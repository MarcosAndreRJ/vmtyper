using VmTyper.Helpers;
using VmTyper.Models;
using VmTyper.Native;
using VmTyper.Services;
using VmTyper.Tests.Fakes;
using VmTyper.ViewModels;
using Xunit;

namespace VmTyper.Tests;

internal sealed class FakeHotkeyRegistrar : IHotkeyRegistrar
{
    private readonly Dictionary<int, (uint modifiers, uint vk, Action handler)> _registered = new();

    public bool RegisterHotkey(int id, uint modifiers, uint virtualKey, Action handler)
    {
        _registered[id] = (modifiers, virtualKey, handler);
        return true;
    }

    public void UnregisterHotkey(int id) => _registered.Remove(id);

    public bool IsRegistered(int id) => _registered.ContainsKey(id);

    public void Trigger(int id)
    {
        if (_registered.TryGetValue(id, out var entry))
        {
            entry.handler();
        }
    }
}

[Trait("Category", "Unit")]
public sealed class TypingSessionCoordinatorTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Coordinator_SecondBegin_ReturnsNull()
    {
        var hotkeys = new FakeHotkeyRegistrar();
        var coordinator = new TypingSessionCoordinator(hotkeys, TestLog.Create(_dir));

        using var first = coordinator.TryBegin("Main");
        Assert.NotNull(first);
        Assert.True(coordinator.IsActive);

        var second = coordinator.TryBegin("Diagnostics");
        Assert.Null(second);
    }

    [Fact]
    public void Coordinator_DisposeReleasesEsc()
    {
        var hotkeys = new FakeHotkeyRegistrar();
        var coordinator = new TypingSessionCoordinator(hotkeys, TestLog.Create(_dir));

        var lease = coordinator.TryBegin("Main");
        Assert.NotNull(lease);
        Assert.True(hotkeys.IsRegistered(TypingSessionCoordinator.HotkeyIdCancel));

        // ESC cancela o lease ativo.
        hotkeys.Trigger(TypingSessionCoordinator.HotkeyIdCancel);
        Assert.True(lease!.Token.IsCancellationRequested);

        lease.Dispose();
        Assert.False(coordinator.IsActive);
        Assert.False(hotkeys.IsRegistered(TypingSessionCoordinator.HotkeyIdCancel));

        // Nova sessão após Dispose funciona normalmente.
        using var next = coordinator.TryBegin("Diagnostics");
        Assert.NotNull(next);
    }

    [Fact]
    public void Diagnostics_WhileMainActive_IsRefused()
    {
        var hotkeys = new FakeHotkeyRegistrar();
        var coordinator = new TypingSessionCoordinator(hotkeys, TestLog.Create(_dir));

        using var mainLease = coordinator.TryBegin("Main");
        Assert.NotNull(mainLease);
        Assert.Equal("Main", coordinator.ActiveOrigin);

        var diagLease = coordinator.TryBegin("Diagnostics");
        Assert.Null(diagLease);
        Assert.Equal("Main", coordinator.ActiveOrigin);
    }

    [Fact]
    public async Task PauseDuringCountdown_FreezesCountdown()
    {
        var log = TestLog.Create(_dir);
        var hotkeys = new FakeHotkeyRegistrar();
        var coordinator = new TypingSessionCoordinator(hotkeys, log);
        var store = new InMemoryQueueStore();
        var autoSave = new ManualAutoSaveScheduler();
        var queue = new TypingQueueService(log, store, autoSave);
        queue.Blocks[0].Text = "abc";
        queue.RestartQueue();

        var settings = new SettingsService(_dir.File("settings.json"));
        settings.Save(new AppSettings { CountdownSeconds = 3, ValidateTargetWindow = false, EnableTargetGuard = false });
        var history = new HistoryService(_dir.File("history.json"));

        var ticks = new List<TaskCompletionSource<bool>>();
        Task FakeDelay(int ms, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<bool>();
            ct.Register(() => tcs.TrySetCanceled(ct));
            ticks.Add(tcs);
            return tcs.Task;
        }

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
            FakeDelay);

        var startTask = vm.StartAsync();

        // Contagem começa em 3.
        Assert.Equal(TypingState.AguardandoContagem, vm.State);
        Assert.Equal("Iniciando em 3...", vm.CountdownText);
        Assert.Single(ticks);

        // Avança 1 segundo -> chega em 2.
        ticks[0].SetResult(true);
        Assert.Equal("Iniciando em 2...", vm.CountdownText);
        Assert.Equal(2, ticks.Count);

        // Pausa durante a contagem em 2.
        vm.TogglePause();
        Assert.Equal(TypingState.Pausado, vm.State);

        // O segundo corrente termina, mas como está pausado não deve avançar para 1 nem iniciar digitação.
        ticks[1].SetResult(true);
        await Task.Yield();
        Assert.Equal(2, ticks.Count);
        Assert.Equal("Iniciando em 2...", vm.CountdownText);
        Assert.Equal(TypingState.Pausado, vm.State);

        // Retoma -> avança para o segundo 1.
        vm.TogglePause();
        Assert.Equal(TypingState.AguardandoContagem, vm.State);
        Assert.Equal(3, ticks.Count);
        Assert.Equal("Iniciando em 1...", vm.CountdownText);

        // Conclui o último segundo -> digita e finaliza.
        ticks[2].SetResult(true);
        await startTask;

        Assert.Equal(TypingState.Concluido, vm.State);
    }
}
