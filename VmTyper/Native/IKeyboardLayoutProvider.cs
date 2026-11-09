namespace VmTyper.Native;

/// <summary>
/// Fronteira mínima sobre as APIs de layout de teclado, para que a resolução
/// caractere → tecla possa ser testada com tabelas fixas (ABNT2, US-Intl)
/// independentemente do layout instalado na máquina.
/// </summary>
internal interface IKeyboardLayoutProvider
{
    /// <summary>Layout (HKL) da thread dona da janela em primeiro plano.</summary>
    IntPtr GetLayout();

    /// <summary>Carrega um layout de teclado por KLID (ex.: "00010416" ABNT2, "00020409" US-Intl) sem alterar o shell.</summary>
    IntPtr LoadLayout(string klid) => GetLayout();

    /// <summary>Equivalente a VkKeyScanEx.</summary>
    short VkKeyScan(char c, IntPtr layout);

    /// <summary>Equivalente a MapVirtualKeyEx.</summary>
    uint MapVirtualKey(uint code, uint mapType, IntPtr layout);
}

/// <summary>Implementação real sobre o user32.</summary>
internal sealed class Win32KeyboardLayoutProvider : IKeyboardLayoutProvider
{
    public static Win32KeyboardLayoutProvider Instance { get; } = new();

    public IntPtr GetLayout()
    {
        IntPtr hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;
        uint threadId = Win32.GetWindowThreadProcessId(hwnd, out _);
        return Win32.GetKeyboardLayout(threadId);
    }

    public IntPtr LoadLayout(string klid)
    {
        if (string.IsNullOrWhiteSpace(klid) || string.Equals(klid, "HostForeground", StringComparison.OrdinalIgnoreCase))
            return GetLayout();
        IntPtr hkl = Win32.LoadKeyboardLayout(klid.Trim(), Win32.KLF_NOTELLSHELL);
        return hkl != IntPtr.Zero ? hkl : GetLayout();
    }

    public short VkKeyScan(char c, IntPtr layout) => Win32.VkKeyScanEx(c, layout);

    public uint MapVirtualKey(uint code, uint mapType, IntPtr layout) => Win32.MapVirtualKeyEx(code, mapType, layout);
}
