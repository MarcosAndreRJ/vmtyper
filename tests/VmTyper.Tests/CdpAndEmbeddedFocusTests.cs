using System.Text.Json;
using VmTyper.Helpers;
using VmTyper.Models;
using VmTyper.Native;
using VmTyper.Services;
using VmTyper.Tests.Fakes;
using Xunit;

namespace VmTyper.Tests;

[Trait("Category", "Unit")]
public sealed class CdpAndEmbeddedFocusTests
{
    private sealed class RecordingCdpDispatcher : ICdpDispatcher
    {
        public List<(string Method, CdpKeyEventPayload Payload)> Calls { get; } = new();

        public Task<string> CallDevToolsProtocolMethodAsync(string methodName, string parametersJson, CancellationToken ct = default)
        {
            var payload = JsonSerializer.Deserialize<CdpKeyEventPayload>(parametersJson)!;
            Calls.Add((methodName, payload));
            return Task.FromResult("{}");
        }
    }

    private sealed class FakeEmbeddedFocusProbe : IEmbeddedFocusProbe
    {
        public EmbeddedFocusSnapshot Snapshot { get; set; } = new(
            HostWindowHwnd: new IntPtr(0x5555),
            IsHostWindowForeground: true,
            IsWebViewControlFocused: true,
            IsDocumentFocused: true,
            ActiveElementTagOrId: "targetTextarea",
            IsConsoleElementActive: true,
            IsWebViewProcessAlive: true);

        public EmbeddedFocusSnapshot ProbeFocus() => Snapshot;
    }

    [Fact]
    public void CdpKeyEventMapper_MapsScanCodes_Modifiers_AndExtendedKeys()
    {
        var layout = FakeKeyboardLayout.Abnt2();
        var planner = new KeyStrokePlanner(new KeyboardMappingService(layout));
        var report = planner.Plan("A/", new PlanOptions(
            InputMethod.TecladoFisico,
            layout.GetLayout(),
            Array.Empty<CharacterSubstitution>(),
            PreserveOriginalIndentation: false,
            IndentationResetStrategy.Auto));

        // Unidade 0: 'A' (ShiftLeft Down -> KeyA Down -> KeyA Up -> ShiftLeft Up)
        var eventsA = CdpKeyEventMapper.MapUnit(report.Units[0]);
        Assert.Equal(4, eventsA.Count);
        Assert.Equal("ShiftLeft", eventsA[0].Code);
        Assert.Equal("rawKeyDown", eventsA[0].Type);
        Assert.Equal(CdpKeyEventMapper.ModShift, eventsA[0].Modifiers);

        Assert.Equal("KeyA", eventsA[1].Code);
        Assert.Equal("keyDown", eventsA[1].Type);
        Assert.Equal("A", eventsA[1].Key);
        Assert.Equal("A", eventsA[1].Text);
        Assert.Equal(CdpKeyEventMapper.ModShift, eventsA[1].Modifiers);

        Assert.Equal("KeyA", eventsA[2].Code);
        Assert.Equal("keyUp", eventsA[2].Type);

        Assert.Equal("ShiftLeft", eventsA[3].Code);
        Assert.Equal("keyUp", eventsA[3].Type);
        Assert.Equal(0, eventsA[3].Modifiers);

        // Unidade 1: '/' no ABNT2 (scan 0x73 -> IntlRo)
        var eventsSlash = CdpKeyEventMapper.MapUnit(report.Units[1]);
        Assert.Equal(2, eventsSlash.Count);
        Assert.Equal("IntlRo", eventsSlash[0].Code);
        Assert.Equal("/", eventsSlash[0].Text);
    }

    [Fact]
    public void CdpKeyEventMapper_DeadKeyStrategies_ProduceExpectedPayloads()
    {
        var layout = FakeKeyboardLayout.Abnt2();
        var planner = new KeyStrokePlanner(new KeyboardMappingService(layout));
        var report = planner.Plan("ã~a", new PlanOptions(
            InputMethod.TecladoFisico,
            layout.GetLayout(),
            Array.Empty<CharacterSubstitution>(),
            PreserveOriginalIndentation: false,
            IndentationResetStrategy.Auto));

        // 'ã' com DeadKeyThenComposed: Dead keyDown/keyUp (Quote) + KeyA keyDown(text="ã")/keyUp
        var deadThenBase = CdpKeyEventMapper.MapUnit(report.Units[0], CdpDeadKeyStrategy.DeadKeyThenComposed);
        Assert.Equal(4, deadThenBase.Count);
        Assert.Equal("Dead", deadThenBase[0].Key);
        Assert.Equal("Quote", deadThenBase[0].Code);
        Assert.Null(deadThenBase[0].Text);
        Assert.Equal("KeyA", deadThenBase[2].Code);
        Assert.Equal("ã", deadThenBase[2].Text);

        // 'ã' com ComposedTextDirect: 2 eventos diretos na tecla base (KeyA com text="ã")
        var composedDirect = CdpKeyEventMapper.MapUnit(report.Units[0], CdpDeadKeyStrategy.ComposedTextDirect);
        Assert.Equal(2, composedDirect.Count);
        Assert.Equal("KeyA", composedDirect[0].Code);
        Assert.Equal("ã", composedDirect[0].Text);

        // '~' literal em "~a" (seguido de espaço): emite "~" no espaço após a tecla morta
        var tildeLiteral = CdpKeyEventMapper.MapUnit(report.Units[1], CdpDeadKeyStrategy.DeadKeyThenComposed);
        Assert.Equal(4, tildeLiteral.Count);
        Assert.Equal("Dead", tildeLiteral[0].Key);
        Assert.Equal("Space", tildeLiteral[2].Code);
        Assert.Equal("~", tildeLiteral[2].Text);
    }

