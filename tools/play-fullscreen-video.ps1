$ErrorActionPreference = 'Stop'
$edge = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
$profile = 'C:\Users\legion\AppData\Local\Temp\wp-edge-play'
$p = Start-Process -FilePath $edge -ArgumentList @(
    '--kiosk', '--no-first-run', '--no-default-browser-check',
    '--autoplay-policy=no-user-gesture-required',
    "--user-data-dir=$profile",
    'file:///C:/Users/legion/WallpaperTestVideo.html'
) -PassThru

Start-Sleep -Seconds 3

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class FgActivate {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
}
'@

$w = Get-Process msedge -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowHandle -ne 0 } |
    Sort-Object StartTime -Descending | Select-Object -First 1
if ($w) {
    [FgActivate]::ShowWindow($w.MainWindowHandle, 3) | Out-Null
    [FgActivate]::SetForegroundWindow($w.MainWindowHandle) | Out-Null
}
Start-Sleep -Seconds 9
"foreground class after activation:"
$fg = [FgActivate]::GetForegroundWindow()
$sb = New-Object System.Text.StringBuilder 256
[FgActivate]::GetClassName($fg, $sb, 256) | Out-Null
"hwnd=0x$($fg.ToInt64().ToString('X')) class=$sb"

Get-Process msedge -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $edge } | Stop-Process -Force -ErrorAction SilentlyContinue
