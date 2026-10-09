using System.Diagnostics;
using VmTyper.Models;
using VmTyper.Native;

namespace VmTyper.Services;

/// <summary>
/// Consulta qual é a janela/processo em primeiro plano no host Windows.
/// Usado apenas como proteção contra envio acidental de teclas para a
/// janela errada — não tenta (nem pode) enxergar dentro da VM, pois o
/// Notepad da VM não é uma janela real do processo host. A validação é
/// sempre feita sobre o navegador (processo host) que hospeda o console
/// VMware.
/// </summary>
public sealed class ActiveWindowService
{
    private static readonly Dictionary<ExpectedBrowser, string[]> BrowserProcessNames = new()
    {
        [ExpectedBrowser.Chrome] = new[] { "chrome" },
        [ExpectedBrowser.Edge] = new[] { "msedge" },
        [ExpectedBrowser.Firefox] = new[] { "firefox" },
    };

    public string? GetForegroundProcessName()
    {
        IntPtr hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;

        Win32.GetWindowThreadProcessId(hwnd, out uint processId);
        if (processId == 0) return null;

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    public bool MatchesExpectedBrowser(ExpectedBrowser expected, out string? actualProcessName)
    {
        actualProcessName = GetForegroundProcessName();

        if (expected == ExpectedBrowser.Nenhum) return true;
        if (actualProcessName is null) return false;
        if (!BrowserProcessNames.TryGetValue(expected, out var candidates)) return true;

        foreach (var candidate in candidates)
        {
            if (string.Equals(actualProcessName, candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
