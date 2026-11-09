using VmTyper.Models;
using VmTyper.Services;
using VmTyper.Tests.Fakes;

namespace VmTyper.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    [Trait("Known", "I")]
    public void ComboSubstitution_WithoutCapturedKey_IsRejectedWithLineNumber()
    {
        var form = SettingsMapper.ToFormState(new AppSettings());
        form.Substitutions.Add(new CharacterSubstitution
        {
            From = "a",
            To = "b",
            Mode = SubstitutionMode.Texto
        });
        form.Substitutions.Add(new CharacterSubstitution
        {
            From = "?",
            Mode = SubstitutionMode.Combinacao,
            ComboVirtualKey = 0 // nenhuma tecla capturada
        });

        var result = SettingsMapper.TryBuildSettings(form);

        Assert.False(result.IsValid);
        Assert.Null(result.Settings);
        var err = Assert.Single(result.Errors);
        Assert.Contains("#2", err);
        Assert.Contains("?", err);
    }

    [Theory]
    [InlineData("-1", "20", "3", "Intervalo entre caracteres")]
    [InlineData("2001", "20", "3", "Intervalo entre caracteres")]
    [InlineData("abc", "20", "3", "Intervalo entre caracteres")]
    [InlineData("10", "-5", "3", "Intervalo após Enter")]
    [InlineData("10", "2500", "3", "Intervalo após Enter")]
    [InlineData("10", "20", "-1", "Contagem regressiva")]
    [InlineData("10", "20", "31", "Contagem regressiva")]
    [Trait("Known", "I")]
    public void RangeValidation_RejectsOutOfBoundsValues(
        string charDelay, string enterDelay, string countdown, string expectedFieldFragment)
    {
        var form = SettingsMapper.ToFormState(new AppSettings());
        form.CharDelayText = charDelay;
        form.EnterDelayText = enterDelay;
        form.CountdownText = countdown;

        var result = SettingsMapper.TryBuildSettings(form);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains(expectedFieldFragment, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Known", "I")]
    public void DuplicateSubstitutionFrom_EmitsWarning_ButRemainsValid()
    {
        var form = SettingsMapper.ToFormState(new AppSettings());
        form.Substitutions.Add(new CharacterSubstitution { From = "/", To = "|" });
        form.Substitutions.Add(new CharacterSubstitution { From = "/", To = "\\" });

        var result = SettingsMapper.TryBuildSettings(form);

        Assert.True(result.IsValid);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("/", warning);
    }

    [Fact]
    [Trait("Known", "I")]
    public void SettingsFormState_RoundTripsEveryField()
    {
        var original = new AppSettings
        {
            CharDelayMs = 45,
            EnterDelayMs = 180,
            CountdownSeconds = 7,
            Method = InputMethod.TecladoFisico,
            TargetLayout = "00010416",
            PreserveOriginalIndentation = true,
            CharacterSubstitutions = new List<CharacterSubstitution>
            {
                new()
                {
                    From = "/",
                    Mode = SubstitutionMode.Combinacao,
                    ComboCtrl = true,
                    ComboAlt = true,
                    ComboShift = false,
                    ComboVirtualKey = 0x51,
                    ComboLabel = "Ctrl + Alt + Q"
                }
            },
            StartHotkey = new HotkeyDefinition { Ctrl = true, Alt = true, Shift = false, VirtualKey = 0x70 },
            PauseResumeHotkey = new HotkeyDefinition { Ctrl = false, Alt = true, Shift = true, VirtualKey = 0x71 },
            EnableTargetGuard = false,
            ValidateTargetWindow = true,
            ExpectedBrowser = ExpectedBrowser.Edge,
            QueueTextPersistence = QueueTextPersistence.Encrypted,
            Features = new FeatureFlags
            {
                TargetGuardStrict = true,
                DetailedTrace = true,
                AdaptiveTiming = true
            },
            MinimizeToTray = false,
            StartMinimized = true,
            AlwaysOnTop = true,
            ClearTextAfterCompletion = true
        };

        var form = SettingsMapper.ToFormState(original);
        var buildResult = SettingsMapper.TryBuildSettings(form, original);

        Assert.True(buildResult.IsValid);
        var roundTripped = buildResult.Settings!;

        AssertEqualSettings(original, roundTripped);
    }

    [Fact]
    [Trait("Known", "I")]
    public void SettingsService_JsonRoundTrip_PreservesAllFields()
    {
        var path = _dir.File("settings.json");
        var service = new SettingsService(path);

        var original = new AppSettings
        {
            CharDelayMs = 15,
            EnterDelayMs = 75,
            CountdownSeconds = 5,
            Method = InputMethod.Unicode,
            TargetLayout = "00020409",
            PreserveOriginalIndentation = true,
            CharacterSubstitutions = new List<CharacterSubstitution>
            {
                new() { From = ";", To = ":", Mode = SubstitutionMode.Texto },
                new()
                {
                    From = "?",
                    Mode = SubstitutionMode.Combinacao,
                    ComboShift = true,
                    ComboVirtualKey = 0xBF,
                    ComboLabel = "Shift + / ?"
                }
            },
            StartHotkey = new HotkeyDefinition { Ctrl = true, Shift = true, VirtualKey = 0x0D },
            PauseResumeHotkey = new HotkeyDefinition { Ctrl = true, Shift = true, VirtualKey = 0x50 },
            EnableTargetGuard = true,
            ValidateTargetWindow = true,
            ExpectedBrowser = ExpectedBrowser.Firefox,
            QueueTextPersistence = QueueTextPersistence.None,
            Features = new FeatureFlags
            {
                TargetGuardStrict = true,
                DetailedTrace = false,
                AdaptiveTiming = false
            },
            MinimizeToTray = false,
            StartMinimized = true,
            AlwaysOnTop = true,
            ClearTextAfterCompletion = true
        };

        service.Save(original);
        var loaded = new SettingsService(path).Load();

        AssertEqualSettings(original, loaded);
    }

    private static void AssertEqualSettings(AppSettings expected, AppSettings actual)
    {
        Assert.Equal(expected.CharDelayMs, actual.CharDelayMs);
        Assert.Equal(expected.EnterDelayMs, actual.EnterDelayMs);
        Assert.Equal(expected.CountdownSeconds, actual.CountdownSeconds);
        Assert.Equal(expected.Method, actual.Method);
        Assert.Equal(expected.TargetLayout, actual.TargetLayout);
        Assert.Equal(expected.PreserveOriginalIndentation, actual.PreserveOriginalIndentation);
        Assert.Equal(expected.EnableTargetGuard, actual.EnableTargetGuard);
        Assert.Equal(expected.ValidateTargetWindow, actual.ValidateTargetWindow);
        Assert.Equal(expected.ExpectedBrowser, actual.ExpectedBrowser);
        Assert.Equal(expected.QueueTextPersistence, actual.QueueTextPersistence);
        Assert.Equal(expected.Features.TargetGuardStrict, actual.Features.TargetGuardStrict);
        Assert.Equal(expected.Features.DetailedTrace, actual.Features.DetailedTrace);
        Assert.Equal(expected.Features.AdaptiveTiming, actual.Features.AdaptiveTiming);
        Assert.Equal(expected.MinimizeToTray, actual.MinimizeToTray);
        Assert.Equal(expected.StartMinimized, actual.StartMinimized);
        Assert.Equal(expected.AlwaysOnTop, actual.AlwaysOnTop);
        Assert.Equal(expected.ClearTextAfterCompletion, actual.ClearTextAfterCompletion);

        Assert.Equal(expected.StartHotkey.Ctrl, actual.StartHotkey.Ctrl);
        Assert.Equal(expected.StartHotkey.Shift, actual.StartHotkey.Shift);
        Assert.Equal(expected.StartHotkey.Alt, actual.StartHotkey.Alt);
        Assert.Equal(expected.StartHotkey.VirtualKey, actual.StartHotkey.VirtualKey);

        Assert.Equal(expected.PauseResumeHotkey.Ctrl, actual.PauseResumeHotkey.Ctrl);
        Assert.Equal(expected.PauseResumeHotkey.Shift, actual.PauseResumeHotkey.Shift);
        Assert.Equal(expected.PauseResumeHotkey.Alt, actual.PauseResumeHotkey.Alt);
        Assert.Equal(expected.PauseResumeHotkey.VirtualKey, actual.PauseResumeHotkey.VirtualKey);

        Assert.Equal(expected.CharacterSubstitutions.Count, actual.CharacterSubstitutions.Count);
        for (int i = 0; i < expected.CharacterSubstitutions.Count; i++)
        {
            var eSub = expected.CharacterSubstitutions[i];
            var aSub = actual.CharacterSubstitutions[i];
            Assert.Equal(eSub.From, aSub.From);
            Assert.Equal(eSub.To, aSub.To);
            Assert.Equal(eSub.Mode, aSub.Mode);
            Assert.Equal(eSub.ComboShift, aSub.ComboShift);
            Assert.Equal(eSub.ComboCtrl, aSub.ComboCtrl);
            Assert.Equal(eSub.ComboAlt, aSub.ComboAlt);
            Assert.Equal(eSub.ComboVirtualKey, aSub.ComboVirtualKey);
            Assert.Equal(eSub.ComboLabel, aSub.ComboLabel);
        }
    }
}
