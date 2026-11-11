param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [Parameter(Mandatory = $true)][string]$OutPng,
    [int]$TimeoutSeconds = 20
)

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Native {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
}
"@

[Native]::SetProcessDPIAware() | Out-Null

$process = Start-Process -FilePath $ExePath -PassThru
try {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $handle = [IntPtr]::Zero
    while ((Get-Date) -lt $deadline) {
        $process.Refresh()
        if ($process.HasExited) { Write-Output "EXITED code=$($process.ExitCode)"; exit 2 }
        if ($process.MainWindowHandle -ne [IntPtr]::Zero -and [Native]::IsWindowVisible($process.MainWindowHandle)) {
            $handle = $process.MainWindowHandle
            break
        }
        Start-Sleep -Milliseconds 250
    }

    if ($handle -eq [IntPtr]::Zero) { Write-Output "NO_WINDOW (processo vivo, sem janela principal visível — talvez iniciado minimizado/na bandeja)"; exit 3 }

    Start-Sleep -Seconds 3   # deixa o layout e o tema assentarem

    $rect = New-Object Native+RECT
    [Native]::GetWindowRect($handle, [ref]$rect) | Out-Null
    $w = $rect.Right - $rect.Left
    $h = $rect.Bottom - $rect.Top
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    $ok = [Native]::PrintWindow($handle, $hdc, 2)   # PW_RENDERFULLCONTENT: captura conteúdo WPF mesmo se ocluído
    $g.ReleaseHdc($hdc)
    $g.Dispose()
    $bmp.Save($OutPng, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output "OK title='$($process.MainWindowTitle)' size=${w}x${h} printwindow=$ok responding=$($process.Responding)"
}
finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
}
