using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using VmTyper.Helpers;
using VmTyper.Models;
using VmTyper.Services;

namespace VmTyper.ViewModels;

/// <summary>
/// ViewModel de configurações com binding e validação via <see cref="INotifyDataErrorInfo"/> (Fase 5 §3).
/// Substitui a cópia manual de campos no code-behind de <c>SettingsWindow</c>, suporta restauração
/// de padrões por aba e global e calcula a prévia dry-run do perfil ativo/personalizado.
/// </summary>
public sealed class SettingsViewModel : ObservableObject, INotifyDataErrorInfo
{
    public const string TabDigitacao = "Digitacao";
    public const string TabSeguranca = "Seguranca";
    public const string TabSubstituicoes = "Substituicoes";
    public const string TabAtalhos = "Atalhos";
    public const string TabPersistencia = "Persistencia";
    public const string TabDiagnostico = "Diagnostico";
    public const string TabNavegadorIntegrado = "NavegadorIntegrado";
    public const string TabExperimental = "Experimental";

    private const string SamplePreviewText =
        "Exemplo de prévia para estimativa dry-run:\n" +
        "carro pessoa ação ~a ^^ 1234567890!\n" +
        "    if (valor >= 10) { return true; }";

    private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);
    private AppSettings _baseSettings;

    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    public bool HasErrors => _errors.Values.Any(l => l.Count > 0);

    public IEnumerable GetErrors(string? propertyName)
    {
        if (string.IsNullOrEmpty(propertyName))
        {
            return _errors.Values.SelectMany(x => x).ToList();
        }
        return _errors.TryGetValue(propertyName, out var list) ? list : Enumerable.Empty<string>();
    }

    public ObservableCollection<CharacterSubstitution> Substitutions { get; } = new();

    // --- Aba 1: Digitação ---
    private string _charDelayText = "2";
    public string CharDelayText
    {
        get => _charDelayText;
        set { if (SetField(ref _charDelayText, value)) ValidateAndRefreshPreview(); }
    }

    private string _enterDelayText = "20";
    public string EnterDelayText
    {
        get => _enterDelayText;
        set { if (SetField(ref _enterDelayText, value)) ValidateAndRefreshPreview(); }
    }

    private string _countdownText = "3";
    public string CountdownText
    {
        get => _countdownText;
        set { if (SetField(ref _countdownText, value)) ValidateAll(); }
    }

    private string _methodTag = "Automatico";
    public string MethodTag
    {
        get => _methodTag;
        set => SetField(ref _methodTag, value);
    }

    private string _targetLayoutTag = "HostForeground";
    public string TargetLayoutTag
    {
        get => _targetLayoutTag;
        set => SetField(ref _targetLayoutTag, value);
    }

    private bool _adaptiveTiming;
    public bool AdaptiveTiming
    {
        get => _adaptiveTiming;
        set
        {
            if (SetField(ref _adaptiveTiming, value))
            {
                if (!value)
                {
                    TypingProfileTag = "Legacy";
                }
                OnPropertyChanged(nameof(IsCustomProfileSelected));
                ValidateAndRefreshPreview();
            }
        }
    }

    private string _typingProfileTag = "Legacy";
    public string TypingProfileTag
    {
        get => _typingProfileTag;
        set
        {
            if (SetField(ref _typingProfileTag, value))
            {
                OnPropertyChanged(nameof(IsCustomProfileSelected));
                ValidateAndRefreshPreview();
            }
        }
    }

    public bool IsCustomProfileSelected => AdaptiveTiming && string.Equals(TypingProfileTag, "Custom", StringComparison.OrdinalIgnoreCase);

    private bool _preserveOriginalIndentation;
    public bool PreserveOriginalIndentation
    {
        get => _preserveOriginalIndentation;
        set => SetField(ref _preserveOriginalIndentation, value);
    }

    // --- Editor do Perfil Personalizado (§2 da F2 + §3 da F5) ---
    private string _customHoldMinText = "8";
    public string CustomHoldMinText
    {
        get => _customHoldMinText;
        set { if (SetField(ref _customHoldMinText, value)) ValidateAndRefreshPreview(); }
    }

    private string _customHoldMaxText = "18";
    public string CustomHoldMaxText
    {
        get => _customHoldMaxText;
        set { if (SetField(ref _customHoldMaxText, value)) ValidateAndRefreshPreview(); }
    }

    private string _customGapMinText = "20";
    public string CustomGapMinText
    {
        get => _customGapMinText;
        set { if (SetField(ref _customGapMinText, value)) ValidateAndRefreshPreview(); }
    }

    private string _customGapMaxText = "45";
    public string CustomGapMaxText
    {
        get => _customGapMaxText;
        set { if (SetField(ref _customGapMaxText, value)) ValidateAndRefreshPreview(); }
    }

    private string _customEnterGapMinText = "40";
    public string CustomEnterGapMinText
    {
        get => _customEnterGapMinText;
        set { if (SetField(ref _customEnterGapMinText, value)) ValidateAndRefreshPreview(); }
    }

    private string _customEnterGapMaxText = "80";
    public string CustomEnterGapMaxText
    {
        get => _customEnterGapMaxText;
        set { if (SetField(ref _customEnterGapMaxText, value)) ValidateAndRefreshPreview(); }
    }

    private string _customSameKeyPenaltyText = "25";
    public string CustomSameKeyPenaltyText
    {
        get => _customSameKeyPenaltyText;
        set { if (SetField(ref _customSameKeyPenaltyText, value)) ValidateAndRefreshPreview(); }
    }

    private string _customModifierLeadText = "8";
    public string CustomModifierLeadText
    {
        get => _customModifierLeadText;
        set { if (SetField(ref _customModifierLeadText, value)) ValidateAndRefreshPreview(); }
    }

    private string _customDeadKeyLeadText = "12";
    public string CustomDeadKeyLeadText
    {
        get => _customDeadKeyLeadText;
        set { if (SetField(ref _customDeadKeyLeadText, value)) ValidateAndRefreshPreview(); }
    }

    private string _customLineIndentPauseText = "20";
    public string CustomLineIndentPauseText
    {
        get => _customLineIndentPauseText;
        set { if (SetField(ref _customLineIndentPauseText, value)) ValidateAndRefreshPreview(); }
    }

    private string _customCorrelationAlphaText = "0.25";
    public string CustomCorrelationAlphaText
    {
        get => _customCorrelationAlphaText;
        set { if (SetField(ref _customCorrelationAlphaText, value)) ValidateAndRefreshPreview(); }
    }

    private string _customBurstLengthText = "0";
    public string CustomBurstLengthText
    {
        get => _customBurstLengthText;
        set { if (SetField(ref _customBurstLengthText, value)) ValidateAndRefreshPreview(); }
    }

    private string _customBurstPauseMinText = "0";
    public string CustomBurstPauseMinText
    {
        get => _customBurstPauseMinText;
        set { if (SetField(ref _customBurstPauseMinText, value)) ValidateAndRefreshPreview(); }
    }

    private string _customBurstPauseMaxText = "0";
    public string CustomBurstPauseMaxText
    {
        get => _customBurstPauseMaxText;
        set { if (SetField(ref _customBurstPauseMaxText, value)) ValidateAndRefreshPreview(); }
    }

    private string _dryRunPreviewText = string.Empty;
    public string DryRunPreviewText
    {
        get => _dryRunPreviewText;
        private set => SetField(ref _dryRunPreviewText, value);
    }

    // --- Aba 2: Segurança de foco ---
    private bool _enableTargetGuard = true;
    public bool EnableTargetGuard
    {
        get => _enableTargetGuard;
        set => SetField(ref _enableTargetGuard, value);
    }

    private bool _targetGuardStrict;
    public bool TargetGuardStrict
    {
        get => _targetGuardStrict;
        set => SetField(ref _targetGuardStrict, value);
    }

    private bool _validateTargetWindow;
    public bool ValidateTargetWindow
    {
        get => _validateTargetWindow;
        set => SetField(ref _validateTargetWindow, value);
    }

    private string _expectedBrowserTag = "Chrome";
    public string ExpectedBrowserTag
    {
        get => _expectedBrowserTag;
        set => SetField(ref _expectedBrowserTag, value);
    }

    private string _resumeCountdownText = "2";
    public string ResumeCountdownText
    {
        get => _resumeCountdownText;
        set { if (SetField(ref _resumeCountdownText, value)) ValidateAll(); }
    }

    // --- Aba 3: Substituições ---
    private string _duplicateSubstitutionsWarning = string.Empty;
    public string DuplicateSubstitutionsWarning
    {
        get => _duplicateSubstitutionsWarning;
        private set
        {
            if (SetField(ref _duplicateSubstitutionsWarning, value))
            {
                OnPropertyChanged(nameof(HasDuplicateSubstitutionsWarning));
            }
        }
    }

    public bool HasDuplicateSubstitutionsWarning => !string.IsNullOrEmpty(DuplicateSubstitutionsWarning);

    // --- Aba 4: Atalhos e Janela ---
    private HotkeyDefinition _startHotkey = new() { Ctrl = true, Shift = true, VirtualKey = 0x0D };
    public HotkeyDefinition StartHotkey
    {
        get => _startHotkey;
        set
        {
            _startHotkey = value;
            OnPropertyChanged(nameof(StartHotkey));
            OnPropertyChanged(nameof(StartHotkeyLabel));
        }
    }

    public string StartHotkeyLabel => _startHotkey.ToString();

    private HotkeyDefinition _pauseResumeHotkey = new() { Ctrl = true, Shift = true, VirtualKey = 0x50 };
    public HotkeyDefinition PauseResumeHotkey
    {
        get => _pauseResumeHotkey;
        set
        {
            _pauseResumeHotkey = value;
            OnPropertyChanged(nameof(PauseResumeHotkey));
            OnPropertyChanged(nameof(PauseResumeHotkeyLabel));
        }
    }

    public string PauseResumeHotkeyLabel => _pauseResumeHotkey.ToString();

    private bool _minimizeToTray = true;
    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set => SetField(ref _minimizeToTray, value);
    }

    private bool _startMinimized;
    public bool StartMinimized
    {
        get => _startMinimized;
        set => SetField(ref _startMinimized, value);
    }

    private bool _alwaysOnTop;
    public bool AlwaysOnTop
    {
        get => _alwaysOnTop;
        set => SetField(ref _alwaysOnTop, value);
    }

    // --- Aba 5: Persistência ---
    private string _queueTextPersistenceTag = "Full";
    public string QueueTextPersistenceTag
    {
        get => _queueTextPersistenceTag;
        set => SetField(ref _queueTextPersistenceTag, value);
    }

    private bool _clearTextAfterCompletion;
    public bool ClearTextAfterCompletion
    {
        get => _clearTextAfterCompletion;
        set => SetField(ref _clearTextAfterCompletion, value);
    }

    public string PersistenceFolderPath { get; }

    // --- Aba 6: Diagnóstico ---
    private bool _enableDiagnosticsJsonl = true;
    public bool EnableDiagnosticsJsonl
    {
        get => _enableDiagnosticsJsonl;
        set => SetField(ref _enableDiagnosticsJsonl, value);
    }

    private bool _detailedTrace;
    public bool DetailedTrace
    {
        get => _detailedTrace;
        set => SetField(ref _detailedTrace, value);
    }

    private string _diagnosticsRetentionText = "20";
    public string DiagnosticsRetentionText
    {
        get => _diagnosticsRetentionText;
        set { if (SetField(ref _diagnosticsRetentionText, value)) ValidateAll(); }
    }

    // --- Aba 7: Navegador integrado ---
    private bool _embeddedBrowser;
    public bool EmbeddedBrowser
    {
        get => _embeddedBrowser;
        set => SetField(ref _embeddedBrowser, value);
    }

    public string EmbeddedBrowserProfilePath { get; }

    // --- Aba 8: Experimental ---
    private bool _cdpInput;
    public bool CdpInput
    {
        get => _cdpInput;
        set => SetField(ref _cdpInput, value);
    }

    public SettingsViewModel(AppSettings? initialSettings = null, string? persistenceFolderPath = null, string? embeddedProfilePath = null)
    {
        _baseSettings = initialSettings is not null ? SettingsMapper.CloneSettings(initialSettings) : new AppSettings();
        PersistenceFolderPath = persistenceFolderPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VmTyper");
        EmbeddedBrowserProfilePath = embeddedProfilePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VmTyper", "WebView2Profile");

        Substitutions.CollectionChanged += (_, _) => ValidateAll();
        LoadFromSettings(_baseSettings);
    }

    public void LoadFromSettings(AppSettings s)
    {
        _baseSettings = SettingsMapper.CloneSettings(s);
        var form = SettingsMapper.ToFormState(s);

        _charDelayText = form.CharDelayText;
        _enterDelayText = form.EnterDelayText;
        _countdownText = form.CountdownText;
        _resumeCountdownText = form.ResumeCountdownText;
        _methodTag = form.MethodTag;
        _targetLayoutTag = form.TargetLayoutTag;
        _adaptiveTiming = form.AdaptiveTiming;
        _typingProfileTag = form.AdaptiveTiming ? form.TypingProfileTag : "Legacy";
        _preserveOriginalIndentation = form.PreserveOriginalIndentation;

        LoadCustomProfileFields(s.CustomProfile);

        Substitutions.Clear();
        foreach (var sub in form.Substitutions)
        {
            AttachSubstitution(sub);
            Substitutions.Add(sub);
        }

        _startHotkey = SettingsMapper.CloneHotkey(form.StartHotkey);
        _pauseResumeHotkey = SettingsMapper.CloneHotkey(form.PauseResumeHotkey);
        _enableTargetGuard = form.EnableTargetGuard;
        _targetGuardStrict = form.TargetGuardStrict;
        _validateTargetWindow = form.ValidateTargetWindow;
        _expectedBrowserTag = form.ExpectedBrowserTag;
        _queueTextPersistenceTag = form.QueueTextPersistenceTag;
        _detailedTrace = form.DetailedTrace;
        _enableDiagnosticsJsonl = form.EnableDiagnosticsJsonl;
        _diagnosticsRetentionText = form.DiagnosticsRetentionText;
        _embeddedBrowser = form.EmbeddedBrowser;
        _cdpInput = form.CdpInput;
        _minimizeToTray = form.MinimizeToTray;
        _startMinimized = form.StartMinimized;
        _alwaysOnTop = form.AlwaysOnTop;
        _clearTextAfterCompletion = form.ClearTextAfterCompletion;

        NotifyAllPropertiesChanged();
        ValidateAndRefreshPreview();
    }

    private void LoadCustomProfileFields(TypingProfile? custom)
    {
        var c = custom ?? TypingProfile.Compatibility();
        _customHoldMinText = c.HoldMs.Min.ToString(CultureInfo.InvariantCulture);
        _customHoldMaxText = c.HoldMs.Max.ToString(CultureInfo.InvariantCulture);
        _customGapMinText = c.GapMs.Min.ToString(CultureInfo.InvariantCulture);
        _customGapMaxText = c.GapMs.Max.ToString(CultureInfo.InvariantCulture);
        _customEnterGapMinText = c.EnterPauseMs.Min.ToString(CultureInfo.InvariantCulture);
        _customEnterGapMaxText = c.EnterPauseMs.Max.ToString(CultureInfo.InvariantCulture);
        _customSameKeyPenaltyText = c.SameKeyMinGapMs.ToString(CultureInfo.InvariantCulture);
        _customModifierLeadText = c.PunctuationPauseMs.Min.ToString(CultureInfo.InvariantCulture);
        _customDeadKeyLeadText = c.PunctuationPauseMs.Max.ToString(CultureInfo.InvariantCulture);
        _customLineIndentPauseText = ((int)Math.Round((c.CodeLineMultiplier - 1.0) * 100)).ToString(CultureInfo.InvariantCulture);
        _customCorrelationAlphaText = c.Correlation.ToString("0.##", CultureInfo.InvariantCulture);
        _customBurstLengthText = ((int)Math.Round(c.WordPauseChance * 100)).ToString(CultureInfo.InvariantCulture);
        _customBurstPauseMinText = c.WordPauseMs.Min.ToString(CultureInfo.InvariantCulture);
        _customBurstPauseMaxText = c.WordPauseMs.Max.ToString(CultureInfo.InvariantCulture);
    }

    public void AddSubstitution(CharacterSubstitution? sub = null)
    {
        var item = sub ?? new CharacterSubstitution();
        AttachSubstitution(item);
        Substitutions.Add(item);
        ValidateAll();
    }

    public void RemoveSubstitution(CharacterSubstitution item)
    {
        Substitutions.Remove(item);
        ValidateAll();
    }

    private void AttachSubstitution(CharacterSubstitution sub)
    {
        sub.PropertyChanged += (_, _) => ValidateAll();
    }

    public void SetPresetDelays(int charDelayMs, int enterDelayMs)
    {
        CharDelayText = charDelayMs.ToString(CultureInfo.InvariantCulture);
        EnterDelayText = enterDelayMs.ToString(CultureInfo.InvariantCulture);
    }

    public void RestoreAllDefaults()
    {
        LoadFromSettings(new AppSettings());
    }

    public void RestoreTabDefaults(string tabKey)
    {
        var defaults = new AppSettings();
        switch (tabKey)
        {
            case TabDigitacao:
                CharDelayText = defaults.CharDelayMs.ToString(CultureInfo.InvariantCulture);
                EnterDelayText = defaults.EnterDelayMs.ToString(CultureInfo.InvariantCulture);
                CountdownText = defaults.CountdownSeconds.ToString(CultureInfo.InvariantCulture);
                MethodTag = defaults.Method.ToString();
                TargetLayoutTag = defaults.TargetLayout;
                AdaptiveTiming = defaults.Features.AdaptiveTiming;
                TypingProfileTag = defaults.TypingProfile;
                PreserveOriginalIndentation = defaults.PreserveOriginalIndentation;
                LoadCustomProfileFields(defaults.CustomProfile);
                NotifyAllPropertiesChanged();
                ValidateAndRefreshPreview();
                break;

            case TabSeguranca:
                EnableTargetGuard = defaults.EnableTargetGuard;
                TargetGuardStrict = defaults.Features.TargetGuardStrict;
                ValidateTargetWindow = defaults.ValidateTargetWindow;
                ExpectedBrowserTag = defaults.ExpectedBrowser.ToString();
                ResumeCountdownText = defaults.ResumeCountdownSeconds.ToString(CultureInfo.InvariantCulture);
                break;

            case TabSubstituicoes:
                Substitutions.Clear();
                ValidateAll();
                break;

            case TabAtalhos:
                StartHotkey = SettingsMapper.CloneHotkey(defaults.StartHotkey);
                PauseResumeHotkey = SettingsMapper.CloneHotkey(defaults.PauseResumeHotkey);
                MinimizeToTray = defaults.MinimizeToTray;
                StartMinimized = defaults.StartMinimized;
                AlwaysOnTop = defaults.AlwaysOnTop;
                break;

            case TabPersistencia:
                QueueTextPersistenceTag = defaults.QueueTextPersistence.ToString();
                ClearTextAfterCompletion = defaults.ClearTextAfterCompletion;
                break;

            case TabDiagnostico:
                EnableDiagnosticsJsonl = defaults.EnableDiagnosticsJsonl;
                DetailedTrace = defaults.Features.DetailedTrace;
                DiagnosticsRetentionText = defaults.DiagnosticsRetentionSessions.ToString(CultureInfo.InvariantCulture);
                break;

            case TabNavegadorIntegrado:
                EmbeddedBrowser = defaults.Features.EmbeddedBrowser;
                break;

            case TabExperimental:
                AdaptiveTiming = defaults.Features.AdaptiveTiming;
                TargetGuardStrict = defaults.Features.TargetGuardStrict;
                EmbeddedBrowser = defaults.Features.EmbeddedBrowser;
                CdpInput = defaults.Features.CdpInput;
                DetailedTrace = defaults.Features.DetailedTrace;
                break;
        }
        ValidateAndRefreshPreview();
    }

    public SettingsValidationResult TryBuildSettings()
    {
        ValidateAll();
        var customProfile = TryBuildCustomProfile(out var customErrors);

        var form = new SettingsFormState
        {
            CharDelayText = CharDelayText,
            EnterDelayText = EnterDelayText,
            CountdownText = CountdownText,
            ResumeCountdownText = ResumeCountdownText,
            MethodTag = MethodTag,
            TargetLayoutTag = TargetLayoutTag,
            AdaptiveTiming = AdaptiveTiming,
            TypingProfileTag = TypingProfileTag,
            CustomProfile = customProfile,
            PreserveOriginalIndentation = PreserveOriginalIndentation,
            Substitutions = Substitutions.Select(SettingsMapper.CloneSubstitution).ToList(),
            StartHotkey = SettingsMapper.CloneHotkey(StartHotkey),
            PauseResumeHotkey = SettingsMapper.CloneHotkey(PauseResumeHotkey),
            EnableTargetGuard = EnableTargetGuard,
            TargetGuardStrict = TargetGuardStrict,
            ValidateTargetWindow = ValidateTargetWindow,
            ExpectedBrowserTag = ExpectedBrowserTag,
            QueueTextPersistenceTag = QueueTextPersistenceTag,
            DetailedTrace = DetailedTrace,
            EnableDiagnosticsJsonl = EnableDiagnosticsJsonl,
            DiagnosticsRetentionText = DiagnosticsRetentionText,
            EmbeddedBrowser = EmbeddedBrowser,
            CdpInput = CdpInput,
            TargetEnvironmentTag = _baseSettings.TargetEnvironment,
            EmbeddedSubMethodTag = _baseSettings.EmbeddedSubMethod,
            MinimizeToTray = MinimizeToTray,
            StartMinimized = StartMinimized,
            AlwaysOnTop = AlwaysOnTop,
            ClearTextAfterCompletion = ClearTextAfterCompletion
        };

        var result = SettingsMapper.TryBuildSettings(form, _baseSettings);
        if (IsCustomProfileSelected && customErrors.Count > 0)
        {
            var mergedErrors = result.Errors.Concat(customErrors).ToList();
            return new SettingsValidationResult(false, null, mergedErrors, result.Warnings);
        }

        return result;
    }

    public void ValidateAndRefreshPreview()
    {
        ValidateAll();
        RefreshDryRunPreview();
    }

    public void ValidateAll()
    {
        ClearErrors();

        if (!int.TryParse(CharDelayText, out int cd) || cd < SettingsMapper.MinDelayMs || cd > SettingsMapper.MaxDelayMs)
        {
            AddError(nameof(CharDelayText), $"Intervalo entre caracteres deve estar entre {SettingsMapper.MinDelayMs} e {SettingsMapper.MaxDelayMs} ms.");
        }

        if (!int.TryParse(EnterDelayText, out int ed) || ed < SettingsMapper.MinDelayMs || ed > SettingsMapper.MaxDelayMs)
        {
            AddError(nameof(EnterDelayText), $"Intervalo após Enter deve estar entre {SettingsMapper.MinDelayMs} e {SettingsMapper.MaxDelayMs} ms.");
        }

        if (!int.TryParse(CountdownText, out int ct) || ct < SettingsMapper.MinCountdownSeconds || ct > SettingsMapper.MaxCountdownSeconds)
        {
            AddError(nameof(CountdownText), $"Contagem regressiva deve estar entre {SettingsMapper.MinCountdownSeconds} e {SettingsMapper.MaxCountdownSeconds} s.");
        }

        if (!int.TryParse(ResumeCountdownText, out int rct) || rct < SettingsMapper.MinCountdownSeconds || rct > SettingsMapper.MaxCountdownSeconds)
        {
            AddError(nameof(ResumeCountdownText), $"Contagem de retomada deve estar entre {SettingsMapper.MinCountdownSeconds} e {SettingsMapper.MaxCountdownSeconds} s.");
        }

        if (!int.TryParse(DiagnosticsRetentionText, out int ret) || ret < SettingsMapper.MinRetentionSessions || ret > SettingsMapper.MaxRetentionSessions)
        {
            AddError(nameof(DiagnosticsRetentionText), $"Retenção deve estar entre {SettingsMapper.MinRetentionSessions} e {SettingsMapper.MaxRetentionSessions} sessões.");
        }

        for (int i = 0; i < Substitutions.Count; i++)
        {
            var sub = Substitutions[i];
            if (!string.IsNullOrEmpty(sub.From) && sub.Mode == SubstitutionMode.Combinacao && sub.ComboVirtualKey == 0)
            {
                AddError(nameof(Substitutions), $"Substituição #{i + 1} ('{sub.From}'): o modo Combinação exige capturar uma tecla.");
            }
        }

        var dups = KeyStrokePlanner.FindDuplicateSubstitutionFroms(Substitutions);
        DuplicateSubstitutionsWarning = dups.Count > 0
            ? $"Aviso: símbolos duplicados em 'De' ({string.Join(", ", dups)}) — apenas a primeira regra de cada símbolo será aplicada."
            : string.Empty;

        if (IsCustomProfileSelected)
        {
            _ = TryBuildCustomProfile(out var customErrors);
            foreach (string err in customErrors)
            {
                AddError(nameof(TypingProfileTag), err);
            }
        }
    }

    private TypingProfile? TryBuildCustomProfile(out List<string> errors)
    {
        errors = new List<string>();
        bool okHoldMin = int.TryParse(CustomHoldMinText, out int holdMin);
        bool okHoldMax = int.TryParse(CustomHoldMaxText, out int holdMax);
        bool okGapMin = int.TryParse(CustomGapMinText, out int gapMin);
        bool okGapMax = int.TryParse(CustomGapMaxText, out int gapMax);
        bool okEnterMin = int.TryParse(CustomEnterGapMinText, out int enterMin);
        bool okEnterMax = int.TryParse(CustomEnterGapMaxText, out int enterMax);
        bool okSameKey = int.TryParse(CustomSameKeyPenaltyText, out int sameKey);
        bool okModLead = int.TryParse(CustomModifierLeadText, out int punctMin);
        bool okDeadLead = int.TryParse(CustomDeadKeyLeadText, out int punctMax);
        bool okIndent = int.TryParse(CustomLineIndentPauseText, out int codeBoostPct);
        bool okAlpha = double.TryParse(CustomCorrelationAlphaText, NumberStyles.Float, CultureInfo.InvariantCulture, out double alpha);
        bool okBurstLen = int.TryParse(CustomBurstLengthText, out int wordChancePct);
        bool okBurstMin = int.TryParse(CustomBurstPauseMinText, out int wordMin);
        bool okBurstMax = int.TryParse(CustomBurstPauseMaxText, out int wordMax);

        if (!okHoldMin || !okHoldMax || !okGapMin || !okGapMax || !okEnterMin || !okEnterMax ||
            !okSameKey || !okModLead || !okDeadLead || !okIndent || !okAlpha || !okBurstLen || !okBurstMin || !okBurstMax)
        {
            errors.Add("Todos os campos numéricos do perfil personalizado devem conter números válidos.");
            return null;
        }

        if (holdMin < 1 || holdMax > 500 || holdMin > holdMax)
        {
            errors.Add("Hold Min/Max inválido (deve respeitar 1 <= Min <= Max <= 500 ms).");
        }
        if (gapMin < 0 || gapMax > 2000 || gapMin > gapMax)
        {
            errors.Add("Gap Min/Max inválido (deve respeitar 0 <= Min <= Max <= 2000 ms).");
        }
        if (enterMin < 0 || enterMax > 2000 || enterMin > enterMax)
        {
            errors.Add("Enter Pause Min/Max inválido (deve respeitar 0 <= Min <= Max <= 2000 ms).");
        }
        if (punctMin < 0 || punctMax > 2000 || punctMin > punctMax)
        {
            errors.Add("Punctuation Pause Min/Max inválido (deve respeitar 0 <= Min <= Max <= 2000 ms).");
        }
        if (wordMin < 0 || wordMax > 2000 || wordMin > wordMax)
        {
            errors.Add("Word Pause Min/Max inválido (deve respeitar 0 <= Min <= Max <= 2000 ms).");
        }
        if (sameKey < 0 || sameKey > 2000)
        {
            errors.Add("SameKeyMinGapMs deve estar entre 0 e 2000 ms.");
        }
        if (alpha < 0.0 || alpha > 0.95)
        {
            errors.Add("Correlação deve estar entre 0.0 e 0.95.");
        }

        if (errors.Count > 0)
        {
            return null;
        }

        return new TypingProfile
        {
            Name = "Custom",
            Kind = ProfileKind.Custom,
            HoldMs = new MsRange(holdMin, holdMax),
            GapMs = new MsRange(gapMin, gapMax),
            EnterPauseMs = new MsRange(enterMin, enterMax),
            PunctuationPauseMs = new MsRange(punctMin, punctMax),
            WordPauseMs = new MsRange(wordMin, wordMax),
            WordPauseChance = Math.Clamp(wordChancePct / 100.0, 0.0, 1.0),
            SameKeyMinGapMs = sameKey,
            CodeLineMultiplier = Math.Clamp(1.0 + (codeBoostPct / 100.0), 1.0, 3.0),
            Correlation = alpha,
            Limits = new SafetyLimits(MinHoldMs: 1, MaxHoldMs: Math.Max(60, holdMax), MinGapMs: 0, MaxGapMs: 2000)
        };
    }

    private void RefreshDryRunPreview()
    {
        try
        {
            int cd = int.TryParse(CharDelayText, out int parsedCd) ? Math.Clamp(parsedCd, 0, 2000) : 2;
            int ed = int.TryParse(EnterDelayText, out int parsedEd) ? Math.Clamp(parsedEd, 0, 2000) : 20;

            var tempSettings = new AppSettings
            {
                CharDelayMs = cd,
                EnterDelayMs = ed,
                TypingProfile = AdaptiveTiming ? TypingProfileTag : "Legacy",
                CustomProfile = TryBuildCustomProfile(out _),
                Features = new FeatureFlags { AdaptiveTiming = AdaptiveTiming }
            };

            var profile = TypingProfile.Resolve(tempSettings);
            var units = new List<InputUnit>(SamplePreviewText.Length);
            int idx = 0;
            foreach (char ch in SamplePreviewText)
            {
                var kind = ch == '\n' ? UnitKind.Enter : UnitKind.Physical;
                var events = new[]
                {
                    new KeyEvent(KeyEventKind.Down, KeyEncoding.Scan, (ushort)ch, false),
                    new KeyEvent(KeyEventKind.Up, KeyEncoding.Scan, (ushort)ch, false)
                };
                units.Add(new InputUnit(idx++, ch.ToString(), kind, events, IsLineBreak: ch == '\n'));
            }
            var report = new PreflightReport(units, units.Count, units.Count, 0, Array.Empty<UnsupportedUnitInfo>(), Array.Empty<string>(), Array.Empty<string>());
            var dry = TypingScheduler.DryRun(report, profile);
            double cps = dry.EstimatedDuration.TotalSeconds > 0
                ? report.TotalUnits / dry.EstimatedDuration.TotalSeconds
                : report.TotalUnits;

            DryRunPreviewText =
                $"Prévia dry-run ({report.TotalUnits} unidades): ~{dry.EstimatedDuration.TotalSeconds:0.2} s " +
                $"({cps:0.1} unid/s) | Perfil efetivo: {profile.Name}";
        }
        catch
        {
            DryRunPreviewText = "Prévia dry-run indisponível (verifique os parâmetros do perfil).";
        }
    }

    private void ClearErrors()
    {
        var keys = _errors.Keys.ToList();
        _errors.Clear();
        foreach (string key in keys)
        {
            ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(key));
        }
        OnPropertyChanged(nameof(HasErrors));
    }

    private void AddError(string propertyName, string error)
    {
        if (!_errors.TryGetValue(propertyName, out var list))
        {
            list = new List<string>();
            _errors[propertyName] = list;
        }
        list.Add(error);
        ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(propertyName));
        OnPropertyChanged(nameof(HasErrors));
    }

    private void NotifyAllPropertiesChanged()
    {
        OnPropertyChanged(string.Empty);
    }
}
