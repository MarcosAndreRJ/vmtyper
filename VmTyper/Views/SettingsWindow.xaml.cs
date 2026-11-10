using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VmTyper.Models;
using VmTyper.Services;
using VmTyper.Services.Persistence;
using VmTyper.ViewModels;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;

namespace VmTyper.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsService _settingsService;
    public SettingsViewModel ViewModel { get; }

    private bool _capturingStart;
    private bool _capturingPause;
    private CharacterSubstitution? _capturingSubstitution;

    public SettingsWindow(SettingsService settingsService)
    {
        InitializeComponent();
        _settingsService = settingsService;
        var loaded = _settingsService.Load();

        if (_settingsService.LastLoadResult is { Status: LoadStatus.Corrupt } corrupt)
        {
            MessageBox.Show(
                this,
                $"O arquivo de configurações não pôde ser lido e foi preservado em:\n{corrupt.PreservedCorruptPath}\n\nAs configurações padrão foram carregadas para edição.",
                "Configurações corrompidas",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        ViewModel = new SettingsViewModel(loaded);
        DataContext = ViewModel;

        PreviewKeyDown += SettingsWindow_PreviewKeyDown;
    }

    private void PresetInstantaneo_Click(object sender, RoutedEventArgs e) => ViewModel.SetPresetDelays(0, 0);
    private void PresetMuitoRapido_Click(object sender, RoutedEventArgs e) => ViewModel.SetPresetDelays(1, 10);
    private void PresetRapido_Click(object sender, RoutedEventArgs e) => ViewModel.SetPresetDelays(3, 15);
    private void PresetNormal_Click(object sender, RoutedEventArgs e) => ViewModel.SetPresetDelays(10, 30);
    private void PresetCompatibilidade_Click(object sender, RoutedEventArgs e) => ViewModel.SetPresetDelays(30, 60);

    private void CaptureStartHotkey_Click(object sender, RoutedEventArgs e)
    {
        _capturingPause = false;
        _capturingSubstitution = null;
        _capturingStart = true;
        StartHotkeyBox.Text = "Pressione a combinação desejada...";
    }

    private void CapturePauseHotkey_Click(object sender, RoutedEventArgs e)
    {
        _capturingStart = false;
        _capturingSubstitution = null;
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
            return;
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
            ViewModel.ValidateAll();
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
            ViewModel.StartHotkey = hotkey;
            StartHotkeyBox.Text = hotkey.ToString();
            _capturingStart = false;
        }
        else if (_capturingPause)
        {
            ViewModel.PauseResumeHotkey = hotkey;
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

    private void AddSubstitution_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.AddSubstitution();
    }

    private void RemoveSubstitution_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CharacterSubstitution item })
        {
            ViewModel.RemoveSubstitution(item);
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

    private void OpenCalibrationForSubstitution_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CharacterSubstitution item }) return;

        var diag = new DiagnosticsWindow(App.TypingServiceInstance, App.LogServiceInstance, App.CoordinatorInstance)
        {
            Owner = this
        };
        if (!string.IsNullOrWhiteSpace(item.From))
        {
            diag.ViewModel.SelectedCalibrationSymbol = item.From;
        }
        diag.ShowDialog();

        // Recarrega as configurações salvas pela janela de calibração caso tenham mudado
        var reloaded = App.SettingsServiceInstance.Load();
        var match = reloaded.CharacterSubstitutions.FirstOrDefault(s => string.Equals(s.From, item.From, StringComparison.Ordinal));
        if (match is not null)
        {
            item.Mode = match.Mode;
            item.SequenceSteps = match.SequenceSteps?.Select(SettingsMapper.CloneSequenceStep).ToList() ?? new();
            item.ComboLabel = match.ComboLabel;
            item.ComboVirtualKey = match.ComboVirtualKey;
            item.ComboShift = match.ComboShift;
            item.ComboCtrl = match.ComboCtrl;
            item.ComboAlt = match.ComboAlt;
        }
    }

    private void OpenPersistenceFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ViewModel.PersistenceFolderPath);
            Process.Start(new ProcessStartInfo
            {
                FileName = ViewModel.PersistenceFolderPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Não foi possível abrir a pasta:\n{ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void PurgeDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            this,
            "Excluir todos os arquivos de diagnóstico JSONL gravados em disco?",
            "Excluir diagnósticos",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        var engine = new TypingDiagnosticsEngine();
        int removed = engine.PurgeAllDiagnostics();
        MessageBox.Show(this, $"{removed} arquivo(s) de diagnóstico excluído(s).", "Diagnósticos excluídos", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ClearEmbeddedProfile_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            this,
            $"Limpar todos os dados do perfil isolado do WebView2 em:\n{ViewModel.EmbeddedBrowserProfilePath}?",
            "Limpar dados do perfil",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            if (Directory.Exists(ViewModel.EmbeddedBrowserProfilePath))
            {
                Directory.Delete(ViewModel.EmbeddedBrowserProfilePath, recursive: true);
            }
            MessageBox.Show(this, "Dados do perfil isolado removidos.", "Perfil limpo", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Não foi possível remover todos os arquivos (feche o navegador integrado primeiro):\n{ex.Message}",
                "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RestoreCurrentTabDefaults_Click(object sender, RoutedEventArgs e)
    {
        string tabKey = (SettingsTabs.SelectedItem as TabItem)?.Tag as string ?? SettingsViewModel.TabDigitacao;
        string tabHeader = (SettingsTabs.SelectedItem as TabItem)?.Header as string ?? "atual";

        var answer = MessageBox.Show(
            this,
            $"Restaurar as configurações da aba '{tabHeader}' para os valores padrão?",
            "Restaurar padrões da aba",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes) return;
        ViewModel.RestoreTabDefaults(tabKey);
    }

    private void RestoreDefaults_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            this,
            "Restaurar todas as configurações de todas as abas para os valores padrão de fábrica?",
            "Restaurar todos os padrões",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes) return;
        ViewModel.RestoreAllDefaults();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var validation = ViewModel.TryBuildSettings();

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

        _settingsService.Save(validation.Settings);

        if (System.Windows.Application.Current is App && App.LogServiceInstance is not null)
        {
            App.LogServiceInstance.DetailedTrace = validation.Settings.Features.DetailedTrace;
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
