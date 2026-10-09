using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VmTyper.Models;
using VmTyper.Services;
using InputMethod = VmTyper.Models.InputMethod;
using MessageBox = System.Windows.MessageBox;
using Button = System.Windows.Controls.Button;

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
        SubstitutionsList.ItemsSource = _substitutions;
        LoadIntoUi(_working);

        PreviewKeyDown += SettingsWindow_PreviewKeyDown;
    }

    private void LoadIntoUi(AppSettings s)
    {
        CharDelayBox.Text = s.CharDelayMs.ToString();
        EnterDelayBox.Text = s.EnterDelayMs.ToString();
        CountdownBox.Text = s.CountdownSeconds.ToString();

        MethodCombo.SelectedIndex = s.Method switch
        {
            InputMethod.Automatico => 0,
            InputMethod.TecladoFisico => 1,
            InputMethod.Unicode => 2,
            _ => 0
        };

        PreserveIndentationCheck.IsChecked = s.PreserveOriginalIndentation;

        _substitutions.Clear();
        foreach (var sub in s.CharacterSubstitutions)
        {
            _substitutions.Add(new CharacterSubstitution
            {
                From = sub.From,
                To = sub.To,
                Mode = sub.Mode,
                ComboShift = sub.ComboShift,
                ComboCtrl = sub.ComboCtrl,
                ComboAlt = sub.ComboAlt,
                ComboVirtualKey = sub.ComboVirtualKey,
                ComboLabel = sub.ComboLabel
            });
        }

        _startHotkey = Clone(s.StartHotkey);
        _pauseHotkey = Clone(s.PauseResumeHotkey);
        StartHotkeyBox.Text = _startHotkey.ToString();
        PauseHotkeyBox.Text = _pauseHotkey.ToString();

        ValidateWindowCheck.IsChecked = s.ValidateTargetWindow;
        ExpectedBrowserCombo.SelectedIndex = s.ExpectedBrowser switch
        {
            ExpectedBrowser.Nenhum => 0,
            ExpectedBrowser.Chrome => 1,
            ExpectedBrowser.Edge => 2,
            ExpectedBrowser.Firefox => 3,
            _ => 0
        };

        MinimizeToTrayCheck.IsChecked = s.MinimizeToTray;
        StartMinimizedCheck.IsChecked = s.StartMinimized;
        AlwaysOnTopCheck.IsChecked = s.AlwaysOnTop;
        ClearAfterCompletionCheck.IsChecked = s.ClearTextAfterCompletion;
    }

    private static HotkeyDefinition Clone(HotkeyDefinition h) => new()
    {
        Ctrl = h.Ctrl,
        Shift = h.Shift,
        Alt = h.Alt,
        VirtualKey = h.VirtualKey
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

        // Captura de combinação para substituição: ao contrário dos
        // atalhos globais, NÃO exige modificador — a VM pode estar
        // "quebrada" justamente numa tecla sem modificador nenhum, e o
        // usuário precisa poder capturar exatamente isso.
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

    /// <summary>
    /// Nome amigável para a tecla capturada, distinguindo explicitamente
    /// variantes de numpad (o motivo desse recurso existir) de teclas
    /// equivalentes no teclado principal.
    /// </summary>
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

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(CharDelayBox.Text, out int charDelay) || charDelay < 0)
        {
            MessageBox.Show(this, "Intervalo entre caracteres inválido.", "Erro", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(EnterDelayBox.Text, out int enterDelay) || enterDelay < 0)
        {
            MessageBox.Show(this, "Intervalo após Enter inválido.", "Erro", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(CountdownBox.Text, out int countdown) || countdown < 0)
        {
            MessageBox.Show(this, "Contagem regressiva inválida.", "Erro", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _working.CharDelayMs = charDelay;
        _working.EnterDelayMs = enterDelay;
        _working.CountdownSeconds = countdown;

        _working.Method = ((MethodCombo.SelectedItem as ComboBoxItem)?.Tag as string) switch
        {
            "TecladoFisico" => InputMethod.TecladoFisico,
            "Unicode" => InputMethod.Unicode,
            _ => InputMethod.Automatico
        };

        _working.PreserveOriginalIndentation = PreserveIndentationCheck.IsChecked == true;

        _working.CharacterSubstitutions = _substitutions
            .Where(sub => !string.IsNullOrEmpty(sub.From))
            .Select(sub => new CharacterSubstitution
            {
                From = sub.From,
                To = sub.To,
                Mode = sub.Mode,
                ComboShift = sub.ComboShift,
                ComboCtrl = sub.ComboCtrl,
                ComboAlt = sub.ComboAlt,
                ComboVirtualKey = sub.ComboVirtualKey,
                ComboLabel = sub.ComboLabel
            })
            .ToList();

        _working.StartHotkey = _startHotkey;
        _working.PauseResumeHotkey = _pauseHotkey;

        _working.ValidateTargetWindow = ValidateWindowCheck.IsChecked == true;
        _working.ExpectedBrowser = ((ExpectedBrowserCombo.SelectedItem as ComboBoxItem)?.Tag as string) switch
        {
            "Chrome" => ExpectedBrowser.Chrome,
            "Edge" => ExpectedBrowser.Edge,
            "Firefox" => ExpectedBrowser.Firefox,
            _ => ExpectedBrowser.Nenhum
        };

        _working.MinimizeToTray = MinimizeToTrayCheck.IsChecked == true;
        _working.StartMinimized = StartMinimizedCheck.IsChecked == true;
        _working.AlwaysOnTop = AlwaysOnTopCheck.IsChecked == true;
        _working.ClearTextAfterCompletion = ClearAfterCompletionCheck.IsChecked == true;

        _settingsService.Save(_working);
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
