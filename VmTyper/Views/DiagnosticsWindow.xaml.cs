using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using VmTyper.Models;
using VmTyper.Services;
using VmTyper.ViewModels;
using InputMethod = VmTyper.Models.InputMethod;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace VmTyper.Views;

using VmTyper;

public partial class DiagnosticsWindow : Window
{
    private readonly IKeyboardInputService _typingService;
    private readonly LogService _log;
    private readonly TypingSessionCoordinator _coordinator;
    public DiagnosticsViewModel ViewModel { get; }

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

        ViewModel = new DiagnosticsViewModel
        {
            ComparisonExpectedText = DefaultTestText
        };
        DataContext = ViewModel;

        TestTextBox.Text = DefaultTestText;
        LogListBox.ItemsSource = _log.Lines;
        UpdateExpectedFingerprint();
        ViewModel.LoadLatestSessionFromDirectory();
        ViewModel.LoadBenchmarksFromDirectory();
        ViewModel.InitializeCalibration();

        Closing += (_, _) =>
        {
            _activeLease?.Cancel();
        };
    }

    private void TestTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateExpectedFingerprint();
        if (ViewModel is not null)
        {
            ViewModel.ComparisonExpectedText = TestTextBox.Text ?? string.Empty;
        }
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

    private void ReloadLatestSession_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.LoadLatestSessionFromDirectory();
    }

    private void OpenSessionFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Abrir arquivo de sessão JSONL",
            Filter = "Arquivos JSONL (*.jsonl)|*.jsonl|Todos os arquivos (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) == true)
        {
            ViewModel.LoadSessionJsonlFile(dlg.FileName);
        }
    }

    private void PurgeDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var engine = new TypingDiagnosticsEngine();
        int removed = engine.PurgeAllDiagnostics();
        StatusText.Text = $"Diagnósticos excluídos ({removed} arquivo(s)).";
        _log.Info($"Excluídos {removed} arquivo(s) de diagnóstico JSONL.");
        ViewModel.LoadLatestSessionFromDirectory();
    }

    private void ImportObserverJson_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Importar JSON do receptor ou da página HTML de diagnóstico",
            Filter = "Arquivos JSON (*.json)|*.json|Todos os arquivos (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                string json = File.ReadAllText(dlg.FileName, Encoding.UTF8);
                ViewModel.ImportAndCompareObserverJson(json);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(this, $"Erro ao importar JSON:\n{ex.Message}", "Erro na comparação",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void ReloadBenchmarks_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.LoadBenchmarksFromDirectory();
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

            StatusText.Text = "Teste enviado (recebimento não verificado).";
            ViewModel.LoadLatestSessionFromDirectory();
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

    // ===============================================================
    // Calibração e Diagnósticos da Barra e Acentuação (§Tarefas 1, 3 e 4)
    // ===============================================================

    private async Task ExecuteCandidateAttemptAsync(CharacterSubstitution candidateSub, string label)
    {
        using var lease = _coordinator.TryBegin("Diagnostics");
        if (lease is null)
        {
            ViewModel.CalibrationStatusMessage = "Já existe uma sessão de digitação ativa no aplicativo.";
            return;
        }

        _activeLease = lease;
        var token = lease.Token;

        try
        {
            for (int i = 3; i > 0; i--)
            {
                ViewModel.CalibrationStatusMessage = $"[Foco no console da VM] Enviando em {i}...";
                await Task.Delay(1000, token);
                await lease.Pause.WaitWhilePausedAsync().WaitAsync(token);
            }
            ViewModel.CalibrationStatusMessage = $"Enviando tentativa: {label}...";

            var options = new TypingOptions
            {
                CharDelayMs = 15,
                EnterDelayMs = 30,
                Method = InputMethod.TecladoFisico,
                DiagnosticMode = true,
                Substitutions = new List<CharacterSubstitution> { candidateSub }
            };

            _log.Info($"=== Início da tentativa de calibração: {label} ===");
            await _typingService.TypeTextAsync(candidateSub.From, options, null, lease.Pause, token);
            _log.Info($"=== Fim da tentativa de calibração: {label} ===");

            ViewModel.CalibrationStatusMessage = $"Tentativa '{label}' enviada. Digite o que apareceu na VM abaixo.";
            ViewModel.LoadLatestSessionFromDirectory();
        }
        catch (OperationCanceledException)
        {
            ViewModel.CalibrationStatusMessage = "Tentativa cancelada.";
            _log.Info($"Tentativa de calibração '{label}' cancelada.");
        }
        catch (Exception ex)
        {
            ViewModel.CalibrationStatusMessage = $"Erro: {ex.Message}";
            _log.Error($"Erro na tentativa '{label}': {ex.Message}");
        }
        finally
        {
            _activeLease = null;
        }
    }

    private async void QuickTestNumpadDivide_Click(object sender, RoutedEventArgs e)
    {
        var candidates = VmSlashAndAccentCandidates.GetSlashCandidates();
        var numpadCandidate = candidates.FirstOrDefault(c => c.Id == "NumpadDivide");
        if (numpadCandidate is not null)
        {
            await ExecuteCandidateAttemptAsync(numpadCandidate.SubstitutionTemplate, numpadCandidate.Name);
        }
    }

    private async void QuickTestAbnt2Slash_Click(object sender, RoutedEventArgs e)
    {
        var candidates = VmSlashAndAccentCandidates.GetSlashCandidates();
        var abntCandidate = candidates.FirstOrDefault(c => c.Id == "Abnt2Slash");
        if (abntCandidate is not null)
        {
            await ExecuteCandidateAttemptAsync(abntCandidate.SubstitutionTemplate, abntCandidate.Name);
        }
    }

    private async void QuickTestUsSlash_Click(object sender, RoutedEventArgs e)
    {
        var candidates = VmSlashAndAccentCandidates.GetSlashCandidates();
        var usCandidate = candidates.FirstOrDefault(c => c.Id == "UsSlash");
        if (usCandidate is not null)
        {
            await ExecuteCandidateAttemptAsync(usCandidate.SubstitutionTemplate, usCandidate.Name);
        }
    }

    private async void QuickTestAltGrQ_Click(object sender, RoutedEventArgs e)
    {
        var candidates = VmSlashAndAccentCandidates.GetSlashCandidates();
        var altGrCandidate = candidates.FirstOrDefault(c => c.Id == "AltGrQ");
        if (altGrCandidate is not null)
        {
            await ExecuteCandidateAttemptAsync(altGrCandidate.SubstitutionTemplate, altGrCandidate.Name);
        }
    }

    private async void QuickTestAltNumpad47_Click(object sender, RoutedEventArgs e)
    {
        var candidates = VmSlashAndAccentCandidates.GetSlashCandidates();
        var altNumpadCandidate = candidates.FirstOrDefault(c => c.Id == "AltNumpad47");
        if (altNumpadCandidate is not null)
        {
            await ExecuteCandidateAttemptAsync(altNumpadCandidate.SubstitutionTemplate, altNumpadCandidate.Name);
        }
    }

    private void FlushDeadKey_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _typingService.FlushDeadKeyState();
            ViewModel.CalibrationStatusMessage = "Sequência de limpeza de tecla morta (Space + Backspace) enviada com sucesso.";
            _log.Info("Limpeza de teclas mortas acionada manualmente.");
        }
        catch (Exception ex)
        {
            ViewModel.CalibrationStatusMessage = $"Falha ao limpar teclas mortas: {ex.Message}";
            _log.Error($"Falha na limpeza de tecla morta: {ex.Message}");
        }
    }

    private async void SendCalibrationAttempt_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedCandidate is null)
        {
            ViewModel.CalibrationStatusMessage = "Nenhum candidato selecionado.";
            return;
        }

        var candidate = ViewModel.SelectedCandidate;
        var sub = SettingsMapper.CloneSubstitution(candidate.SubstitutionTemplate);
        sub.From = ViewModel.SelectedCalibrationSymbol;

        await ExecuteCandidateAttemptAsync(sub, $"{ViewModel.SelectedCalibrationSymbol} ({candidate.Name})");
    }

    private void MarkCalibrationConfirmed_Click(object sender, RoutedEventArgs e)
    {
        string obs = ViewModel.CalibrationObservedResult;
        string expected = ViewModel.SelectedCalibrationSymbol;
        string msg = string.Equals(obs, expected, StringComparison.Ordinal)
            ? $"Validado: Caractere '{expected}' conferido com sucesso no console da VM."
            : $"Marcado como Validado manualmente (Esperado: '{expected}', Informado: '{obs}').";
        ViewModel.SetCalibrationFeedback(DeliverySemanticState.Confirmed, msg);
        _log.Info($"Calibração: {msg}");
    }

    private void MarkCalibrationFailed_Click(object sender, RoutedEventArgs e)
    {
        string obs = ViewModel.CalibrationObservedResult;
        string expected = ViewModel.SelectedCalibrationSymbol;
        string msg = $"Incorreto: Esperado '{expected}', mas apareceu '{obs}' na VM.";
        ViewModel.SetCalibrationFeedback(DeliverySemanticState.Failed, msg);
        _log.Warn($"Calibração: {msg}");
    }

    private void MarkCalibrationUnconfirmed_Click(object sender, RoutedEventArgs e)
    {
        string expected = ViewModel.SelectedCalibrationSymbol;
        string msg = $"Tentativa para '{expected}' não confirmada pelo operador.";
        ViewModel.SetCalibrationFeedback(DeliverySemanticState.Unknown, msg);
        _log.Info($"Calibração: {msg}");
    }

    private void SaveApprovedCalibration_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedCandidate is null)
        {
            System.Windows.MessageBox.Show(this, "Selecione um candidato antes de salvar.", "Calibração", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var currentSettings = App.SettingsServiceInstance.Load();
            var updated = ViewModel.ApplyApprovedCalibration(currentSettings);
            App.SettingsServiceInstance.Save(updated);

            string msg = $"Mapeamento para '{ViewModel.SelectedCalibrationSymbol}' salvo com sucesso no perfil da VM!";
            _log.Info($"Calibração salva: '{ViewModel.SelectedCalibrationSymbol}' -> {ViewModel.SelectedCandidate.Name}");
            System.Windows.MessageBox.Show(this, msg, "Calibração Salva", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            string err = $"Erro ao salvar calibração no perfil: {ex.Message}";
            _log.Error(err);
            System.Windows.MessageBox.Show(this, err, "Erro", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
