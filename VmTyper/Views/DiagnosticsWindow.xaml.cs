using System.Windows;
using System.Windows.Controls;
using VmTyper.Models;
using VmTyper.Native;
using VmTyper.Services;
using InputMethod = VmTyper.Models.InputMethod;

namespace VmTyper.Views;

using VmTyper;

public partial class DiagnosticsWindow : Window
{
    private const int HotkeyIdDiagnosticsCancel = 103;

    private readonly IKeyboardInputService _typingService;
    private readonly LogService _log;
    private readonly GlobalHotkeyService _hotkeyService;

    private CancellationTokenSource? _cts;
    private bool _running;

    private const string DefaultTestText =
        "Teste:\n" +
        "abcdefghijklmnopqrstuvwxyz\n" +
        "ABCDEFGHIJKLMNOPQRSTUVWXYZ\n" +
        "0123456789\n" +
        "áàãâéêíóôõúç\n" +
        "ÁÀÃÂÉÊÍÓÔÕÚÇ\n" +
        "! @ # $ % ¨ & * ( ) - _ = +";

    public DiagnosticsWindow(IKeyboardInputService typingService, LogService log, GlobalHotkeyService hotkeyService)
    {
        InitializeComponent();
        _typingService = typingService;
        _log = log;
        _hotkeyService = hotkeyService;

        TestTextBox.Text = DefaultTestText;
        LogListBox.ItemsSource = _log.Lines;

        Closing += (_, _) =>
        {
            _cts?.Cancel();
            if (_hotkeyService.IsRegistered(HotkeyIdDiagnosticsCancel))
                _hotkeyService.UnregisterHotkey(HotkeyIdDiagnosticsCancel);
        };
    }

    private async void SendTest_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        _running = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _hotkeyService.RegisterHotkey(HotkeyIdDiagnosticsCancel, 0, Win32.VK_ESCAPE, () => _cts?.Cancel());

        try
        {
            for (int i = 3; i > 0; i--)
            {
                StatusText.Text = $"Iniciando em {i}...";
                await Task.Delay(1000, token);
            }
            StatusText.Text = "Digitando teste...";

            var method = ((MethodCombo.SelectedItem as ComboBoxItem)?.Tag as string) switch
            {
                "TecladoFisico" => InputMethod.TecladoFisico,
                "Unicode" => InputMethod.Unicode,
                _ => InputMethod.Automatico
            };

            var options = new TypingOptions
            {
                CharDelayMs = 10,
                EnterDelayMs = 30,
                Method = method,
                DiagnosticMode = true,
                Substitutions = App.SettingsServiceInstance.Load().CharacterSubstitutions
            };
            var pauseTokenSource = new Helpers.PauseTokenSource();

            _log.Info("=== Início do teste de diagnóstico ===");
            await _typingService.TypeTextAsync(TestTextBox.Text, options, null, pauseTokenSource.Token, token);
            _log.Info("=== Fim do teste de diagnóstico ===");

            StatusText.Text = "Teste concluído.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Teste cancelado.";
            _log.Info("Teste de diagnóstico cancelado pelo usuário.");
        }
        finally
        {
            if (_hotkeyService.IsRegistered(HotkeyIdDiagnosticsCancel))
                _hotkeyService.UnregisterHotkey(HotkeyIdDiagnosticsCancel);
            _cts?.Dispose();
            _cts = null;
            _running = false;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
    }
}
