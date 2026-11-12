using System.IO;
using VmTyper.Models;
using VmTyper.Services;
using VmTyper.Tests.Fakes;
using VmTyper.ViewModels;
using Xunit;

namespace VmTyper.Tests;

[Trait("Category", "Unit")]
public sealed class SubstitutionRoundTripTests
{
    [Fact]
    public void SubstitutionsConfiguredInSettingsViewModel_SurviveSaveAndLoad()
    {
        string path = Path.Combine(Path.GetTempPath(), "vmtyper-sub-" + Guid.NewGuid().ToString("N"), "settings.json");
        var service = new SettingsService(path);

        var vm = new SettingsViewModel(new AppSettings(), Path.GetDirectoryName(path));
        vm.AddSubstitution(new CharacterSubstitution { From = "->", To = "=>" });
        vm.AddSubstitution(new CharacterSubstitution { From = "/", Mode = SubstitutionMode.Combinacao, ComboVirtualKey = 0x6F, ComboShift = true });

        var built = vm.TryBuildSettings();
        Assert.True(built.IsValid, string.Join("; ", built.Errors));
        Assert.Equal(2, built.Settings!.CharacterSubstitutions.Count);

        service.Save(built.Settings);
        var loaded = service.Load();

        Assert.Equal(2, loaded.CharacterSubstitutions.Count);
        Assert.Equal("=>", loaded.CharacterSubstitutions[0].To);
        Assert.Equal(SubstitutionMode.Combinacao, loaded.CharacterSubstitutions[1].Mode);
    }
}

[Trait("Category", "Unit")]
public sealed class EditorSubstitutionTests
{
    [Fact]
    public async Task EditorSelection_AppliesSubstitutionsFromSettings()
    {
        using var dir = new TempDir();
        var log = TestLog.Create(dir);
        using var harness = new EngineHarness();
        var settingsSvc = new SettingsService(dir.File("settings.json"));
        settingsSvc.Save(new AppSettings
        {
            CountdownSeconds = 0,
            CharDelayMs = 0,
            EnterDelayMs = 0,
            EnableTargetGuard = false,
            // Texto vazio em "Para": a sequência de origem é removida da digitação.
            CharacterSubstitutions = { new CharacterSubstitution { From = "->", To = string.Empty } }
        });

        var hotkeys = new FakeHotkeyRegistrar();
        var vm = new MainViewModel(
            harness.Engine, new ActiveWindowService(), settingsSvc, new HistoryService(dir.File("history.json")),
            hotkeys, new TypingQueueService(log, new InMemoryQueueStore(), new ManualAutoSaveScheduler()), log,
            new TypingSessionCoordinator(hotkeys, log), (_, _) => Task.CompletedTask);

        await vm.StartSelectionAsync("x->y");

        var analysis = new KeyStateAnalysis(harness.Injector.Delivered, harness.Layout);
        Assert.Equal(2, analysis.MainKeyDowns.Count);
    }
}
