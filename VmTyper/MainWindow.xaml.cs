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
        if (_isExiting) return;

        if (_viewModel is not null && _viewModel.Settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            App.TrayIconServiceInstance.Visible = true;
            return;
        }
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
}