    [Theory]
    [InlineData(CdpDeadKeyStrategy.DeadKeyThenComposed)]
    [InlineData(CdpDeadKeyStrategy.ComposedTextDirect)]
    public async Task CdpInputInjector_IntegratesWithKeyboardInputService_AndEmitsExpectedText(CdpDeadKeyStrategy strategy)
    {
        using var dir = new TempDir();
        var layout = FakeKeyboardLayout.Abnt2();
        var cdpDispatcher = new RecordingCdpDispatcher();
        var cdpInjector = new CdpInputInjector(cdpDispatcher, strategy);
        var service = new KeyboardInputService(
            new KeyboardMappingService(layout),
            TestLog.Create(dir),
            cdpInjector,
            layout);

        const string sample = "Olá, ação ~a 123!\n\tfim";
        await service.TypeTextAsync(
            sample,
            new TypingOptions
            {
                Method = InputMethod.Automatico,
                PreserveOriginalIndentation = false,
                Profile = TypingProfile.Legacy(0, 0, seed: 1)
            },
            null,
            PauseToken.None,
            CancellationToken.None);

        Assert.All(cdpDispatcher.Calls, c => Assert.Equal("Input.dispatchKeyEvent", c.Method));

        // Reconstrói o texto emitido pelos eventos keyDown com Text != null (convertendo \r de Enter para \n)
        string emittedText = string.Concat(
            cdpDispatcher.Calls
                .Where(c => c.Payload.Type == "keyDown" && c.Payload.Text is not null)
                .Select(c => c.Payload.Text == "\r" ? "\n" : c.Payload.Text));

        Assert.Equal(sample, emittedText);
    }

    [Fact]
    public void TargetGuard_EmbeddedMode_ReportsUnknownForUnverifiableLevels_AndPausesOnAnyLoss()
    {
        using var dir = new TempDir();
        var fgProvider = new FakeForegroundWindowProvider();
        using var guard = new TargetGuard(fgProvider, TestLog.Create(dir));
        var probe = new FakeEmbeddedFocusProbe();

        // 1. Tudo com foco: Ok, mas GuestReadyStatus é sempre "desconhecido" (nunca "ok")
        Assert.Equal("desconhecido", probe.Snapshot.GuestReadyStatus);
        var okRes = guard.CheckEmbeddedBeforeUnit(0, probe);
        Assert.Equal(TargetCheckStatus.Ok, okRes.Status);

        // 2. Nível não verificável (ex.: cross-origin iframe -> IsConsoleElementActive = null): reporta "desconhecido", não falha se os níveis verificáveis estão ok
        probe.Snapshot = probe.Snapshot with { IsConsoleElementActive = null, ActiveElementTagOrId = null };
        Assert.Equal("desconhecido", probe.Snapshot.ConsoleFocusStatus);
        Assert.Equal("desconhecido", probe.Snapshot.ActiveElementStatus);
        Assert.Equal(TargetCheckStatus.Ok, guard.CheckEmbeddedBeforeUnit(1, probe).Status);

        // 3. Perda de foco no documento (document.hasFocus() == false) -> TargetLost
        probe.Snapshot = probe.Snapshot with { IsDocumentFocused = false };
        var docLost = guard.CheckEmbeddedBeforeUnit(2, probe);
        Assert.Equal(TargetCheckStatus.TargetLost, docLost.Status);
        Assert.NotNull(docLost.Incident);

        // 4. Falha do processo WebView2 (ProcessFailed) -> TargetLost imediato
        probe.Snapshot = probe.Snapshot with { IsDocumentFocused = true, IsWebViewProcessAlive = false };
        var procFailed = guard.CheckEmbeddedBeforeUnit(3, probe);
        Assert.Equal(TargetCheckStatus.TargetLost, procFailed.Status);
        Assert.NotNull(procFailed.Incident);
    }
}
