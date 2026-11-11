using VmTyper.Models;
using VmTyper.Services;
using VmTyper.Tests.Fakes;
using VmTyper.ViewModels;
using Xunit;

namespace VmTyper.Tests;

internal sealed class FakeForegroundWindowProvider : IForegroundWindowProvider
{
    private Action<IntPtr>? _listener;

    public uint CurrentProcessId { get; set; } = 9999;
    public IntPtr ForegroundHwnd { get; private set; } = new(0x1001);
    public uint ForegroundPid { get; set; } = 2000;
    public string ForegroundProcessName { get; set; } = "chrome";
    public string ForegroundTitle { get; set; } = "Console VMware - VM01";
    public int CaptureCalls { get; private set; }

    public void SwitchWindow(IntPtr hwnd, uint pid, string processName, string title)
    {
        ForegroundHwnd = hwnd;
        ForegroundPid = pid;
        ForegroundProcessName = processName;
        ForegroundTitle = title;
        _listener?.Invoke(hwnd);
    }

    public IntPtr GetForegroundRootHwnd() => ForegroundHwnd;

    public string GetWindowTitle(IntPtr hwnd) => hwnd == ForegroundHwnd ? ForegroundTitle : string.Empty;

    public TargetAnchor? CaptureAnchor()
    {
        CaptureCalls++;
        if (ForegroundHwnd == IntPtr.Zero) return null;
        return new TargetAnchor(ForegroundHwnd, ForegroundPid, ForegroundProcessName, ForegroundTitle, DateTime.UtcNow);
    }

    public IDisposable SubscribeForegroundChanged(Action<IntPtr> onForegroundRootChanged)
    {
        _listener = onForegroundRootChanged;
        return new DummyDisposable(() => _listener = null);
    }

    private sealed class DummyDisposable : IDisposable
    {
        private readonly Action _onDispose;
        public DummyDisposable(Action onDispose) => _onDispose = onDispose;
        public void Dispose() => _onDispose();
    }
}

