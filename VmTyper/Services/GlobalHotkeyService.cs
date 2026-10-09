using System.Windows.Interop;
using VmTyper.Native;

namespace VmTyper.Services;

/// <summary>
/// Registra/desregistra hotkeys globais (RegisterHotKey) usando uma janela
/// de mensagens invisível (HWND_MESSAGE), que não aparece na tela, não
/// possui foco de UI e portanto não pode "roubar" o foco de outra janela.
/// As mensagens WM_HOTKEY chegam através do loop de mensagens normal do
/// thread de UI (Dispatcher), então os handlers já executam na UI thread.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const int WmHotkey = Win32.WM_HOTKEY;

    private HwndSource? _source;
    private readonly Dictionary<int, Action> _handlers = new();

    public void Initialize()
    {
        var parameters = new HwndSourceParameters("VmTyperHotkeyMessageWindow")
        {
            WindowStyle = 0,
            ExtendedWindowStyle = 0,
            ParentWindow = new IntPtr(-3) // HWND_MESSAGE: janela apenas de mensagens
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public bool RegisterHotkey(int id, uint modifiers, uint virtualKey, Action handler)
    {
        if (_source is null)
            throw new InvalidOperationException("GlobalHotkeyService.Initialize() deve ser chamado antes.");

        bool ok = Win32.RegisterHotKey(_source.Handle, id, modifiers | Win32.MOD_NOREPEAT, virtualKey);
        if (ok)
        {
            _handlers[id] = handler;
        }
        return ok;
    }

    public void UnregisterHotkey(int id)
    {
        if (_source is null) return;
        Win32.UnregisterHotKey(_source.Handle, id);
        _handlers.Remove(id);
    }

    public bool IsRegistered(int id) => _handlers.ContainsKey(id);

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey)
        {
            int id = wParam.ToInt32();
            if (_handlers.TryGetValue(id, out var action))
            {
                handled = true;
                action.Invoke();
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in new List<int>(_handlers.Keys))
        {
            UnregisterHotkey(id);
        }

        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
        }
    }
}
