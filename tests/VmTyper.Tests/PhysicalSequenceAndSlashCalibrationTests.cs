using System.Text.Json;
using VmTyper.Models;
using VmTyper.Native;
using VmTyper.Services;
using VmTyper.ViewModels;
using Xunit;

namespace VmTyper.Tests;

[Trait("Category", "Unit")]
public sealed class PhysicalSequenceAndSlashCalibrationTests : IDisposable
{
    private readonly EngineHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public void SlashCandidateDefinitions_ReflectCorrectVirtualKeysAndScanCodes()
    {
        var candidates = VmSlashAndAccentCandidates.GetSlashCandidates();
        Assert.Equal(5, candidates.Count);

        // 1. Numpad Divide: 0x6F, Scan 0x35, Extended True
        var numpad = candidates.First(c => c.Id == "NumpadDivide");
        Assert.Equal(Win32.VK_DIVIDE, numpad.SubstitutionTemplate.ComboVirtualKey);
        Assert.Equal(SubstitutionMode.Combinacao, numpad.Mode);

        // 2. ABNT2 Slash: 0xC1, Scan 0x73, Extended False
        var abnt2 = candidates.First(c => c.Id == "Abnt2Slash");
        Assert.Equal(SubstitutionMode.Sequencia, abnt2.Mode);
        Assert.Single(abnt2.SubstitutionTemplate.SequenceSteps);
        Assert.Equal(Win32.VK_ABNT_C1, abnt2.SubstitutionTemplate.SequenceSteps[0].VirtualKey);
        Assert.Equal(0x73, abnt2.SubstitutionTemplate.SequenceSteps[0].ScanCode);
        Assert.False(abnt2.SubstitutionTemplate.SequenceSteps[0].Extended);

        // 3. US Slash: 0xBF, Scan 0x35, Extended False
        var us = candidates.First(c => c.Id == "UsSlash");
        Assert.Equal(SubstitutionMode.Sequencia, us.Mode);
        Assert.Single(us.SubstitutionTemplate.SequenceSteps);
        Assert.Equal(Win32.VK_OEM_2, us.SubstitutionTemplate.SequenceSteps[0].VirtualKey);
        Assert.Equal(0x35, us.SubstitutionTemplate.SequenceSteps[0].ScanCode);
        Assert.False(us.SubstitutionTemplate.SequenceSteps[0].Extended);

        // 4. AltGr + Q
        var altGrQ = candidates.First(c => c.Id == "AltGrQ");
        Assert.Equal(SubstitutionMode.Combinacao, altGrQ.Mode);
        Assert.True(altGrQ.SubstitutionTemplate.ComboCtrl);
        Assert.True(altGrQ.SubstitutionTemplate.ComboAlt);
        Assert.Equal(0x51u, altGrQ.SubstitutionTemplate.ComboVirtualKey);

        // 5. Alt + 47 Numpad
        var alt47 = candidates.First(c => c.Id == "AltNumpad47");
        Assert.Equal(SubstitutionMode.Sequencia, alt47.Mode);
        var steps = alt47.SubstitutionTemplate.SequenceSteps;
        Assert.True(steps.Count >= 3);
        Assert.Equal(KeySequenceAction.KeyDown, steps[0].Action); // Alt down
        Assert.Equal(Win32.VK_LMENU, steps[0].VirtualKey);
        Assert.Equal(KeySequenceAction.KeyUp, steps[^1].Action);  // Alt up
        Assert.Equal(Win32.VK_LMENU, steps[^1].VirtualKey);
    }

    [Fact]
    public void QuestionMarkCandidateDefinitions_AreWellFormed()
    {
        var candidates = VmSlashAndAccentCandidates.GetQuestionCandidates();
        Assert.NotEmpty(candidates);

        var abntQuestion = candidates.First(c => c.Id == "Abnt2Question");
        Assert.True(abntQuestion.SubstitutionTemplate.ComboShift);
        Assert.Equal(Win32.VK_ABNT_C1, abntQuestion.SubstitutionTemplate.ComboVirtualKey);

        var usQuestion = candidates.First(c => c.Id == "UsQuestion");
        Assert.True(usQuestion.SubstitutionTemplate.ComboShift);
        Assert.Equal(Win32.VK_OEM_2, usQuestion.SubstitutionTemplate.ComboVirtualKey);
    }

    [Fact]
    public void AccentCandidateDefinitions_IncludeDeadKeyWithInterKeyDelay()
    {
        var acuteCandidates = VmSlashAndAccentCandidates.GetCandidatesForSymbol("á");
        Assert.NotEmpty(acuteCandidates);

        var deadAcute = acuteCandidates.First(c => c.Id == "DeadAcuteLayout");
        Assert.Equal(SubstitutionMode.Sequencia, deadAcute.Mode);
        var steps = deadAcute.SubstitutionTemplate.SequenceSteps;
        Assert.Equal(2, steps.Count);
        Assert.Equal(0x1A, steps[0].ScanCode);
        Assert.Equal(15, steps[0].DelayMs); // Pausa inter-tecla obrigatória de 15ms
        Assert.Equal(KeySequenceAction.Press, steps[0].Action);
        Assert.Equal((ushort)'A', steps[1].VirtualKey);
        Assert.False(steps[1].Shift);

        // Maiúscula
        var upperAcute = VmSlashAndAccentCandidates.GetCandidatesForSymbol("Á");
        var upperDead = upperAcute.First(c => c.Id == "DeadAcuteLayout");
        Assert.True(upperDead.SubstitutionTemplate.SequenceSteps[1].Shift);
    }