[Trait("Category", "Unit")]
public sealed class TargetGuardTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void TryCaptureAnchor_RejectsOwnProcess_AndWrongBrowser()
    {
        var log = TestLog.Create(_dir);
        var provider = new FakeForegroundWindowProvider
        {
            CurrentProcessId = 1234,
            ForegroundPid = 1234,
            ForegroundProcessName = "VmTyper"
        };
        using var guard = new TargetGuard(provider, log);

        // Próprio VmTyper -> rejeitado.
        var ownRes = guard.TryCaptureAnchor(validateBrowser: false, ExpectedBrowser.Nenhum);
        Assert.False(ownRes.IsValid);

        // Outro processo, mas navegador errado com ValidateTargetWindow ligado -> rejeitado.
        provider.ForegroundPid = 5555;
        provider.ForegroundProcessName = "notepad";
        var browserRes = guard.TryCaptureAnchor(validateBrowser: true, ExpectedBrowser.Chrome);
        Assert.False(browserRes.IsValid);

        // Navegador certo -> aceito e PID/ProcessName cacheados na âncora (N13).
        provider.ForegroundProcessName = "chrome";
        var okRes = guard.TryCaptureAnchor(validateBrowser: true, ExpectedBrowser.Chrome);
        Assert.True(okRes.IsValid);
        Assert.Equal(1, provider.CaptureCalls - 2); // só TryCaptureAnchor consulta PID/ProcessName
    }

    [Fact]
    public void CheckBeforeUnit_UsesCachedAnchorWithoutRequeryingProcess_AndHandlesTitleChanges()
    {
        var log = TestLog.Create(_dir);
        var provider = new FakeForegroundWindowProvider();
        using var guard = new TargetGuard(provider, log);

        Assert.True(guard.TryCaptureAnchor(false, ExpectedBrowser.Nenhum).IsValid);
        int capturesAfterInit = provider.CaptureCalls;

        // 100 verificações por unidade não chamam CaptureAnchor (N13).
        for (int i = 0; i < 100; i++)
        {
            var r = guard.CheckBeforeUnit(i, strictTitleMode: false);
            Assert.Equal(TargetCheckStatus.Ok, r.Status);
        }
        Assert.Equal(capturesAfterInit, provider.CaptureCalls);

        // Mudança de título no modo padrão -> apenas aviso, sem perder destino.
        provider.ForegroundTitle = "Console VMware - Outra Aba";
        var warnCheck = guard.CheckBeforeUnit(100, strictTitleMode: false);
        Assert.Equal(TargetCheckStatus.TitleChangedWarning, warnCheck.Status);

        // Mudança de título no modo TargetGuardStrict -> TargetLost + incidente.
        provider.ForegroundTitle = "Console VMware - Aba 3";
        var strictCheck = guard.CheckBeforeUnit(101, strictTitleMode: true);
        Assert.Equal(TargetCheckStatus.TargetLost, strictCheck.Status);
        Assert.Single(guard.Incidents);
    }

    [Fact]
    [Trait("Known", "C")]
    public async Task LosingFocusMidTyping_PausesAtExactUnitIndex_WithoutCuttingUnit_AndRequiresConfirmationForNewHwnd()
    {
        var log = TestLog.Create(_dir);
        var provider = new FakeForegroundWindowProvider();
        using var guard = new TargetGuard(provider, log);
        var hotkeys = new FakeHotkeyRegistrar();
        var coordinator = new TypingSessionCoordinator(hotkeys, log);
        var store = new InMemoryQueueStore();
        var autoSave = new ManualAutoSaveScheduler();
        var queue = new TypingQueueService(log, store, autoSave);
        queue.Blocks[0].Text = "ABCD";

        var settings = new SettingsService(_dir.File("settings.json"));
        settings.Save(new AppSettings
        {
            CountdownSeconds = 0,
            CharDelayMs = 20,
            EnableTargetGuard = true,
            ValidateTargetWindow = false
        });
        var history = new HistoryService(_dir.File("history.json"));

        using var harness = new EngineHarness();
        var vm = new MainViewModel(
            harness.Engine,
            new ActiveWindowService(),
            settings,
            history,
            hotkeys,
            queue,
            log,
            coordinator,
            (_, _) => Task.CompletedTask,
            guard)
        {
            ResumeCountdownSeconds = 0
        };

        // Quando a 2ª unidade ('B', index 1) terminar de ser enviada, trocamos o HWND antes da 3ª ('C', index 2).
        int unitsDeliveredBeforeSwitch = 0;
        queue.Blocks[0].PropertyChanged += (_, _) => { };
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.TypedCharacters) && vm.TypedCharacters == 2 && unitsDeliveredBeforeSwitch == 0)
            {
                unitsDeliveredBeforeSwitch = vm.TypedCharacters;
                provider.SwitchWindow(new IntPtr(0x2002), 3000, "notepad", "Bloco de Notas");
            }
        };

        var sessionTask = vm.StartAsync();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (vm.State == TypingState.Digitando && sw.ElapsedMilliseconds < 2000)
        {
            await Task.Delay(10);
        }

        // A sessão deve estar pausada por perda de destino exatamente antes da unidade 2 ('C').
        Assert.Equal(TypingState.PausadoDestinoPerdido, vm.State);
        Assert.Equal(TextBlockStatus.Paused, queue.Blocks[0].Status);
        Assert.Equal(2, vm.TypedCharacters);
        Assert.Single(guard.Incidents);
        Assert.Equal(2, guard.Incidents[0].UnitIndex);

        // Nenhuma unidade ficou cortada no meio (zero teclas presas no injetor).
        var midAnalysis = harness.Analyze();
        Assert.Empty(midAnalysis.StuckKeys);
        Assert.Equal(2, midAnalysis.MainKeyDowns.Count);

        // Tentativa 1 de retomar com o novo HWND (0x2002) recusando a confirmação -> continua pausado.
        bool confirmationAsked = false;
        vm.ConfirmTargetChanged = (_, _) =>
        {
            confirmationAsked = true;
            return false;
        };
        vm.TogglePause();
        Assert.True(confirmationAsked);
        Assert.Equal(TypingState.PausadoDestinoPerdido, vm.State);

        // Tentativa 2 de retomar confirmando o novo HWND -> retoma e conclui as 2 unidades restantes ('C', 'D').
        vm.ConfirmTargetChanged = (_, _) => true;
        vm.TogglePause();
        await sessionTask;

        Assert.Equal(TypingState.Concluido, vm.State);
        Assert.Equal(4, vm.TypedCharacters);
        var finalAnalysis = harness.Analyze();
        Assert.Empty(finalAnalysis.StuckKeys);
        Assert.Equal(4, finalAnalysis.MainKeyDowns.Count);
    }
}
