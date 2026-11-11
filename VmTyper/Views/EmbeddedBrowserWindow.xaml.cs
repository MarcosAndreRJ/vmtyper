using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using VmTyper.Helpers;
using VmTyper.Models;
using VmTyper.Services;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace VmTyper.Views;

/// <summary>
/// Janela experimental para o navegador integrado (Fase 4 §5).
/// Mantém barra de controle separada do HWND do WebView2 (sem sobreposição de airspace),
/// relata foco multi-nível (incluindo <c>VM pronta: desconhecido</c>) e pausa imediatamente
/// a sessão se o processo do navegador falhar (<c>ProcessFailed</c>).
/// </summary>
public partial class EmbeddedBrowserWindow : Window, IEmbeddedFocusProbe
{
    private readonly LogService _log;
    private readonly TypingSessionCoordinator _coordinator;
    private bool _webViewProcessAlive = true;
    private bool _webViewFocused;
    private bool? _documentFocused;
    private string? _activeElementTag;

    public EmbeddedBrowserWindow(LogService log, TypingSessionCoordinator coordinator, FeatureFlags flags)
    {
        InitializeComponent();
        _log = log;
        _coordinator = coordinator;

        if (flags.EmbeddedBrowser)
        {
            ModeComboBox.SelectedIndex = flags.CdpInput ? 2 : 1;
            PlaceholderText.Text = "Modo experimental habilitado. Aguardando inicialização sob demanda do WebView2 Runtime.";
        }

        Activated += (_, _) => UpdateFocusBanner();
        Deactivated += (_, _) => UpdateFocusBanner();
    }

    public void OnWebViewProcessFailed(string reason, PauseTokenSource? activePauseSource)
    {
        _webViewProcessAlive = false;
        activePauseSource?.Pause();
        _log.Error($"[EmbeddedBrowser] Processo do WebView2 falhou ({reason}). Sessão pausada imediatamente.");
        NavigationStatusText.Text = $"Falha no processo WebView2 ({reason}) — clique em Atualizar para recarregar.";
        UpdateFocusBanner();
    }

    public EmbeddedFocusSnapshot ProbeFocus()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        bool isFg = IsActive;
        return new EmbeddedFocusSnapshot(
            HostWindowHwnd: hwnd,
            IsHostWindowForeground: isFg,
            IsWebViewControlFocused: _webViewFocused,
            IsDocumentFocused: _documentFocused,
            ActiveElementTagOrId: _activeElementTag,
            IsConsoleElementActive: null,
            IsWebViewProcessAlive: _webViewProcessAlive);
    }

    public void UpdateEmbeddedState(bool webViewFocused, bool? documentFocused, string? activeElementTag)
    {
        _webViewFocused = webViewFocused;
        _documentFocused = documentFocused;
        _activeElementTag = activeElementTag;
        UpdateFocusBanner();
    }

    private void UpdateFocusBanner()
    {
        var snap = ProbeFocus();
        string sessionState = _coordinator.IsActive ? "Ativa" : "Inativa";
        FocusStatusText.Text =
            $"Sessão: {sessionState} | Janela: {snap.HostForegroundStatus} | Controle: {snap.ControlFocusStatus} | " +
            $"Doc: {snap.DocumentFocusStatus} | Ativo: {snap.ActiveElementStatus} | VM pronta: {snap.GuestReadyStatus}";
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        NavigationStatusText.Text = "Voltar solicitado.";
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        NavigationStatusText.Text = "Avançar solicitado.";
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        _webViewProcessAlive = true;
        NavigationStatusText.Text = "Recarregando página...";
        UpdateFocusBanner();
    }

    private void Navigate_Click(object sender, RoutedEventArgs e)
    {
        NavigateToAddress();
    }

    private void AddressTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            NavigateToAddress();
            e.Handled = true;
        }
    }

    private void NavigateToAddress()
    {
        string url = AddressTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(url)) return;
        NavigationStatusText.Text = $"Navegando para {url}";
    }
}
