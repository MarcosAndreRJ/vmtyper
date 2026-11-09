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

/// <summary>
/// Passo 0.7: digita o corpus básico no receptor local com o motor v1 e
/// compara o que chegou. O resultado é a linha de base de fidelidade.
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

        // Proteção: o motor v1 não monitora o destino (defeito C). Se o receptor não
        // estiver em primeiro plano agora, nada é digitado.
        Assert.True(receiver.IsForeground(), "O receptor não ficou em primeiro plano; nenhuma tecla foi enviada.");

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
        string outFile = Path.Combine(outDir, $"fidelity-v1-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        await File.WriteAllTextAsync(outFile, JsonSerializer.Serialize(new
        {
            engine = "v1",
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

        // A linha de base registra o que acontece; os defeitos conhecidos (N1 etc.)
        // fazem o texto divergir, e isso é o dado, não uma falha do teste.
        Assert.False(string.IsNullOrEmpty(received), "Nada chegou ao receptor.");
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
