using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VmTyper.Models;
using VmTyper.Services;
using VmTyper.Services.Persistence;
using InputMethod = VmTyper.Models.InputMethod;
using MessageBox = System.Windows.MessageBox;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;

namespace VmTyper.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsService _settingsService;
    private AppSettings _working;

    private HotkeyDefinition _startHotkey = new();
    private HotkeyDefinition _pauseHotkey = new();

    private bool _capturingStart;
    private bool _capturingPause;
    private CharacterSubstitution? _capturingSubstitution;

    private readonly ObservableCollection<CharacterSubstitution> _substitutions = new();

    public SettingsWindow(SettingsService settingsService)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _working = _settingsService.Load();

        if (_settingsService.LastLoadResult is { Status: LoadStatus.Corrupt } corrupt)
        {
            MessageBox.Show(
                this,
                $"O arquivo de configurações não pôde ser lido e foi preservado em:\n{corrupt.PreservedCorruptPath}\n\nAs configurações padrão foram carregadas para edição.",
                "Configurações corrompidas",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        SubstitutionsList.ItemsSource = _substitutions;
        LoadIntoUi(_working);

        PreviewKeyDown += SettingsWindow_PreviewKeyDown;
    }

    private void LoadIntoUi(AppSettings s)
    {
        var form = SettingsMapper.ToFormState(s);

        CharDelayBox.Text = form.CharDelayText;
        EnterDelayBox.Text = form.EnterDelayText;
        CountdownBox.Text = form.CountdownText;

        SelectComboByTag(MethodCombo, form.MethodTag);
        SelectOrSetComboByTag(TargetLayoutCombo, form.TargetLayoutTag);
        AdaptiveTimingCheck.IsChecked = form.AdaptiveTiming;
        UpdateProfileItemsVisibility(form.AdaptiveTiming);
        SelectComboByTag(TypingProfileCombo, form.AdaptiveTiming ? form.TypingProfileTag : "Legacy");

        PreserveIndentationCheck.IsChecked = form.PreserveOriginalIndentation;

        _substitutions.Clear();
        foreach (var sub in form.Substitutions)
        {
            _substitutions.Add(sub);
        }

        _startHotkey = SettingsMapper.CloneHotkey(form.StartHotkey);
        _pauseHotkey = SettingsMapper.CloneHotkey(form.PauseResumeHotkey);
        StartHotkeyBox.Text = _startHotkey.ToString();
        PauseHotkeyBox.Text = _pauseHotkey.ToString();

        EnableTargetGuardCheck.IsChecked = form.EnableTargetGuard;
        TargetGuardStrictCheck.IsChecked = form.TargetGuardStrict;
        ValidateWindowCheck.IsChecked = form.ValidateTargetWindow;
        SelectComboByTag(ExpectedBrowserCombo, form.ExpectedBrowserTag);

        MinimizeToTrayCheck.IsChecked = form.MinimizeToTray;
        StartMinimizedCheck.IsChecked = form.StartMinimized;
        AlwaysOnTopCheck.IsChecked = form.AlwaysOnTop;
        ClearAfterCompletionCheck.IsChecked = form.ClearTextAfterCompletion;
        SelectComboByTag(QueuePersistenceCombo, form.QueueTextPersistenceTag);
        DetailedTraceCheck.IsChecked = form.DetailedTrace;
    }

    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item && string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private static void SelectOrSetComboByTag(ComboBox combo, string tag)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item && string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        combo.SelectedIndex = -1;
        combo.Text = tag;
    }

    private static string ReadComboTagOrText(ComboBox combo, string fallback)
    {
        if (combo.SelectedItem is ComboBoxItem item && item.Tag is string tag && !string.IsNullOrWhiteSpace(tag))
            return tag;
        if (!string.IsNullOrWhiteSpace(combo.Text))
            return combo.Text.Trim();
        return fallback;
    }

    private SettingsFormState ReadFromUi() => new()
    {
        CharDelayText = CharDelayBox.Text,
        EnterDelayText = EnterDelayBox.Text,
        CountdownText = CountdownBox.Text,
        MethodTag = ReadComboTagOrText(MethodCombo, "Automatico"),
        TargetLayoutTag = ReadComboTagOrText(TargetLayoutCombo, "HostForeground"),
        AdaptiveTiming = AdaptiveTimingCheck.IsChecked == true,
        TypingProfileTag = ReadComboTagOrText(TypingProfileCombo, "Legacy"),
        CustomProfile = _working.CustomProfile,
        PreserveOriginalIndentation = PreserveIndentationCheck.IsChecked == true,
        Substitutions = _substitutions.Select(SettingsMapper.CloneSubstitution).ToList(),
        StartHotkey = SettingsMapper.CloneHotkey(_startHotkey),
        PauseResumeHotkey = SettingsMapper.CloneHotkey(_pauseHotkey),
        EnableTargetGuard = EnableTargetGuardCheck.IsChecked == true,
        TargetGuardStrict = TargetGuardStrictCheck.IsChecked == true,
        ValidateTargetWindow = ValidateWindowCheck.IsChecked == true,
        ExpectedBrowserTag = ReadComboTagOrText(ExpectedBrowserCombo, "Nenhum"),
        QueueTextPersistenceTag = ReadComboTagOrText(QueuePersistenceCombo, "Full"),
        DetailedTrace = DetailedTraceCheck.IsChecked == true,
        MinimizeToTray = MinimizeToTrayCheck.IsChecked == true,
        StartMinimized = StartMinimizedCheck.IsChecked == true,
        AlwaysOnTop = AlwaysOnTopCheck.IsChecked == true,
        ClearTextAfterCompletion = ClearAfterCompletionCheck.IsChecked == true
    };

    // --- Presets de velocidade (item 7 do escopo) ---
    private void PresetInstantaneo_Click(object sender, RoutedEventArgs e) => SetDelays(0, 0);
    private void PresetMuitoRapido_Click(object sender, RoutedEventArgs e) => SetDelays(1, 10);
    private void PresetRapido_Click(object sender, RoutedEventArgs e) => SetDelays(3, 15);
    private void PresetNormal_Click(object sender, RoutedEventArgs e) => SetDelays(10, 30);
    private void PresetCompatibilidade_Click(object sender, RoutedEventArgs e) => SetDelays(30, 60);

    private void SetDelays(int charDelay, int enterDelay)
    {
        CharDelayBox.Text = charDelay.ToString();
        EnterDelayBox.Text = enterDelay.ToString();
    }

    private void AdaptiveTimingCheck_Changed(object sender, RoutedEventArgs e)
    {
        bool enabled = AdaptiveTimingCheck.IsChecked == true;
        UpdateProfileItemsVisibility(enabled);
        if (!enabled)
        {
            SelectComboByTag(TypingProfileCombo, "Legacy");
        }
    }

    private void UpdateProfileItemsVisibility(bool adaptiveEnabled)
    {
        var vis = adaptiveEnabled ? Visibility.Visible : Visibility.Collapsed;
        if (ProfileFastItem is not null) ProfileFastItem.Visibility = vis;
        if (ProfileCompatibilityItem is not null) ProfileCompatibilityItem.Visibility = vis;
        if (ProfileNaturalItem is not null) ProfileNaturalItem.Visibility = vis;
        if (ProfileCustomItem is not null) ProfileCustomItem.Visibility = vis;
    }

    // --- Captura de atalhos ---
    private void CaptureStartHotkey_Click(object sender, RoutedEventArgs e)
    {
        _capturingPause = false;
        _capturingStart = true;
        StartHotkeyBox.Text = "Pressione a combinação desejada...";
    }

    private void CapturePauseHotkey_Click(object sender, RoutedEventArgs e)
    {
        _capturingStart = false;
        _capturingPause = true;
        PauseHotkeyBox.Text = "Pressione a combinação desejada...";
    }

    private void SettingsWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_capturingStart && !_capturingPause && _capturingSubstitution is null) return;

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                 or Key.LeftAlt or Key.RightAlt or Key.System)
        {
            return; // aguarda a tecla "principal"
        }

        var modifiers = Keyboard.Modifiers;

        if (_capturingSubstitution is not null)
        {
            var sub = _capturingSubstitution;
            sub.ComboShift = modifiers.HasFlag(ModifierKeys.Shift);
            sub.ComboCtrl = modifiers.HasFlag(ModifierKeys.Control);
            sub.ComboAlt = modifiers.HasFlag(ModifierKeys.Alt);
            sub.ComboVirtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
            sub.ComboLabel = BuildComboLabel(sub.ComboShift, sub.ComboCtrl, sub.ComboAlt, key);
            _capturingSubstitution = null;
            e.Handled = true;
            return;
        }

        var hotkey = new HotkeyDefinition
        {
            Ctrl = modifiers.HasFlag(ModifierKeys.Control),
            Shift = modifiers.HasFlag(ModifierKeys.Shift),
            Alt = modifiers.HasFlag(ModifierKeys.Alt),
            VirtualKey = (uint)KeyInterop.VirtualKeyFromKey(key)
        };

        if (!hotkey.Ctrl && !hotkey.Shift && !hotkey.Alt)
        {
            MessageBox.Show(this, "Use pelo menos um modificador (Ctrl, Shift ou Alt).", "Atalho inválido",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
            return;
        }

        if (_capturingStart)
        {
            _startHotkey = hotkey;
            StartHotkeyBox.Text = hotkey.ToString();
            _capturingStart = false;
        }
        else if (_capturingPause)
        {
            _pauseHotkey = hotkey;
            PauseHotkeyBox.Text = hotkey.ToString();
            _capturingPause = false;
        }

        e.Handled = true;
    }

    private static string BuildComboLabel(bool shift, bool ctrl, bool alt, Key key)
    {
        var parts = new List<string>();
        if (ctrl) parts.Add("Ctrl");
        if (shift) parts.Add("Shift");
        if (alt) parts.Add("Alt");
        parts.Add(FriendlyKeyName(key));
        return string.Join(" + ", parts);
    }

    private static string FriendlyKeyName(Key key) => key switch
    {
        Key.Divide => "/ (numpad)",
        Key.Multiply => "* (numpad)",
        Key.Subtract => "- (numpad)",
        Key.Add => "+ (numpad)",
        Key.Decimal => ", (numpad)",
        Key.NumPad0 => "0 (numpad)",
        Key.NumPad1 => "1 (numpad)",
        Key.NumPad2 => "2 (numpad)",
        Key.NumPad3 => "3 (numpad)",
        Key.NumPad4 => "4 (numpad)",
        Key.NumPad5 => "5 (numpad)",
        Key.NumPad6 => "6 (numpad)",
        Key.NumPad7 => "7 (numpad)",
        Key.NumPad8 => "8 (numpad)",
        Key.NumPad9 => "9 (numpad)",
        Key.OemQuestion => "/ ?",
        Key.Oem1 => "; :",
        Key.OemComma => ", <",
        Key.OemPeriod => ". >",
        Key.OemMinus => "- _",
        Key.OemPlus => "= +",
        Key.OemTilde => "` ~",
        Key.OemOpenBrackets => "[ {",
        Key.Oem6 => "] }",
        Key.Oem5 => "\\ |",
        Key.OemQuotes => "' \"",
        Key.Enter => "Enter",
        Key.Tab => "Tab",
        Key.Space => "Espaço",
        _ => key.ToString()
    };

    // --- Substituições de caracteres ---
    private void AddSubstitution_Click(object sender, RoutedEventArgs e)
    {
        _substitutions.Add(new CharacterSubstitution());
    }

    private void RemoveSubstitution_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CharacterSubstitution item })
        {
            _substitutions.Remove(item);
        }
    }

    private void CaptureSubstitutionCombo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CharacterSubstitution item }) return;

        _capturingStart = false;
        _capturingPause = false;
        _capturingSubstitution = item;
        item.ComboLabel = "Pressione a combinação desejada...";
    }

    private void RestoreDefaults_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            this,
            "Restaurar todas as configurações para os valores padrão de fábrica?",
            "Restaurar padrões",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes) return;

        _working = new AppSettings();
        LoadIntoUi(_working);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var form = ReadFromUi();
        var validation = SettingsMapper.TryBuildSettings(form, _working);

        if (!validation.IsValid || validation.Settings is null)
        {
            MessageBox.Show(
                this,
                string.Join("\n", validation.Errors),
                "Configuração inválida",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (validation.Warnings.Count > 0)
        {
            var proceed = MessageBox.Show(
                this,
                string.Join("\n\n", validation.Warnings) + "\n\nDeseja salvar mesmo assim?",
                "Aviso de configuração",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (proceed != MessageBoxResult.Yes) return;
        }

        _working = validation.Settings;
        _settingsService.Save(_working);

        if (System.Windows.Application.Current is App && App.LogServiceInstance is not null)
        {
            App.LogServiceInstance.DetailedTrace = _working.Features.DetailedTrace;
        }

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
