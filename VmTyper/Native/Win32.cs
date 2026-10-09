using System.Runtime.InteropServices;

namespace VmTyper.Native;

/// <summary>
/// Ponto único de acesso às APIs nativas do Windows usadas pelo aplicativo.
/// Nenhuma outra classe deve conter [DllImport] diretamente — isso mantém
/// o restante do código testável e livre de detalhes de interop.
/// </summary>
internal static class Win32
{
    // ---------------------------------------------------------------
    // SendInput / INPUT
    // ---------------------------------------------------------------

    public const uint INPUT_MOUSE = 0;
    public const uint INPUT_KEYBOARD = 1;
    public const uint INPUT_HARDWARE = 2;

    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_UNICODE = 0x0004;
    public const uint KEYEVENTF_SCANCODE = 0x0008;

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    // O layout abaixo (uint "type" seguido da union) é a forma comprovada de
    // fazer o marshaling da struct INPUT do Win32 corretamente tanto em x86
    // quanto em x64: o runtime aplica o padding de 4 bytes após "type"
    // automaticamente, pois a union contém um IntPtr (8 bytes em x64) que
    // exige alinhamento de 8 bytes — reproduzindo exatamente o layout nativo.
    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    public static readonly int InputSize = Marshal.SizeOf(typeof(INPUT));

    // ---------------------------------------------------------------
    // Virtual keys usadas pelo aplicativo
    // ---------------------------------------------------------------

    public const ushort VK_LSHIFT = 0xA0;
    public const ushort VK_RSHIFT = 0xA1;
    public const ushort VK_LCONTROL = 0xA2;
    public const ushort VK_RCONTROL = 0xA3;
    public const ushort VK_LMENU = 0xA4; // Alt esquerdo
    public const ushort VK_RMENU = 0xA5; // Alt direito / AltGr
    public const ushort VK_RETURN = 0x0D;
    public const ushort VK_TAB = 0x09;
    public const ushort VK_ESCAPE = 0x1B;
    public const ushort VK_SHIFT = 0x10;
    public const ushort VK_CONTROL = 0x11;
    public const ushort VK_MENU = 0x12;
    public const ushort VK_HOME = 0x24;
    public const ushort VK_DELETE = 0x2E;

    /// <summary>
    /// Teclas que o Windows trata como "estendidas" e que exigem o flag
    /// KEYEVENTF_EXTENDEDKEY para que o scan code seja interpretado
    /// corretamente (ex.: Alt/Ctrl direitos, setas, Insert/Delete etc.).
    /// </summary>
    public static bool IsExtendedKey(ushort vk) => vk switch
    {
        VK_RMENU => true,
        VK_RCONTROL => true,
        0x21 or 0x22 or 0x23 or 0x24 => true, // PageUp/PageDown/End/Home
        0x25 or 0x26 or 0x27 or 0x28 => true, // Setas
        0x2D or 0x2E => true,                  // Insert/Delete
        0x90 => true,                          // NumLock
        _ => false
    };

    // ---------------------------------------------------------------
    // Teclado / layout
    // ---------------------------------------------------------------

    [DllImport("user32.dll")]
    public static extern short VkKeyScanEx(char ch, IntPtr dwhkl);

    [DllImport("user32.dll")]
    public static extern uint MapVirtualKeyEx(uint uCode, uint uMapType, IntPtr dwhkl);

    public const uint MAPVK_VK_TO_VSC = 0;
    public const uint MAPVK_VK_TO_VSC_EX = 4;

    [DllImport("user32.dll")]
    public static extern IntPtr GetKeyboardLayout(uint idThread);

    // ---------------------------------------------------------------
    // Janela ativa / processo em primeiro plano
    // ---------------------------------------------------------------

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    // ---------------------------------------------------------------
    // Hotkeys globais
    // ---------------------------------------------------------------

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    public const int WM_HOTKEY = 0x0312;

    // ---------------------------------------------------------------
    // Helpers de alto nível para montar INPUTs de teclado
    // ---------------------------------------------------------------

    public static INPUT KeyDownByVirtualKey(ushort vk)
    {
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = IsExtendedKey(vk) ? KEYEVENTF_EXTENDEDKEY : 0,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
    }

    public static INPUT KeyUpByVirtualKey(ushort vk)
    {
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = KEYEVENTF_KEYUP | (IsExtendedKey(vk) ? KEYEVENTF_EXTENDEDKEY : 0),
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
    }

    public static INPUT ScanKeyDown(ushort scanCode, bool extended)
    {
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = scanCode,
                    dwFlags = KEYEVENTF_SCANCODE | (extended ? KEYEVENTF_EXTENDEDKEY : 0),
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
    }

    public static INPUT ScanKeyUp(ushort scanCode, bool extended)
    {
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = scanCode,
                    dwFlags = KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP | (extended ? KEYEVENTF_EXTENDEDKEY : 0),
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
    }

    public static INPUT UnicodeKeyDown(char c)
    {
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = c,
                    dwFlags = KEYEVENTF_UNICODE,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
    }

    public static INPUT UnicodeKeyUp(char c)
    {
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = c,
                    dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
    }

    /// <summary>
    /// Envia um lote de INPUTs em uma única chamada a SendInput (mais confiável
    /// e atômico do ponto de vista da fila de entrada do que várias chamadas
    /// separadas para cada tecla).
    /// </summary>
    public static void SendInputs(IReadOnlyList<INPUT> inputs)
    {
        if (inputs.Count == 0) return;
        var array = inputs as INPUT[] ?? new List<INPUT>(inputs).ToArray();
        uint sent = SendInput((uint)array.Length, array, InputSize);
        if (sent != array.Length)
        {
            int error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"SendInput enviou apenas {sent}/{array.Length} eventos. Código de erro Win32: {error}.");
        }
    }

    /// <summary>
    /// Força a liberação (KeyUp) de todas as teclas modificadoras que o
    /// aplicativo eventualmente usa, para garantir que nenhuma fique
    /// "logicamente pressionada" em caso de cancelamento ou exceção.
    /// </summary>
    public static void ReleaseAllModifiers()
    {
        var ups = new List<INPUT>
        {
            KeyUpByVirtualKey(VK_LSHIFT),
            KeyUpByVirtualKey(VK_RSHIFT),
            KeyUpByVirtualKey(VK_LCONTROL),
            KeyUpByVirtualKey(VK_RCONTROL),
            KeyUpByVirtualKey(VK_LMENU),
            KeyUpByVirtualKey(VK_RMENU),
        };
        try
        {
            SendInputs(ups);
        }
        catch
        {
            // Melhor esforço: se até a liberação falhar, não há mais nada
            // que possamos fazer a partir daqui.
        }
    }
}
