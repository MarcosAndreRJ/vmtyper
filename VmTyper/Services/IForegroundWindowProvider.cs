using System.Diagnostics;
using System.Text;
using VmTyper.Native;

namespace VmTyper.Services;

/// <summary>
/// Âncora da janela de destino capturada ao final da contagem regressiva (§3.1).
/// Armazena em cache o PID e o nome do processo para evitar chamadas repetidas a
/// <see cref="Process.GetProcessById(int)"/> a cada caractere (N13).
/// </summary>
public sealed record TargetAnchor(
    IntPtr Hwnd,
    uint Pid,
    string ProcessName,
    string Title,
    DateTime CapturedAt);

/// <summary>
/// Incidente registrado quando o destino é perdido durante a transmissão (§3.3).
/// </summary>
public sealed record TargetLostIncident(
    int UnitIndex,
    IntPtr FromHwnd,
    string FromTitle,
    IntPtr ToHwnd,
    string ToTitle,
    DateTime Timestamp);

/// <summary>
/// Abstração para consulta e monitoramento da janela de topo em primeiro plano,
/// permitindo testes unitários determinísticos com provedor falso (§3).
/// </summary>
public interface IForegroundWindowProvider
{
    uint CurrentProcessId { get; }

    /// <summary>Devolve o HWND raiz (<c>GA_ROOT</c>) da janela em primeiro plano, ou <see cref="IntPtr.Zero"/>.</summary>
    IntPtr GetForegroundRootHwnd();

    /// <summary>Obtém apenas o título atual da janela, sem consultar <see cref="Process"/> (barato para cada unidade).</summary>
    string GetWindowTitle(IntPtr hwnd);

    /// <summary>Captura os dados completos (incluindo PID e ProcessName) para criar uma <see cref="TargetAnchor"/>.</summary>
    TargetAnchor? CaptureAnchor();

    /// <summary>Inicia o monitoramento assíncrono de troca de janela em primeiro plano (<c>EVENT_SYSTEM_FOREGROUND</c>).</summary>
    IDisposable SubscribeForegroundChanged(Action<IntPtr> onForegroundRootChanged);
}

/// <summary>Implementação real sobre o user32 (<c>GetForegroundWindow</c>, <c>GetAncestor(GA_ROOT)</c>, <c>SetWinEventHook</c>).</summary>
public sealed class Win32ForegroundWindowProvider : IForegroundWindowProvider
{
    public static Win32ForegroundWindowProvider Instance { get; } = new();

    public uint CurrentProcessId { get; } = (uint)Environment.ProcessId;

    public IntPtr GetForegroundRootHwnd()
    {
        IntPtr hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;
        IntPtr root = Win32.GetAncestor(hwnd, Win32.GA_ROOT);
        return root != IntPtr.Zero ? root : hwnd;
    }

    public string GetWindowTitle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return string.Empty;
        int len = Win32.GetWindowTextLength(hwnd);
        if (len <= 0) return string.Empty;
        var sb = new StringBuilder(len + 1);
        Win32.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public TargetAnchor? CaptureAnchor()
    {
        IntPtr root = GetForegroundRootHwnd();
        if (root == IntPtr.Zero) return null;

        Win32.GetWindowThreadProcessId(root, out uint pid);
        if (pid == 0) return null;

        string processName = string.Empty;
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            processName = proc.ProcessName;
        }
        catch
        {
            processName = "unknown";
        }

        string title = GetWindowTitle(root);
        return new TargetAnchor(root, pid, processName, title, DateTime.UtcNow);
    }

    public IDisposable SubscribeForegroundChanged(Action<IntPtr> onForegroundRootChanged)
    {
        return new WinEventSubscription(onForegroundRootChanged);
    }

    private sealed class WinEventSubscription : IDisposable
    {
        private readonly Action<IntPtr> _callback;
        // Mantém referência viva ao delegate para evitar coleta pelo GC enquanto o hook nativo estiver ativo.
        private readonly Win32.WinEventDelegate _proc;
        private IntPtr _hook;

        public WinEventSubscription(Action<IntPtr> callback)
        {
            _callback = callback;
            _proc = OnWinEvent;
            _hook = Win32.SetWinEventHook(
                Win32.EVENT_SYSTEM_FOREGROUND,
                Win32.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero,
                _proc,
                0,
                0,
                Win32.WINEVENT_OUTOFCONTEXT);
        }

        private void OnWinEvent(
            IntPtr hWinEventHook,
            uint eventType,
            IntPtr hwnd,
            int idObject,
            int idChild,
            uint dwEventThread,
            uint dwmsEventTime)
        {
            IntPtr root = hwnd != IntPtr.Zero ? Win32.GetAncestor(hwnd, Win32.GA_ROOT) : IntPtr.Zero;
            if (root == IntPtr.Zero) root = hwnd;
            _callback(root);
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero)
            {
                Win32.UnhookWinEvent(_hook);
                _hook = IntPtr.Zero;
            }
        }
    }
}
