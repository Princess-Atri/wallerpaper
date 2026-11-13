$ErrorActionPreference = 'Stop'
$video = 'C:\Users\legion\Videos\屏幕录制\屏幕录制 2026-10-25 182749.mp4'
$player = Start-Process -FilePath 'C:\Program Files\Windows Media Player\wmplayer.exe' `
    -ArgumentList @('/fullscreen', $video) -PassThru
Start-Sleep -Seconds 4

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class FgInspect {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public static string Dump(IntPtr h) {
        var sb = new StringBuilder(256); GetClassName(h, sb, 256);
        var tt = new StringBuilder(512); GetWindowText(h, tt, 512);
        RECT r, c; GetWindowRect(h, out r); GetClientRect(h, out c);
        long style = GetWindowLongPtr(h, -16).ToInt64();
        long ex = GetWindowLongPtr(h, -20).ToInt64();
        return string.Format("hwnd=0x{0:X} class={1} title={2} zoomed={3} style=0x{4:X8} ex=0x{5:X8} rect={6},{7},{8},{9} client={10}x{11}",
            h.ToInt64(), sb, tt, IsZoomed(h), style, ex, r.Left, r.Top, r.Right, r.Bottom, c.Right, c.Left);
    }
}
'@

$h = $player.MainWindowHandle
if ($h -eq [IntPtr]::Zero) {
    $h = [FgInspect]::GetForegroundWindow()
}
[FgInspect]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds 500
"player hwnd=0x$($h.ToInt64().ToString('X'))"
[FgInspect]::Dump($h)
"foreground:"
[FgInspect]::Dump([FgInspect]::GetForegroundWindow())

if (!$player.HasExited) {
    $player.CloseMainWindow() | Out-Null
    Start-Sleep -Seconds 1
    if (!$player.HasExited) { $player.Kill() }
}
