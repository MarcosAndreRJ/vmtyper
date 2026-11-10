using System.Windows;
using VmTyper.Models;
using VmTyper.ViewModels;
using VmTyper.Views;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;

namespace VmTyper;

public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;
    private bool _isExiting;

    public MainWindow()
    {
        InitializeComponent();
    }

    public void AttachViewModel(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        ApplyBehaviorSettings();

        viewModel.ConfirmPartialResume = ConfirmPartialResumeDialog;
        viewModel.ConfirmPreflightUnsupported = ConfirmPreflightUnsupportedDialog;
        viewModel.ConfirmTargetChanged = ConfirmTargetChangedDialog;

        if (App.QueueLoadCorruptPath is { } corruptPath)
        {
            MessageBox.Show(this,
                $"A fila salva não pôde ser lida.\nUma cópia foi preservada em:\n{corruptPath}\n\nComeçar com fila vazia?",
                "Fila corrompida preservada",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        App.TrayIconServiceInstance.OpenRequested += TrayOpen;
        App.TrayIconServiceInstance.TypeRequested += () => _viewModel?.StartCommand.Execute(null);
        App.TrayIconServiceInstance.PauseResumeRequested += () => _viewModel?.PauseResumeCommand.Execute(null);
        App.TrayIconServiceInstance.CancelRequested += () => _viewModel?.CancelCommand.Execute(null);
        App.TrayIconServiceInstance.ExitRequested += () =>
        {
            _isExiting = true;
            System.Windows.Application.Current.Shutdown();
        };
    }

    private void ApplyBehaviorSettings()
    {
        if (_viewModel is null) return;
        Topmost = _viewModel.Settings.AlwaysOnTop;
    }

    // Reaplica as configurações de comportamento (Sempre no topo etc.) após
    // o usuário salvar alterações na tela de Configurações.
    public void RefreshBehaviorSettings() => ApplyBehaviorSettings();

    private void TrayOpen()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        App.TrayIconServiceInstance.Visible = false;
    }

    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (_viewModel is null) return;

        if (WindowState == WindowState.Minimized && _viewModel.Settings.MinimizeToTray)
        {
            Hide();
            App.TrayIconServiceInstance.Visible = true;
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isExiting && _viewModel is not null && _viewModel.Settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            App.TrayIconServiceInstance.Visible = true;
            return;
        }

        // §2.4 (H5): se houver sessão ativa ao fechar de verdade, cancelar e aguardar até 2 s.
        App.CoordinatorInstance?.CancelAndWait(TimeSpan.FromSeconds(2));
    }

    private bool? ConfirmPartialResumeDialog(TextBlockItem block)
    {
        if (block.SentUpToIndex > 0 && block.SentUpToIndex < block.Text.Length)
        {
            string remaining = block.Text.Substring(block.SentUpToIndex);
            string preview = remaining.Length > 40 ? remaining.Substring(0, 40) + "…" : remaining;
            var res = MessageBox.Show(this,
                $"O Bloco {block.Order} está no estado '{block.StatusLabel}'.\n\n" +
                $"• Sim: Reenviar do início (recomendado)\n" +
                $"• Não: Continuar da unidade {block.SentUpToIndex} (texto restante: \"{preview}\")\n" +
                $"• Cancelar: Não iniciar agora",
                "Confirmar envio de bloco interrompido/parcial",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question,
                MessageBoxResult.Yes);

            return res switch
            {
                MessageBoxResult.Yes => false,
                MessageBoxResult.No => true,
                _ => null
            };
        }

        var simple = MessageBox.Show(this,
            $"O Bloco {block.Order} está no estado '{block.StatusLabel}'. Deseja reenviar do início?",
            "Confirmar reenvio",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question,
            MessageBoxResult.OK);
        return simple == MessageBoxResult.OK ? false : null;
    }

    private PreflightDecision ConfirmPreflightUnsupportedDialog(Services.PreflightReport report)
    {
        var summary = string.Join("\n", report.Unsupported
            .Take(10)
            .Select(u => $"• Posição #{u.Index} ({u.Category}): {u.Reason}"));
        if (report.UnsupportedCount > 10)
        {
            summary += $"\n… e mais {report.UnsupportedCount - 10} unidade(s).";
        }

        var res = MessageBox.Show(this,
            $"O texto contém {report.UnsupportedCount} unidade(s) não suportada(s) no layout físico:\n\n" +
            $"{summary}\n\n" +
            $"• Sim: Usar Unicode para essas {report.UnsupportedCount} unidade(s)\n" +
            $"• Não: Enviar mesmo assim, pulando {report.UnsupportedCount} unidade(s)\n" +
            $"• Cancelar (padrão): Cancelar envio",
            "Pré-voo: unidades não suportadas",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        return res switch
        {
            MessageBoxResult.Yes => PreflightDecision.FallbackToUnicode,
            MessageBoxResult.No => PreflightDecision.SkipUnsupported,
            _ => PreflightDecision.Cancel
        };
    }

    private bool ConfirmTargetChangedDialog(Services.TargetAnchor previous, Services.TargetAnchor current)
    {
        var res = MessageBox.Show(this,
            $"O destino mudou.\n" +
            $"Anterior: {previous.ProcessName} (0x{previous.Hwnd:X})\n" +
            $"Atual: {current.ProcessName} (0x{current.Hwnd:X})\n\n" +
            $"Usar a nova janela como destino?",
            "Destino alterado",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        return res == MessageBoxResult.Yes;
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is { CanEditQueue: false }) return;
        var window = new SettingsWindow(App.SettingsServiceInstance)
        {
            Owner = this
        };
        if (window.ShowDialog() == true)
        {
            _viewModel?.ReloadSettings();
            ApplyBehaviorSettings();
        }
    }

    private void OpenDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is { CanEditQueue: false }) return;
        var window = new DiagnosticsWindow(App.TypingServiceInstance, App.LogServiceInstance, App.CoordinatorInstance)
        {
            Owner = this
        };
        window.Show();
    }

    private void EnvironmentCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_viewModel is null || !_viewModel.CanEditQueue) return;
        if (string.Equals(_viewModel.SelectedEnvironmentTag, "Embedded", StringComparison.OrdinalIgnoreCase) &&
            _viewModel.Settings.Features.EmbeddedBrowser)
        {
            var embeddedWindow = new EmbeddedBrowserWindow(App.LogServiceInstance, App.CoordinatorInstance, _viewModel.Settings.Features)
            {
                Owner = this
            };
            embeddedWindow.Show();
        }
    }

    private void AddBlock_Click(object sender, RoutedEventArgs e)
    {
        _viewModel?.AddBlock();
    }

    private void RestartQueue_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(this,
            "Deseja reiniciar a fila? Todos os blocos voltarão ao estado pendente.",
            "Reiniciar fila", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
            _viewModel?.RestartQueue();
    }

    private void NewQueue_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(this,
            "Isso removerá todos os blocos preparados. Deseja continuar?",
            "Nova fila", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes)
            _viewModel?.NewQueue();
    }

    private void RemoveBlock_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TextBlockItem item }) return;

        if (!item.IsEmpty)
        {
            var result = MessageBox.Show(this,
                $"Deseja remover o Bloco {item.Order}?",
                "Remover bloco", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;
        }

        _viewModel?.RemoveBlock(item.Id);
    }

    private void OpenCodeFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Arquivos de Código e Texto (*.pas;*.dpr;*.inc;*.sql;*.ddl;*.txt)|*.pas;*.dpr;*.inc;*.sql;*.ddl;*.txt|Arquivos Delphi (*.pas;*.dpr;*.inc)|*.pas;*.dpr;*.inc|Scripts SQL (*.sql;*.ddl)|*.sql;*.ddl|Todos os arquivos (*.*)|*.*",
            Title = "Abrir Arquivo de Código"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                _viewModel?.DocManager.OpenFile(dialog.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Erro ao abrir arquivo:\n{ex.Message}", "Erro ao abrir arquivo", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void NewCodeDocument_Click(object sender, RoutedEventArgs e)
    {
        int count = (_viewModel?.DocManager.Documents.Count ?? 0) + 1;
        _viewModel?.DocManager.CreateNewDocument(string.Empty, $"Documento {count}");
    }

    private void SelectDocumentTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.FrameworkElement { Tag: SourceDocument doc } && _viewModel != null)
        {
            _viewModel.DocManager.CurrentDocument = doc;
        }
    }

    private void CloseDocumentTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.FrameworkElement { Tag: SourceDocument doc } && _viewModel != null)
        {
            _viewModel.DocManager.CloseDocument(doc);
        }
    }

    private void SelectBlockTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.FrameworkElement { Tag: CodeBlock block } && _viewModel != null)
        {
            _viewModel.DocManager.SelectedBlock = block;
            CodeEditor.ScrollToLine(block.StartLine);
            CodeEditor.SelectRange(block.StartOffset, block.Length);
        }
    }

    private void SplitAtCaret_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null) return;
        int caret = CodeEditor.CaretOffset;
        bool split = _viewModel.DocManager.SplitCurrentBlockAtCaret(caret);
        if (!split)
        {
            MessageBox.Show(this, "Não foi possível dividir no cursor. Certifique-se de que o cursor está dentro do bloco selecionado.", "Divisão de Bloco", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void MergeNextBlock_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.DocManager.SelectedBlock is { } sb)
        {
            bool merged = _viewModel.DocManager.MergeBlockWithNext(sb);
            if (!merged)
            {
                MessageBox.Show(this, "Não há bloco seguinte consecutivo para mesclar.", "Unir Blocos", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }
}
