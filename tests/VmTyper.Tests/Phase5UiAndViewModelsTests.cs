using System.IO;
using System.Text;
using System.Xml.Linq;
using VmTyper.Models;
using VmTyper.Services;
using VmTyper.Tests.Fakes;
using VmTyper.ViewModels;
using Xunit;

namespace VmTyper.Tests;

public sealed class Phase5UiAndViewModelsTests
{
    [Fact]
    public void TextBlockItem_AllStates_ExposeDistinctIconTextAndSupportingColor()
    {
        var bEmpty = new TextBlockItem { Order = 1, Text = "", Status = TextBlockStatus.Empty };
        Assert.Equal("○", bEmpty.StatusIcon);
        Assert.Equal("Vazio", bEmpty.StatusText);

        var bReady = new TextBlockItem { Order = 1, Text = "abc", Status = TextBlockStatus.Ready };
        Assert.Equal("●", bReady.StatusIcon);
        Assert.Equal("Pronto", bReady.StatusText);

        var bNext = new TextBlockItem { Order = 1, Text = "abc", Status = TextBlockStatus.Ready };
        typeof(TextBlockItem).GetProperty(nameof(TextBlockItem.IsNext))!.SetValue(bNext, true);
        Assert.Equal("▶", bNext.StatusIcon);
        Assert.Contains("Próximo ▶", bNext.StatusText);

        var bTyping = new TextBlockItem { Order = 1, Text = "abc", Status = TextBlockStatus.Typing };
        Assert.Equal("⌨", bTyping.StatusIcon);
        Assert.Equal("Digitando", bTyping.StatusText);

        var bPaused = new TextBlockItem { Order = 1, Text = "abc", Status = TextBlockStatus.Paused };
        Assert.Equal("⏸", bPaused.StatusIcon);
        Assert.Equal("Pausado", bPaused.StatusText);

        var bSent = new TextBlockItem { Order = 1, Text = "abc", Status = TextBlockStatus.Sent };
        Assert.Equal("↗", bSent.StatusIcon);
        Assert.Equal("Enviado", bSent.StatusText);

        var bPartial = new TextBlockItem { Order = 1, Text = "abcdef", SentUpToIndex = 3, Status = TextBlockStatus.PartiallySent };
        Assert.Equal("◐", bPartial.StatusIcon);
        Assert.Equal("Parcial (3/6)", bPartial.StatusText);

        var bFailed = new TextBlockItem { Order = 1, Text = "abc", Status = TextBlockStatus.Failed };
        Assert.Equal("✗", bFailed.StatusIcon);
        Assert.Equal("Falha", bFailed.StatusText);

        var bInterrupted = new TextBlockItem { Order = 1, Text = "abc", Status = TextBlockStatus.Interrupted };
        Assert.Equal("⚠", bInterrupted.StatusIcon);
        Assert.Equal("Interrompido", bInterrupted.StatusText);
    }

