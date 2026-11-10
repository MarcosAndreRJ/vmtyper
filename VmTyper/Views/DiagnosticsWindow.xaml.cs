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
    private readonly IKeyboardInputService _typingService;
    private readonly LogService _log;
    private readonly TypingSessionCoordinator _coordinator;

    private TypingSessionLease? _activeLease;

    private const string DefaultTestText =
        "Teste:\n" +
        "abcdefghijklmnopqrstuvwxyz\n" +
        "ABCDEFGHIJKLMNOPQRSTUVWXYZ\n" +
        "0123456789\n" +
        "áàãâéêíóôõúç\n" +
        "ÁÀÃÂÉÊÍÓÔÕÚÇ\n" +
        "! @ # $ % ¨ & * ( ) - _ = +";

    public DiagnosticsWindow(IKeyboardInputService typingService, LogService log, TypingSessionCoordinator coordinator)
    {
        InitializeComponent();
        _typingService = typingService;
        _log = log;
        _coordinator = coordinator;

        TestTextBox.Text = DefaultTestText;
        LogListBox.ItemsSource = _log.Lines;
        UpdateExpectedFingerprint();

        Closing += (_, _) =>
        {
            _activeLease?.Cancel();
        };
    }

    private void TestTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateExpectedFingerprint();
    }

    private void UpdateExpectedFingerprint()
    {
        if (ExpectedFingerprintText is null) return;
        var fp = DivergenceClassifier.ComputeFingerprint(TestTextBox.Text ?? string.Empty);
        int letters = fp.CategoryCounts.GetValueOrDefault("letra") + fp.CategoryCounts.GetValueOrDefault("acentuado");
        int digits = fp.CategoryCounts.GetValueOrDefault("dígito");
        int punct = fp.CategoryCounts.GetValueOrDefault("pontuação");
        int spaces = fp.CategoryCounts.GetValueOrDefault("espaço");
        int controls = fp.CategoryCounts.GetValueOrDefault("controle");
        ExpectedFingerprintText.Text =
            $"Esperado — SHA-256(12): {fp.Sha256Hex12} | len={fp.CharLength} (graf={fp.GraphemeCount}) | " +
            $"L:{letters} D:{digits} P:{punct} S:{spaces} C:{controls}";
    }

    private void PurgeDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var engine = new TypingDiagnosticsEngine();
        int removed = engine.PurgeAllDiagnostics();
        StatusText.Text = $"Diagnósticos excluídos ({removed} arquivo(s)).";
        _log.Info($"Excluídos {removed} arquivo(s) de diagnóstico JSONL.");
    }

    private async void SendTest_Click(object sender, RoutedEventArgs e)
    {
        using var lease = _coordinator.TryBegin("Diagnostics");
        if (lease is null)
        {
            StatusText.Text = "Já existe uma sessão de digitação ativa.";
            return;
        }

        _activeLease = lease;
        var token = lease.Token;

        try
        {
            for (int i = 3; i > 0; i--)
            {
                StatusText.Text = $"Iniciando em {i}...";
                await Task.Delay(1000, token);
                await lease.Pause.WaitWhilePausedAsync().WaitAsync(token);
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

            _log.Info("=== Início do teste de diagnóstico ===");
            await _typingService.TypeTextAsync(TestTextBox.Text, options, null, lease.Pause, token);
            _log.Info("=== Fim do teste de diagnóstico ===");

            StatusText.Text = "Teste concluído.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Teste cancelado.";
            _log.Info("Teste de diagnóstico cancelado pelo usuário.");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erro: {ex.Message}";
            _log.Error("Erro no teste de diagnóstico: " + ex.Message);
        }
        finally
        {
            _activeLease = null;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _activeLease?.Cancel();
    }
}
