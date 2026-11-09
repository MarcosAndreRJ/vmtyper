using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VmTyper.TestReceiver;

/// <summary>Um evento de teclado como o Windows o entregou ao hook de baixo nível.</summary>
public sealed record HookEvent(
    long Timestamp,
    double ElapsedMs,
    bool KeyUp,
    uint Vk,
    uint Scan,
    uint Flags,
    bool Extended,
    bool Injected);

/// <summary>
/// Hook WH_KEYBOARD_LL: observa (sem bloquear) todos os eventos de teclado
/// da sessão enquanto o receptor está aberto. Pode ser barrado por EDR; nesse
/// caso o receptor continua funcionando só com o TextBox.
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYUP = 0x0105;
    private const uint LLKHF_EXTENDED = 0x01;
    private const uint LLKHF_INJECTED = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private readonly LowLevelKeyboardProc _proc; // mantido vivo enquanto o hook existir
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _lock = new();
    private readonly List<HookEvent> _events = new();
    private IntPtr _handle;

    public KeyboardHook()
    {
        _proc = Callback;
        _handle = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_handle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"SetWindowsHookEx falhou (erro {Marshal.GetLastWin32Error()}).");
        }
    }

    public IReadOnlyList<HookEvent> Snapshot()
    {
        lock (_lock) return _events.ToList();
    }

    public void Clear()
    {
        lock (_lock) _events.Clear();
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int msg = wParam.ToInt32();
            var e = new HookEvent(
                Stopwatch.GetTimestamp(),
                _clock.Elapsed.TotalMilliseconds,
                msg is WM_KEYUP or WM_SYSKEYUP,
                data.vkCode,
                data.scanCode,
                data.flags,
                (data.flags & LLKHF_EXTENDED) != 0,
                (data.flags & LLKHF_INJECTED) != 0);
            lock (_lock) _events.Add(e);
        }
        return CallNextHookEx(_handle, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
