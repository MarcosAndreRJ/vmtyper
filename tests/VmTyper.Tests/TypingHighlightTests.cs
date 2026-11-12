using VmTyper.Models;
using VmTyper.Documents;
using VmTyper.Services;
using VmTyper.Tests.Fakes;
using VmTyper.ViewModels;
using Xunit;

namespace VmTyper.Tests;

[Trait("Category", "Unit")]
public sealed class TypingHighlightTests
{
    private static PreflightReport Plan(string text, params CharacterSubstitution[] subs)
    {
        var layout = FakeKeyboardLayout.Abnt2();
        var planner = new KeyStrokePlanner(new KeyboardMappingService(layout));
        return planner.Plan(text, new PlanOptions(
            InputMethod.TecladoFisico, layout.GetLayout(), subs));
    }

    [Fact]
    public void Planner_RecordsSourceRangeOfEachUnit_InNormalizedText()
    {
        var report = Plan("ab\r\ncd");

        // Normalizado: "ab\ncd"
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, report.Units.Select(u => u.SourceStart));
        Assert.All(report.Units, u => Assert.Equal(1, u.SourceLength));
    }

    [Fact]
    public void Planner_UnitsOfATextSubstitution_ShareTheSourceRangeOfTheOriginal()
    {
        var report = Plan("x->y", new CharacterSubstitution { From = "->", To = "=>" });

        // 'x' (0), "=>" gera 2 unidades com origem 1..3, 'y' (3)
        Assert.Equal(new[] { 0, 1, 1, 3 }, report.Units.Select(u => u.SourceStart));
        Assert.Equal(new[] { 1, 2, 2, 1 }, report.Units.Select(u => u.SourceLength));
    }

    [Fact]
    public void NormalizedToRawMap_SkipsTheCarriageReturnOfCrLf()
    {
        var map = MainViewModel.BuildNormalizedToRawMap("ab\r\ncd");

        // normalizado "ab\ncd": a=0 b=1 \n=3 (o \r anterior é descartado) c=4 d=5, fim=6
        Assert.Equal(new[] { 0, 1, 3, 4, 5, 6 }, map);
    }

    [Theory]
    [InlineData("begin Foo_1 := 2;", 8, 6, 5)]  // dentro da palavra Foo_1 (começa em 6)
    [InlineData("begin Foo_1 := 2;", 0, 0, 5)]  // início da palavra
    [InlineData("a := b;", 2, 2, 1)]            // símbolo isolado: o próprio caractere
    [InlineData("a b", 1, -1, 0)]               // espaço: sem destaque
    public void ExpandToWord_ReturnsTheWordAroundThePosition(string text, int index, int expectedStart, int expectedLength)
    {
        var (start, length) = MainViewModel.ExpandToWord(text, index, 1);
        Assert.Equal(expectedStart, start);
        Assert.Equal(expectedLength, length);
    }
}

[Trait("Category", "Unit")]
public sealed class QueueTypingHighlightTests
{
    [Fact]
    public async Task QueueBlock_HighlightsEachWordWhileTyping_AndClearsAtTheEnd()
    {
        using var dir = new TempDir();
        var log = TestLog.Create(dir);
        using var harness = new EngineHarness();
        var settingsSvc = new SettingsService(dir.File("settings.json"));
        settingsSvc.Save(new AppSettings { CountdownSeconds = 0, CharDelayMs = 0, EnterDelayMs = 0, EnableTargetGuard = false });

        var queue = new TypingQueueService(log, new InMemoryQueueStore(), new ManualAutoSaveScheduler());
        queue.Blocks[0].Text = "ola mundo";
        var hotkeys = new FakeHotkeyRegistrar();
        var vm = new MainViewModel(
            harness.Engine, new ActiveWindowService(), settingsSvc, new HistoryService(dir.File("history.json")),
            hotkeys, queue, log, new TypingSessionCoordinator(hotkeys, log), (_, _) => Task.CompletedTask);

        var block = queue.Blocks[0];
        var seen = new List<TextHighlight>();
        block.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TextBlockItem.TypingHighlight) && block.TypingHighlight is { } h) seen.Add(h);
        };

        await vm.StartQueueBlockAsync();

        Assert.Contains(new TextHighlight(0, 3), seen);   // "ola"
        Assert.Contains(new TextHighlight(4, 5), seen);   // "mundo"
        Assert.Null(block.TypingHighlight);
    }
}

[Trait("Category", "Unit")]
public sealed class EditorTypingHighlightTests
{
    private static async Task<List<(int Offset, int Length)>> TypeAsync(Func<MainViewModel, DocumentManager, Task> start)
    {
        using var dir = new TempDir();
        var log = TestLog.Create(dir);
        using var harness = new EngineHarness();
        var settingsSvc = new SettingsService(dir.File("settings.json"));
        settingsSvc.Save(new AppSettings { CountdownSeconds = 0, CharDelayMs = 0, EnterDelayMs = 0, EnableTargetGuard = false });

        var manager = new DocumentManager();
        manager.CreateNewDocument("ola mundo", "Doc");
        var hotkeys = new FakeHotkeyRegistrar();
        var vm = new MainViewModel(
            harness.Engine, new ActiveWindowService(), settingsSvc, new HistoryService(dir.File("history.json")),
            hotkeys, new TypingQueueService(log, new InMemoryQueueStore(), new ManualAutoSaveScheduler()), log,
            new TypingSessionCoordinator(hotkeys, log), (_, _) => Task.CompletedTask, docManager: manager);

        var seen = new List<(int, int)>();
        vm.TypingHighlightChanged += (o, l) => seen.Add((o, l));
        await start(vm, manager);
        return seen;
    }

    [Fact]
    public async Task WholeDocument_HighlightsEachWord_AndClearsAtTheEnd()
    {
        var seen = await TypeAsync((vm, _) => vm.StartDocumentAsync());

        Assert.Contains((0, 3), seen);   // "ola"
        Assert.Contains((4, 5), seen);   // "mundo"
        Assert.Equal((-1, 0), seen[^1]);
    }

    [Fact]
    public async Task Selection_HighlightsRelativeToTheSelectionStart()
    {
        var seen = await TypeAsync(async (vm, _) =>
        {
            vm.EditorSelectionStartProvider = () => 100;
            await vm.StartSelectionAsync("ola mundo");
        });

        Assert.Contains((100, 3), seen);
        Assert.Contains((104, 5), seen);
    }
}