    [Fact]
    public void PhysicalSequencePlanning_ProducesPhysicalSequenceUnitWithExactEvents()
    {
        var planner = _h.Engine.Planner;
        var sub = new CharacterSubstitution
        {
            From = "/",
            Mode = SubstitutionMode.Sequencia,
            SequenceSteps = new List<KeySequenceStep>
            {
                new() { ScanCode = 0x35, Extended = false, Action = KeySequenceAction.Press, DelayMs = 25, Label = "US Slash" }
            }
        };

        var options = new PlanOptions(
            InputMethod.TecladoFisico,
            IntPtr.Zero,
            new[] { sub });

        var report = planner.Plan("/", options);
        Assert.Single(report.Units);
        var unit = report.Units[0];

        Assert.Equal(UnitKind.PhysicalSequence, unit.Kind);
        Assert.Equal(2, unit.Events.Count);
        Assert.Equal(KeyEventKind.Down, unit.Events[0].Kind);
        Assert.Equal(0x35, unit.Events[0].Code);
        Assert.False(unit.Events[0].Extended);

        Assert.Equal(KeyEventKind.Up, unit.Events[1].Kind);
        Assert.Equal(0x35, unit.Events[1].Code);
        Assert.Equal(25, unit.Events[1].DelayAfterMs);
    }

    [Fact]
    public void PhysicalSequencePlanning_WithModifiersAndAltGr_ProducesProperDownUpOrder()
    {
        var planner = _h.Engine.Planner;
        var sub = new CharacterSubstitution
        {
            From = "@",
            Mode = SubstitutionMode.Sequencia,
            SequenceSteps = new List<KeySequenceStep>
            {
                new() { VirtualKey = 0x51, AltGr = true, Action = KeySequenceAction.Press, Label = "AltGr+Q" }
            }
        };

        var options = new PlanOptions(InputMethod.TecladoFisico, IntPtr.Zero, new[] { sub });
        var report = planner.Plan("@", options);

        Assert.Single(report.Units);
        var events = report.Units[0].Events;
        // AltGr triggers Ctrl + Alt down before key down, and key up before Alt + Ctrl up
        Assert.True(events.Count >= 6);
        Assert.Equal(KeyEventKind.Down, events[0].Kind); // Ctrl down
        Assert.Equal(KeyEventKind.Down, events[1].Kind); // Alt down
        Assert.Equal(KeyEventKind.Down, events[2].Kind); // Main key down
        Assert.Equal(KeyEventKind.Up, events[3].Kind);   // Main key up
        Assert.Equal(KeyEventKind.Up, events[4].Kind);   // Alt up
        Assert.Equal(KeyEventKind.Up, events[5].Kind);   // Ctrl up
    }

    [Fact]
    public async Task PhysicalSequenceExecution_TypesKeysWithDelaysAndEmptiesTracker()
    {
        var sub = new CharacterSubstitution
        {
            From = "/",
            Mode = SubstitutionMode.Sequencia,
            SequenceSteps = new List<KeySequenceStep>
            {
                new() { ScanCode = 0x35, Extended = true, Action = KeySequenceAction.Press, DelayMs = 10, Label = "Numpad Divide" }
            }
        };

        var options = EngineHarness.Options(InputMethod.TecladoFisico, substitutions: new List<CharacterSubstitution> { sub });

        await _h.TypeAsync("/", options);

        var a = _h.Analyze();
        Assert.Empty(a.StuckKeys);
        Assert.Equal(0, _h.Engine.Tracker.Count);

        var delivered = _h.Injector.Delivered;
        Assert.Equal(2, delivered.Count);
        Assert.Equal(0x35, delivered[0].Scan);
        Assert.True(delivered[0].IsExtended);
        Assert.False(delivered[0].IsKeyUp);

        Assert.Equal(0x35, delivered[1].Scan);
        Assert.True(delivered[1].IsExtended);
        Assert.True(delivered[1].IsKeyUp);
    }

    [Fact]
    public void DeadKeyInterKeyDelay_IsAppliedToAccentuationInPhysicalMode()
    {
        var planner = _h.Engine.Planner;
        var options = new PlanOptions(InputMethod.TecladoFisico, IntPtr.Zero, Array.Empty<CharacterSubstitution>());

        var report = planner.Plan("ã", options);
        Assert.Single(report.Units);
        var unit = report.Units[0];

        // ã decompõe em ~ e a -> total 4 eventos: [~ Down, ~ Up, a Down, a Up]
        Assert.Equal(4, unit.Events.Count);
        var deadKeyUp = unit.Events[1];
        Assert.Equal(KeyEventKind.Up, deadKeyUp.Kind);
        Assert.True(deadKeyUp.DelayAfterMs >= KeyStrokePlanner.DefaultDeadKeyInterKeyDelayMs,
            $"Esperava DelayAfterMs >= {KeyStrokePlanner.DefaultDeadKeyInterKeyDelayMs}, mas obteve {deadKeyUp.DelayAfterMs}");
    }