    [Fact]
    public async Task DeliverySemantics_ConfirmedIsNeverDerivedFromLocalSent()
    {
        // 1. Nível de unidade e de bloco local
        Assert.Equal(DeliverySemanticState.Sent, DeliverySemantics.FromLocalSendResult(UnitOutcome.Sent, hadFocusInstability: false));
        Assert.Equal(DeliverySemanticState.Suspicious, DeliverySemantics.FromLocalSendResult(UnitOutcome.Sent, hadFocusInstability: true));
        Assert.Equal(DeliverySemanticState.Failed, DeliverySemantics.FromLocalSendResult(UnitOutcome.Skipped));
        Assert.Equal(DeliverySemanticState.Failed, DeliverySemantics.FromLocalSendResult(UnitOutcome.Failed));

        foreach (TextBlockStatus status in Enum.GetValues<TextBlockStatus>())
        {
            var blockSemantic = DeliverySemantics.FromBlockStatus(status);
            Assert.NotEqual(DeliverySemanticState.Confirmed, blockSemantic);
        }

        // 2. Nível de MainViewModel ao concluir bloco sem receptor
        using var dir = new TempDir();
        var log = TestLog.Create(dir);
        using var harness = new EngineHarness();
        var settingsSvc = new SettingsService(dir.File("settings.json"));
        settingsSvc.Save(new AppSettings
        {
            CountdownSeconds = 0,
            CharDelayMs = 0,
            EnterDelayMs = 0,
            EnableTargetGuard = false
        });

        var queueSvc = new TypingQueueService(log, new InMemoryQueueStore(), new ManualAutoSaveScheduler());
        queueSvc.Blocks[0].Text = "Bloco de teste F5";
        var hotkeys = new FakeHotkeyRegistrar();
        var vm = new MainViewModel(
            harness.Engine,
            new ActiveWindowService(),
            settingsSvc,
            new HistoryService(dir.File("history.json")),
            hotkeys,
            queueSvc,
            log,
            new TypingSessionCoordinator(hotkeys, log),
            (_, _) => Task.CompletedTask);

        await vm.StartAsync();

        Assert.Equal(TypingState.Concluido, vm.State);
        Assert.Equal(DeliverySemanticState.Sent, vm.LastDeliverySemanticState);
        Assert.NotEqual(DeliverySemanticState.Confirmed, vm.LastDeliverySemanticState);
        Assert.Contains("unidades enviadas. Recebimento não verificado.", vm.StatusMessage);
        Assert.DoesNotContain("Confirmado", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("concluído com sucesso", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DiagnosticsViewModel_TimelineAndObserverComparison_EnforceFiveSemanticStates()
    {
        using var dir = new TempDir();
        var engine = new TypingDiagnosticsEngine(dir.Path);
        var opts = new TypingOptions { Method = VmTyper.Models.InputMethod.Automatico, TargetLayout = "00010416" };
        string jsonlPath = engine.BeginSession(Guid.NewGuid(), opts, TypingProfile.Legacy(), new FeatureFlags(), detailedTrace: false);

        var unit0 = new InputUnit(0, "a", UnitKind.Physical, new[]
        {
            new KeyEvent(KeyEventKind.Down, KeyEncoding.Scan, 0x1E, false),
            new KeyEvent(KeyEventKind.Up, KeyEncoding.Scan, 0x1E, false)
        });
        var unit1 = new InputUnit(1, "☺", UnitKind.Unsupported, Array.Empty<KeyEvent>(), "Unsupported");

        engine.RecordEvent(unit0, new KeyTiming(TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(2), "Normal"), new UnitResult(0, UnitOutcome.Sent, 2, 0, 100, 200));
        engine.RecordEvent(unit1, new KeyTiming(TimeSpan.Zero, TimeSpan.Zero, "Unsupported"), new UnitResult(1, UnitOutcome.Skipped, 0, 0, 200, 200, "Unsupported"));
        engine.EndSession();

        var diagVm = new DiagnosticsViewModel();
        diagVm.LoadSessionJsonlFile(jsonlPath);

        Assert.Equal(2, diagVm.TimelineEvents.Count);
        Assert.Equal(DeliverySemanticState.Sent, diagVm.TimelineEvents[0].Badge.State);
        Assert.NotEqual(DeliverySemanticState.Confirmed, diagVm.TimelineEvents[0].Badge.State);
        Assert.Equal(DeliverySemanticState.Failed, diagVm.TimelineEvents[1].Badge.State);

        // Antes de importar JSON do receptor -> Unknown
        Assert.Equal(DeliverySemanticState.Unknown, diagVm.ComparisonDeliveryBadge.State);

        // Importando JSON idêntico sem eventos suspeitos -> Confirmed
        diagVm.ComparisonExpectedText = "abcd";
        diagVm.ImportAndCompareObserverJson("{\"text\":\"abcd\",\"suspiciousCount\":0}");
        Assert.Equal(DeliverySemanticState.Confirmed, diagVm.ComparisonDeliveryBadge.State);

        // Importando JSON idêntico mas com repeat/double-down suspeito -> Suspicious
        diagVm.ImportAndCompareObserverJson("{\"text\":\"abcd\",\"suspiciousCount\":2}");
        Assert.Equal(DeliverySemanticState.Suspicious, diagVm.ComparisonDeliveryBadge.State);

        // Importando JSON com divergência (duplicação) -> Failed
        diagVm.ImportAndCompareObserverJson("{\"text\":\"abbcd\",\"suspiciousCount\":0}");
        Assert.Equal(DeliverySemanticState.Failed, diagVm.ComparisonDeliveryBadge.State);
    }

    [Fact]
    public void SettingsViewModel_Validation_RoundTrip_AndRestoreDefaultsPerTabAndGlobal()
    {
        var initial = new AppSettings
        {
            CharDelayMs = 15,
            EnterDelayMs = 45,
            CountdownSeconds = 5,
            ResumeCountdownSeconds = 4,
            Method = InputMethod.TecladoFisico,
            TargetLayout = "00010416",
            TypingProfile = "Compatibility",
            PreserveOriginalIndentation = true,
            EnableTargetGuard = false,
            ValidateTargetWindow = true,
            ExpectedBrowser = ExpectedBrowser.Edge,
            QueueTextPersistence = QueueTextPersistence.Encrypted,
            EnableDiagnosticsJsonl = false,
            DiagnosticsRetentionSessions = 50,
            Features = new FeatureFlags
            {
                AdaptiveTiming = true,
                TargetGuardStrict = true,
                EmbeddedBrowser = true,
                CdpInput = true,
                DetailedTrace = true
            }
        };

        var vm = new SettingsViewModel(initial);
        Assert.False(vm.HasErrors);
        Assert.NotEmpty(vm.DryRunPreviewText);

        // 1. Ida e volta completa
        var built = vm.TryBuildSettings();
        Assert.True(built.IsValid);
        Assert.NotNull(built.Settings);
        Assert.Equal(15, built.Settings!.CharDelayMs);
        Assert.Equal(45, built.Settings.EnterDelayMs);
        Assert.Equal(5, built.Settings.CountdownSeconds);
        Assert.Equal(4, built.Settings.ResumeCountdownSeconds);
        Assert.Equal(InputMethod.TecladoFisico, built.Settings.Method);
        Assert.Equal("00010416", built.Settings.TargetLayout);
        Assert.Equal("Compatibility", built.Settings.TypingProfile);
        Assert.True(built.Settings.PreserveOriginalIndentation);
        Assert.False(built.Settings.EnableTargetGuard);
        Assert.True(built.Settings.ValidateTargetWindow);
        Assert.Equal(ExpectedBrowser.Edge, built.Settings.ExpectedBrowser);
        Assert.Equal(QueueTextPersistence.Encrypted, built.Settings.QueueTextPersistence);
        Assert.False(built.Settings.EnableDiagnosticsJsonl);
        Assert.Equal(50, built.Settings.DiagnosticsRetentionSessions);
        Assert.True(built.Settings.Features.EmbeddedBrowser);
        Assert.True(built.Settings.Features.CdpInput);

        // 2. Validação INotifyDataErrorInfo
        vm.CharDelayText = "5000";
        Assert.True(vm.HasErrors);
        Assert.NotEmpty(vm.GetErrors(nameof(SettingsViewModel.CharDelayText)).Cast<string>());

        vm.CharDelayText = "10";
        Assert.False(vm.HasErrors);

        // Validação de perfil Custom inválido (HoldMin > HoldMax)
        vm.AdaptiveTiming = true;
        vm.TypingProfileTag = "Custom";
        vm.CustomHoldMinText = "100";
        vm.CustomHoldMaxText = "10";
        Assert.True(vm.HasErrors);
        Assert.False(vm.TryBuildSettings().IsValid);

        // 3. Restaurar apenas a aba Digitação preserva a aba Segurança e Persistência
        vm.RestoreTabDefaults(SettingsViewModel.TabDigitacao);
        Assert.False(vm.HasErrors);
        Assert.Equal("2", vm.CharDelayText);
        Assert.Equal("Legacy", vm.TypingProfileTag);
        Assert.Equal("Encrypted", vm.QueueTextPersistenceTag); // preservado!
        Assert.Equal("Edge", vm.ExpectedBrowserTag);           // preservado!

        // 4. Restaurar todos os padrões globalmente
        vm.RestoreAllDefaults();
        Assert.Equal("Full", vm.QueueTextPersistenceTag);
        Assert.Equal("Chrome", vm.ExpectedBrowserTag);
        Assert.True(vm.EnableTargetGuard);
        Assert.False(vm.EmbeddedBrowser);
    }

    [Fact]
    public async Task Phase5Smoke_TwoBlocks_QueueStates_AndAccessibility()
    {
        // 1. Verifica que todos os <Button das janelas XAML possuem AutomationProperties.Name (§5)
        string repoRoot = FindRepoRoot();
        string[] xamlFiles =
        {
            Path.Combine(repoRoot, "VmTyper", "MainWindow.xaml"),
            Path.Combine(repoRoot, "VmTyper", "Views", "SettingsWindow.xaml"),
            Path.Combine(repoRoot, "VmTyper", "Views", "DiagnosticsWindow.xaml"),
            Path.Combine(repoRoot, "VmTyper", "Views", "EmbeddedBrowserWindow.xaml")
        };

        XNamespace xamlNs = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        foreach (string xamlPath in xamlFiles)
        {
            var doc = XDocument.Load(xamlPath);
            var buttons = doc.Descendants(xamlNs + "Button").ToList();
            Assert.NotEmpty(buttons);
            foreach (var btn in buttons)
            {
                string? autoName = btn.Attribute("AutomationProperties.Name")?.Value;
                Assert.False(string.IsNullOrWhiteSpace(autoName),
                    $"Botão sem AutomationProperties.Name em {Path.GetFileName(xamlPath)}: {btn}");
            }
        }

        // 2. Smoke de fluxo completo: criar 2 blocos, digitar no receptor, verificar estados e progresso
        using var dir = new TempDir();
        var log = TestLog.Create(dir);
        using var harness = new EngineHarness();
        var settingsSvc = new SettingsService(dir.File("settings.json"));
        settingsSvc.Save(new AppSettings
        {
            CountdownSeconds = 0,
            CharDelayMs = 0,
            EnterDelayMs = 0,
            EnableTargetGuard = false
        });

        var queueSvc = new TypingQueueService(log, new InMemoryQueueStore(), new ManualAutoSaveScheduler());
        queueSvc.Blocks[0].Text = "Bloco 1: carro e ação!";
        queueSvc.AddBlock();
        queueSvc.Blocks[1].Text = "Bloco 2: teste final 123.";

        var hotkeys = new FakeHotkeyRegistrar();
        var vm = new MainViewModel(
            harness.Engine,
            new ActiveWindowService(),
            settingsSvc,
            new HistoryService(dir.File("history.json")),
            hotkeys,
            queueSvc,
            log,
            new TypingSessionCoordinator(hotkeys, log),
            (_, _) => Task.CompletedTask);

        Assert.Equal("▶ Próximo ▶", vm.Blocks[0].StatusLabel);
        Assert.Equal("● Pronto", vm.Blocks[1].StatusLabel);

        // Digita Bloco 1
        await vm.StartAsync();
        Assert.Equal(TextBlockStatus.Sent, vm.Blocks[0].Status);
        Assert.Equal("↗ Enviado", vm.Blocks[0].StatusLabel);
        Assert.Equal("▶ Próximo ▶", vm.Blocks[1].StatusLabel);
        Assert.Equal(0, vm.SkippedUnits);
        Assert.Equal(0, vm.FailedUnits);

        string received1 = harness.Layout.Decode(harness.Injector.Delivered);
        Assert.Equal(vm.Blocks[0].Text, received1);

        int countAfter1 = harness.Injector.Delivered.Count;

        // Digita Bloco 2
        await vm.StartAsync();
        Assert.Equal(TextBlockStatus.Sent, vm.Blocks[1].Status);
        Assert.Equal("↗ Enviado", vm.Blocks[1].StatusLabel);
        Assert.False(vm.HasNextBlock);

        string received2 = harness.Layout.Decode(harness.Injector.Delivered.Skip(countAfter1).ToList());
        Assert.Equal(vm.Blocks[1].Text, received2);
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "VmTyper.sln"))) return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new DirectoryNotFoundException("Raiz do repositório não encontrada.");
    }
}
