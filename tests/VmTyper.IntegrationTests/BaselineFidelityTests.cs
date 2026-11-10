using System.IO;
using System.Text.Json;
using VmTyper.Helpers;
using VmTyper.Models;
using VmTyper.Native;
using VmTyper.Services;
using VmTyper.Tests;
using Xunit.Abstractions;

namespace VmTyper.IntegrationTests;

/// <summary>Só roda com VMTYPER_INTEGRATION=1: injeta teclas de verdade na sessão.</summary>
public sealed class IntegrationFactAttribute : FactAttribute
{
    public const string Variable = "VMTYPER_INTEGRATION";

    public IntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1")
        {
            Skip = $"Injeta teclado real na sessão interativa; defina {Variable}=1 com o desktop desbloqueado e sem mexer no teclado/mouse.";
        }
    }
}

public sealed class IntegrationTheoryAttribute : TheoryAttribute
{
    public IntegrationTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable(IntegrationFactAttribute.Variable) != "1")
        {
            Skip = $"Injeta teclado real na sessão interativa; defina {IntegrationFactAttribute.Variable}=1 com o desktop desbloqueado e sem mexer no teclado/mouse.";
        }
    }
}

/// <summary>
/// Passo 0.7 e Fase 1: digita os corpora no receptor local e verifica fidelidade e TargetGuard.
/// </summary>
[Trait("Category", "Integration")]
public sealed class BaselineFidelityTests
{
    private readonly ITestOutputHelper _output;

    public BaselineFidelityTests(ITestOutputHelper output) => _output = output;