    [Fact]
    public void FlushDeadKeyState_SendsSpaceAndBackspace()
    {
        _h.Engine.FlushDeadKeyState();

        var delivered = _h.Injector.Delivered;
        // Space Down, Space Up, Backspace Down, Backspace Up = 4 eventos
        Assert.Equal(4, delivered.Count);

        Assert.False(delivered[0].IsKeyUp); // Space Down
        Assert.True(delivered[1].IsKeyUp);  // Space Up

        Assert.False(delivered[2].IsKeyUp); // Backspace Down
        Assert.True(delivered[3].IsKeyUp);  // Backspace Up
    }

    [Fact]
    public void DiagnosticsViewModel_CalibrationSymbolChange_PopulatesCandidatesAndUpdatesSteps()
    {
        var vm = new DiagnosticsViewModel();
        vm.InitializeCalibration();

        Assert.NotEmpty(vm.AvailableSymbols);
        Assert.Contains("/", vm.AvailableSymbols);
        Assert.Contains("á", vm.AvailableSymbols);

        vm.SelectedCalibrationSymbol = "/";
        Assert.NotEmpty(vm.AvailableCandidates);
        Assert.NotNull(vm.SelectedCandidate);
        Assert.NotEmpty(vm.CalibrationSequenceSteps);

        vm.SelectedCalibrationSymbol = "á";
        Assert.NotEmpty(vm.AvailableCandidates);
        Assert.NotNull(vm.SelectedCandidate);
        Assert.Contains(vm.AvailableCandidates, c => c.Id == "DeadAcuteLayout");
    }

    [Fact]
    public void DiagnosticsViewModel_FeedbackAndApplyCalibration_PreservesExistingSubstitutions()
    {
        var vm = new DiagnosticsViewModel();
        vm.SelectedCalibrationSymbol = "/";

        var targetCandidate = vm.AvailableCandidates.First(c => c.Id == "UsSlash");
        vm.SelectedCandidate = targetCandidate;

        vm.CalibrationObservedResult = "/";
        vm.SetCalibrationFeedback(DeliverySemanticState.Confirmed, "Validado");
        Assert.Equal(DeliverySemanticState.Confirmed, vm.CalibrationDeliveryBadge.State);

        var settings = new AppSettings
        {
            CharacterSubstitutions = new List<CharacterSubstitution>
            {
                new() { From = "preexisting", To = "test", Mode = SubstitutionMode.Texto }
            }
        };

        var updated = vm.ApplyApprovedCalibration(settings);
        Assert.Equal(2, updated.CharacterSubstitutions.Count);
        Assert.Equal("preexisting", updated.CharacterSubstitutions[0].From);

        var savedRule = updated.CharacterSubstitutions[1];
        Assert.Equal("/", savedRule.From);
        Assert.Equal(SubstitutionMode.Sequencia, savedRule.Mode);
        Assert.Single(savedRule.SequenceSteps);
        Assert.Equal(0x35, savedRule.SequenceSteps[0].ScanCode);
    }

    [Fact]
    public void SettingsMapper_And_Serialization_PreservesSequenceSteps()
    {
        var sub = new CharacterSubstitution
        {
            From = "/",
            Mode = SubstitutionMode.Sequencia,
            SequenceSteps = new List<KeySequenceStep>
            {
                new() { ScanCode = 0x35, Extended = true, Action = KeySequenceAction.Press, DelayMs = 20, Label = "Numpad" }
            }
        };

        var original = new AppSettings
        {
            CharacterSubstitutions = new List<CharacterSubstitution> { sub }
        };

        // Clonagem
        var clone = SettingsMapper.CloneSettings(original);
        Assert.Single(clone.CharacterSubstitutions);
        Assert.Equal(SubstitutionMode.Sequencia, clone.CharacterSubstitutions[0].Mode);
        Assert.Single(clone.CharacterSubstitutions[0].SequenceSteps);
        Assert.Equal(0x35, clone.CharacterSubstitutions[0].SequenceSteps[0].ScanCode);
        Assert.True(clone.CharacterSubstitutions[0].SequenceSteps[0].Extended);
        Assert.Equal(20, clone.CharacterSubstitutions[0].SequenceSteps[0].DelayMs);

        // Serialização JSON roundtrip
        string json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<AppSettings>(json);
        Assert.NotNull(deserialized);
        Assert.Single(deserialized.CharacterSubstitutions);
        Assert.Equal(SubstitutionMode.Sequencia, deserialized.CharacterSubstitutions[0].Mode);
        Assert.Single(deserialized.CharacterSubstitutions[0].SequenceSteps);
        Assert.Equal(0x35, deserialized.CharacterSubstitutions[0].SequenceSteps[0].ScanCode);
    }
}
