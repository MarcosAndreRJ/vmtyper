using VmTyper.Models;

namespace VmTyper.Services;

/// <summary>
/// Representação pura (sem controles WPF) dos valores exibidos e editados na
/// <see cref="Views.SettingsWindow"/>, permitindo testar o mapeamento ida e volta
/// e todas as regras de validação (§6 I).
/// </summary>
public sealed class SettingsFormState
{
    public string CharDelayText { get; set; } = "2";
    public string EnterDelayText { get; set; } = "20";
    public string CountdownText { get; set; } = "3";
    public string MethodTag { get; set; } = "Automatico";
    public string TargetLayoutTag { get; set; } = "HostForeground";
    public bool PreserveOriginalIndentation { get; set; }
    public List<CharacterSubstitution> Substitutions { get; set; } = new();
    public HotkeyDefinition StartHotkey { get; set; } = new() { Ctrl = true, Shift = true, VirtualKey = 0x0D };
    public HotkeyDefinition PauseResumeHotkey { get; set; } = new() { Ctrl = true, Shift = true, VirtualKey = 0x50 };
    public bool EnableTargetGuard { get; set; } = true;
    public bool TargetGuardStrict { get; set; }
    public bool ValidateTargetWindow { get; set; }
    public string ExpectedBrowserTag { get; set; } = "Chrome";
    public string QueueTextPersistenceTag { get; set; } = "Full";
    public bool DetailedTrace { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool AlwaysOnTop { get; set; }
    public bool ClearTextAfterCompletion { get; set; }
}

public sealed record SettingsValidationResult(
    bool IsValid,
    AppSettings? Settings,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

public static class SettingsMapper
{
    public const int MinDelayMs = 0;
    public const int MaxDelayMs = 2000;
    public const int MinCountdownSeconds = 0;
    public const int MaxCountdownSeconds = 30;

    public static SettingsFormState ToFormState(AppSettings s)
    {
        return new SettingsFormState
        {
            CharDelayText = s.CharDelayMs.ToString(),
            EnterDelayText = s.EnterDelayMs.ToString(),
            CountdownText = s.CountdownSeconds.ToString(),
            MethodTag = s.Method.ToString(),
            TargetLayoutTag = string.IsNullOrWhiteSpace(s.TargetLayout) ? "HostForeground" : s.TargetLayout,
            PreserveOriginalIndentation = s.PreserveOriginalIndentation,
            Substitutions = s.CharacterSubstitutions.Select(CloneSubstitution).ToList(),
            StartHotkey = CloneHotkey(s.StartHotkey),
            PauseResumeHotkey = CloneHotkey(s.PauseResumeHotkey),
            EnableTargetGuard = s.EnableTargetGuard,
            TargetGuardStrict = s.Features.TargetGuardStrict,
            ValidateTargetWindow = s.ValidateTargetWindow,
            ExpectedBrowserTag = s.ExpectedBrowser.ToString(),
            QueueTextPersistenceTag = s.QueueTextPersistence.ToString(),
            DetailedTrace = s.Features.DetailedTrace,
            MinimizeToTray = s.MinimizeToTray,
            StartMinimized = s.StartMinimized,
            AlwaysOnTop = s.AlwaysOnTop,
            ClearTextAfterCompletion = s.ClearTextAfterCompletion
        };
    }

    public static SettingsValidationResult TryBuildSettings(SettingsFormState form, AppSettings? baseSettings = null)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (!int.TryParse(form.CharDelayText, out int charDelay) || charDelay < MinDelayMs || charDelay > MaxDelayMs)
        {
            errors.Add($"Intervalo entre caracteres inválido (deve estar entre {MinDelayMs} e {MaxDelayMs} ms).");
        }

        if (!int.TryParse(form.EnterDelayText, out int enterDelay) || enterDelay < MinDelayMs || enterDelay > MaxDelayMs)
        {
            errors.Add($"Intervalo após Enter inválido (deve estar entre {MinDelayMs} e {MaxDelayMs} ms).");
        }

        if (!int.TryParse(form.CountdownText, out int countdown) || countdown < MinCountdownSeconds || countdown > MaxCountdownSeconds)
        {
            errors.Add($"Contagem regressiva inválida (deve estar entre {MinCountdownSeconds} e {MaxCountdownSeconds} s).");
        }

        var activeSubs = new List<CharacterSubstitution>();
        for (int i = 0; i < form.Substitutions.Count; i++)
        {
            var sub = form.Substitutions[i];
            if (string.IsNullOrEmpty(sub.From)) continue;

            if (sub.Mode == SubstitutionMode.Combinacao && sub.ComboVirtualKey == 0)
            {
                errors.Add($"Substituição na linha #{i + 1} ('{sub.From}'): o modo Combinação exige capturar uma tecla.");
            }
            activeSubs.Add(CloneSubstitution(sub));
        }

        var duplicateFroms = KeyStrokePlanner.FindDuplicateSubstitutionFroms(activeSubs);
        if (duplicateFroms.Count > 0)
        {
            warnings.Add($"Existem regras de substituição com o mesmo 'De' ({string.Join(", ", duplicateFroms)}); apenas a primeira regra de cada símbolo terá efeito.");
        }

        if (errors.Count > 0)
        {
            return new SettingsValidationResult(false, null, errors, warnings);
        }

        var result = baseSettings is not null ? CloneSettings(baseSettings) : new AppSettings();
        result.CharDelayMs = charDelay;
        result.EnterDelayMs = enterDelay;
        result.CountdownSeconds = countdown;
        result.Method = form.MethodTag switch
        {
            "TecladoFisico" => InputMethod.TecladoFisico,
            "Unicode" => InputMethod.Unicode,
            _ => InputMethod.Automatico
        };
        result.TargetLayout = string.IsNullOrWhiteSpace(form.TargetLayoutTag) ? "HostForeground" : form.TargetLayoutTag.Trim();
        result.PreserveOriginalIndentation = form.PreserveOriginalIndentation;
        result.CharacterSubstitutions = activeSubs;
        result.StartHotkey = CloneHotkey(form.StartHotkey);
        result.PauseResumeHotkey = CloneHotkey(form.PauseResumeHotkey);
        result.EnableTargetGuard = form.EnableTargetGuard;
        result.Features.TargetGuardStrict = form.TargetGuardStrict;
        result.Features.DetailedTrace = form.DetailedTrace;
        result.ValidateTargetWindow = form.ValidateTargetWindow;
        result.ExpectedBrowser = form.ExpectedBrowserTag switch
        {
            "Chrome" => ExpectedBrowser.Chrome,
            "Edge" => ExpectedBrowser.Edge,
            "Firefox" => ExpectedBrowser.Firefox,
            _ => ExpectedBrowser.Nenhum
        };
        result.QueueTextPersistence = form.QueueTextPersistenceTag switch
        {
            "Encrypted" => QueueTextPersistence.Encrypted,
            "None" => QueueTextPersistence.None,
            _ => QueueTextPersistence.Full
        };
        result.MinimizeToTray = form.MinimizeToTray;
        result.StartMinimized = form.StartMinimized;
        result.AlwaysOnTop = form.AlwaysOnTop;
        result.ClearTextAfterCompletion = form.ClearTextAfterCompletion;

        return new SettingsValidationResult(true, result, errors, warnings);
    }

    public static HotkeyDefinition CloneHotkey(HotkeyDefinition h) => new()
    {
        Ctrl = h.Ctrl,
        Shift = h.Shift,
        Alt = h.Alt,
        VirtualKey = h.VirtualKey
    };

    public static CharacterSubstitution CloneSubstitution(CharacterSubstitution sub) => new()
    {
        From = sub.From,
        To = sub.To,
        Mode = sub.Mode,
        ComboShift = sub.ComboShift,
        ComboCtrl = sub.ComboCtrl,
        ComboAlt = sub.ComboAlt,
        ComboVirtualKey = sub.ComboVirtualKey,
        ComboLabel = sub.ComboLabel
    };

    private static AppSettings CloneSettings(AppSettings s) => new()
    {
        CharDelayMs = s.CharDelayMs,
        EnterDelayMs = s.EnterDelayMs,
        Method = s.Method,
        CountdownSeconds = s.CountdownSeconds,
        PreserveOriginalIndentation = s.PreserveOriginalIndentation,
        CharacterSubstitutions = s.CharacterSubstitutions.Select(CloneSubstitution).ToList(),
        StartHotkey = CloneHotkey(s.StartHotkey),
        PauseResumeHotkey = CloneHotkey(s.PauseResumeHotkey),
        ValidateTargetWindow = s.ValidateTargetWindow,
        ExpectedBrowser = s.ExpectedBrowser,
        EnableTargetGuard = s.EnableTargetGuard,
        TargetLayout = s.TargetLayout,
        QueueTextPersistence = s.QueueTextPersistence,
        Features = new FeatureFlags
        {
            AdaptiveTiming = s.Features.AdaptiveTiming,
            TargetGuardStrict = s.Features.TargetGuardStrict,
            EmbeddedBrowser = s.Features.EmbeddedBrowser,
            CdpInput = s.Features.CdpInput,
            DetailedTrace = s.Features.DetailedTrace
        },
        MinimizeToTray = s.MinimizeToTray,
        StartMinimized = s.StartMinimized,
        AlwaysOnTop = s.AlwaysOnTop,
        ClearTextAfterCompletion = s.ClearTextAfterCompletion
    };
}
