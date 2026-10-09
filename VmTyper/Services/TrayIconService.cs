using System.Windows.Forms;

namespace VmTyper.Services;

/// <summary>
/// Ícone na bandeja do sistema (usando System.Windows.Forms.NotifyIcon, a
/// forma padrão de implementar tray icon em WPF sem dependências externas).
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;

    public event Action? OpenRequested;
    public event Action? TypeRequested;
    public event Action? PauseResumeRequested;
    public event Action? CancelRequested;
    public event Action? ExitRequested;

    public TrayIconService()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Abrir", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add("Digitar", null, (_, _) => TypeRequested?.Invoke());
        menu.Items.Add("Pausar / Continuar", null, (_, _) => PauseResumeRequested?.Invoke());
        menu.Items.Add("Cancelar", null, (_, _) => CancelRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => ExitRequested?.Invoke());

        _notifyIcon = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "VmTyper",
            Visible = false,
            ContextMenuStrip = menu
        };

        _notifyIcon.DoubleClick += (_, _) => OpenRequested?.Invoke();
    }

    public bool Visible
    {
        get => _notifyIcon.Visible;
        set => _notifyIcon.Visible = value;
    }

    public void ShowBalloon(string title, string text)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = text;
        _notifyIcon.ShowBalloonTip(2000);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
