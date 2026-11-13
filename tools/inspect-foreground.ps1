$ErrorActionPreference = 'Stop'
$edge = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
$profile = 'C:\Users\legion\AppData\Local\Temp\wp-edge-inspect'
$p = Start-Process -FilePath $edge -ArgumentList @(
    '--kiosk', '--no-first-run', '--no-default-browser-check',
    '--autoplay-policy=no-user-gesture-required',
    "--user-data-dir=$profile",
    'file:///C:/Users/legion/WallpaperTestVideo.html'
) -PassThru

Start-Sleep -Seconds 4

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class Fg {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public static string Dump(IntPtr h) {
        var cls = new StringBuilder(256); GetClassName(h, cls, 256);
        var title = new StringBuilder(512); GetWindowText(h, title, 512);
        RECT r; GetWindowRect(h, out r);
        RECT d; DwmGetWindowAttribute(h, 9, out d, Marshal.SizeOf(typeof(RECT)));
        long style = GetWindowLongPtr(h, -16).ToInt64();
        long ex = GetWindowLongPtr(h, -20).ToInt64();
        return string.Format("hwnd=0x{0:X} class={1} title={2} zoomed={3} style=0x{4:X8} ex=0x{5:X8} rect={6},{7},{8},{9} dwm={10},{11},{12},{13}",
            h.ToInt64(), cls, title, IsZoomed(h), style, ex, r.Left, r.Top, r.Right, r.Bottom,
            d.Left, d.Top, d.Right, d.Bottom);
    }
}
'@

$windows = Get-Process msedge -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowHandle -ne 0 } |
    Select-Object Id, MainWindowHandle
$windows | ForEach-Object { "edge id=$($_.Id) hwnd=0x$($_.MainWindowHandle.ToInt64().ToString('X'))" }
$target = $windows | Select-Object -First 1
if ($target) {
    [Fg]::SetForegroundWindow($target.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 800
}
"foreground after activation:"
[Fg]::Dump([Fg]::GetForegroundWindow())
if ($target) {
    "target:"
    [Fg]::Dump($target.MainWindowHandle)
}

Get-Process msedge -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $edge } | Stop-Process -Force -ErrorAction SilentlyContinue
