using System.IO;
using System.Windows.Threading;
using VmTyper.Helpers;
using VmTyper.Models;
using VmTyper.Native;
using VmTyper.Services;
using VmTyper.Tests.Fakes;

namespace VmTyper.Tests;

/// <summary>
/// Teste que descreve o comportamento CORRETO de um defeito conhecido do
/// motor v1. Fica pulado por padrão, com o identificador do defeito no
/// motivo. Para ver os defeitos reproduzidos (testes vermelhos), rode com
/// a variável de ambiente VMTYPER_RUN_KNOWN=1. Na fase que corrige o
/// defeito, o atributo é trocado por [Fact].
/// </summary>
public sealed class KnownDefectFactAttribute : FactAttribute
{
    public const string RunKnownVariable = "VMTYPER_RUN_KNOWN";

    public KnownDefectFactAttribute(string defectId, string fixedIn = "F1")
    {
        if (Environment.GetEnvironmentVariable(RunKnownVariable) != "1")
        {
            Skip = $"Known defect {defectId} – corrigido na {fixedIn} ({RunKnownVariable}=1 para executar)";
        }
    }
}

/// <summary>Diretório temporário isolado por teste (arquivos de fila, configurações e logs).</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "VmTyperTests", Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    /// <summary>Concatena o conteúdo de todos os arquivos do diretório (recursivo).</summary>
    public string AllContent() => string.Join("\n",
        Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories).Select(System.IO.File.ReadAllText));

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* melhor esforço */ }
    }
}

internal static class TestLog
{
    /// <summary>LogService gravando num diretório temporário. As linhas em memória só chegam pelo Dispatcher do teste; leia o arquivo.</summary>
    public static LogService Create(TempDir dir) =>
        new(Dispatcher.CurrentDispatcher, System.IO.Path.Combine(dir.Path, "logs"));
}

/// <summary>Motor de teclado real ligado ao injetor e ao layout falsos.</summary>
internal sealed class EngineHarness : IDisposable
{
    public TempDir Dir { get; } = new();
    public RecordingInjector Injector { get; } = new();
    public FakeKeyboardLayout Layout { get; }
    public LogService Log { get; }
    public KeyboardInputService Engine { get; }

    public EngineHarness(FakeKeyboardLayout? layout = null)
    {
        Layout = layout ?? FakeKeyboardLayout.Abnt2();
        Log = TestLog.Create(Dir);
        Engine = new KeyboardInputService(new KeyboardMappingService(Layout), Log, Injector, Layout);
    }

    public static TypingOptions Options(
        InputMethod method = InputMethod.TecladoFisico,
        IReadOnlyList<CharacterSubstitution>? substitutions = null,
        bool diagnostic = false,
        int charDelayMs = 0) => new()
    {
        CharDelayMs = charDelayMs,
        EnterDelayMs = 0,
        Method = method,
        DiagnosticMode = diagnostic,
        Substitutions = substitutions ?? Array.Empty<CharacterSubstitution>()
    };

    public Task TypeAsync(string text, TypingOptions? options = null, IProgress<TypingProgress>? progress = null) =>
        Engine.TypeTextAsync(text, options ?? Options(), progress, PauseToken.None, CancellationToken.None);

    public KeyStateAnalysis Analyze() => new(Injector.Delivered, Layout);

    public void Dispose() => Dir.Dispose();
}

/// <summary>
/// Reconstrói o estado das teclas a partir dos eventos entregues. A
/// identidade "lógica" de uma tecla é o scan code + flag estendida: um
/// evento por virtual key é convertido para scan pela tabela do layout, que
/// é o que o Windows faz. A identidade "estrita" também inclui a codificação
/// (scan vs. VK), que é o que um receptor que casa pares pela forma do
/// evento enxerga.
/// </summary>
internal sealed class KeyStateAnalysis
{
    public readonly record struct Key(string Encoding, uint Code);

    public IReadOnlyList<RecordedInput> Events { get; }

    /// <summary>KeyUp lógicos sem KeyDown correspondente pendente.</summary>
    public List<RecordedInput> OrphanKeyUps { get; } = new();

    /// <summary>Teclas lógicas ainda pressionadas ao final.</summary>
    public HashSet<uint> StuckKeys { get; } = new();

    /// <summary>KeyUp que liberou uma tecla pressionada com outra codificação (scan vs. VK).</summary>
    public List<RecordedInput> MismatchedReleases { get; } = new();

    /// <summary>Scan codes lógicos de cada KeyDown de tecla não modificadora, em ordem.</summary>
    public List<uint> MainKeyDowns { get; } = new();

    public KeyStateAnalysis(IReadOnlyList<RecordedInput> events, FakeKeyboardLayout layout)
    {
        Events = events;
        var pressed = new Dictionary<uint, string>();
        var modifierScans = new HashSet<uint>(new[]
        {
            Win32.VK_LSHIFT, Win32.VK_RSHIFT, Win32.VK_LCONTROL, Win32.VK_RCONTROL, Win32.VK_LMENU, Win32.VK_RMENU
        }.Select(vk => LogicalOf(vk, layout)));

        foreach (var e in events)
        {
            if (e.IsUnicode) continue;

            uint logical = e.IsScanCode
                ? (e.IsExtended ? 0xE000u : 0u) | e.Scan
                : LogicalOf(e.Vk, layout);
            string encoding = e.IsScanCode ? "scan" : "vk";

            if (!e.IsKeyUp)
            {
                pressed[logical] = encoding;
                if (!modifierScans.Contains(logical)) MainKeyDowns.Add(logical);
            }
            else if (pressed.TryGetValue(logical, out var pressEncoding))
            {
                if (pressEncoding != encoding) MismatchedReleases.Add(e);
                pressed.Remove(logical);
            }
            else
            {
                OrphanKeyUps.Add(e);
            }
        }

        StuckKeys.UnionWith(pressed.Keys);
    }

    private static uint LogicalOf(uint vk, FakeKeyboardLayout layout)
    {
        uint scan = layout.MapVirtualKey(vk, Win32.MAPVK_VK_TO_VSC, IntPtr.Zero);
        return (layout.IsExtendedVk(vk) ? 0xE000u : 0u) | scan;
    }
}