    [IntegrationFact]
    public async Task BasicCorpus_V1Engine_ReachesReceiver()
    {
        string corpus = KeyboardInputService.NormalizeLineEndings(
            await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "corpora", "basico.txt"))).TrimEnd('\n');

        using var receiver = ReceiverProcess.Start(hook: true);
        receiver.Send("CLEAR");
        receiver.Send("ACTIVATE");
        await Task.Delay(500);

        Assert.True(receiver.IsForeground(), $"O receptor não ficou em primeiro plano ({receiver.DescribeForeground()}); nenhuma tecla foi enviada.");

        using var dir = new TempDir();
        var log = TestLog.Create(dir);
        var engine = new KeyboardInputService(
            new KeyboardMappingService(Win32KeyboardLayoutProvider.Instance), log,
            Win32InputInjector.Instance, Win32KeyboardLayoutProvider.Instance);
        var defaults = new AppSettings();
        var options = new TypingOptions
        {
            CharDelayMs = defaults.CharDelayMs,
            EnterDelayMs = defaults.EnterDelayMs,
            Method = InputMethod.TecladoFisico
        };

        await engine.TypeTextAsync(corpus, options, null, PauseToken.None, CancellationToken.None);
        await Task.Delay(500);

        var result = receiver.Export();
        string received = KeyboardInputService.NormalizeLineEndings(result.Text);
        var diff = TextDiff.Describe(corpus, received);

        string outDir = Path.Combine(AppContext.BaseDirectory, "baseline");
        Directory.CreateDirectory(outDir);
        string outFile = Path.Combine(outDir, $"fidelity-f1-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        await File.WriteAllTextAsync(outFile, JsonSerializer.Serialize(new
        {
            engine = "v2-f1",
            options.CharDelayMs,
            options.EnterDelayMs,
            method = options.Method.ToString(),
            expected = corpus,
            received,
            identical = corpus == received,
            diff,
            hookEvents = result.Events.Count,
            injectedEvents = result.Events.Count(e => e.Injected)
        }, new JsonSerializerOptions { WriteIndented = true }));

        _output.WriteLine($"Resultado salvo em {outFile}");
        _output.WriteLine(diff.Count == 0 ? "Texto idêntico." : string.Join(Environment.NewLine, diff));

        Assert.Equal(corpus, received);
    }

    [IntegrationTheory]
    [InlineData("basico.txt", InputMethod.TecladoFisico)]
    [InlineData("basico.txt", InputMethod.Unicode)]
    [InlineData("acentuacao.txt", InputMethod.TecladoFisico)]
    [InlineData("acentuacao.txt", InputMethod.Unicode)]
    [InlineData("teclas-mortas.txt", InputMethod.TecladoFisico)]
    [InlineData("teclas-mortas.txt", InputMethod.Unicode)]
    public async Task Corpus_ReachesReceiverIdentically_InBothModes(string corpusFile, InputMethod method)
    {
        string corpus = KeyboardInputService.NormalizeLineEndings(
            await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "corpora", corpusFile))).TrimEnd('\n');

        using var receiver = ReceiverProcess.Start(hook: true);
        receiver.Send("CLEAR");
        receiver.Send("ACTIVATE");
        await Task.Delay(500);

        Assert.True(receiver.IsForeground(), $"O receptor não ficou em primeiro plano ({receiver.DescribeForeground()}); nenhuma tecla foi enviada.");

        using var dir = new TempDir();
        var log = TestLog.Create(dir);
        var engine = new KeyboardInputService(
            new KeyboardMappingService(Win32KeyboardLayoutProvider.Instance), log,
            Win32InputInjector.Instance, Win32KeyboardLayoutProvider.Instance);
        var options = new TypingOptions
        {
            CharDelayMs = 8,
            EnterDelayMs = 25,
            Method = method,
            TargetLayout = "HostForeground"
        };

        await engine.TypeTextAsync(corpus, options, null, PauseToken.None, CancellationToken.None);
        await Task.Delay(500);

        var result = receiver.Export();
        string received = KeyboardInputService.NormalizeLineEndings(result.Text);
        var diff = TextDiff.Describe(corpus, received);
        _output.WriteLine($"{corpusFile} ({method}): {(diff.Count == 0 ? "idêntico" : string.Join(" | ", diff))}");

        Assert.Equal(corpus, received);
    }

    [IntegrationFact]
    public async Task SecondWindowOpenedMidTyping_PausesSession_AndSecondWindowReceivesZeroChars()
    {
        using var receiver1 = ReceiverProcess.Start(hook: true);
        using var receiver2 = ReceiverProcess.Start(hook: true);

        receiver1.Send("CLEAR");
        receiver2.Send("CLEAR");
        receiver1.Send("ACTIVATE");
        await Task.Delay(500);

        Assert.True(receiver1.IsForeground(), "O receptor 1 não ficou em primeiro plano.");

        using var dir = new TempDir();
        var log = TestLog.Create(dir);
        using var guard = new TargetGuard(Win32ForegroundWindowProvider.Instance, log);
        var anchorRes = guard.TryCaptureAnchor(validateBrowser: false, ExpectedBrowser.Nenhum);
        Assert.True(anchorRes.IsValid, anchorRes.ErrorMessage);

        var pauseSource = new PauseTokenSource();
        using var cts = new CancellationTokenSource();
        TargetLostIncident? lostIncident = null;

        var engine = new KeyboardInputService(
            new KeyboardMappingService(Win32KeyboardLayoutProvider.Instance), log,
            Win32InputInjector.Instance, Win32KeyboardLayoutProvider.Instance);

        var progress = new DirectProgress<TypingProgress>(p =>
        {
            if (p.Sent == 3)
            {
                receiver2.Send("ACTIVATE");
                Thread.Sleep(150);
            }
            var check = guard.CheckBeforeUnit(p.Sent, strictTitleMode: false);
            if (check.Status == TargetCheckStatus.TargetLost)
            {
                lostIncident ??= check.Incident;
                pauseSource.Pause();
            }
        });

        var options = new TypingOptions
        {
            CharDelayMs = 25,
            EnterDelayMs = 25,
            Method = InputMethod.TecladoFisico
        };

        var typingTask = engine.TypeTextAsync("abcdefghij", options, progress, pauseSource.Token, cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!pauseSource.IsPaused && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.True(pauseSource.IsPaused, "A sessão deveria ter pausado quando o segundo receptor assumiu o primeiro plano.");
        Assert.NotNull(lostIncident);

        // Cancela a sessão pausada e aguarda encerramento limpo.
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => typingTask);

        await Task.Delay(300);
        var exp1 = receiver1.Export();
        var exp2 = receiver2.Export();

        Assert.Equal("abc", exp1.Text);
        Assert.Equal(string.Empty, exp2.Text);
    }
}

internal static class TextDiff
{
    /// <summary>Lista as linhas que diferem, com o primeiro ponto de divergência.</summary>
    public static List<string> Describe(string expected, string actual)
    {
        var e = expected.Split('\n');
        var a = actual.Split('\n');
        var result = new List<string>();
        for (int i = 0; i < Math.Max(e.Length, a.Length); i++)
        {
            string el = i < e.Length ? e[i] : "<ausente>";
            string al = i < a.Length ? a[i] : "<ausente>";
            if (el == al) continue;
            int col = 0;
            while (col < el.Length && col < al.Length && el[col] == al[col]) col++;
            result.Add($"linha {i + 1}, coluna {col + 1}: esperado «{el}» recebido «{al}»");
        }
        return result;
    }
}
