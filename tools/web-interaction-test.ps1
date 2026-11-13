$ErrorActionPreference = 'Continue'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Threading;

public static class CursorAnimator
{
    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);

    private static Timer timer;
    private static int index;
    private static readonly int[,] Points = new int[,]
    {
        { 300, 300 }, { 600, 400 }, { 900, 500 }, { 1200, 600 }, { 600, 400 }
    };

    public static void Start()
    {
        SetCursorPos(Points[0, 0], Points[0, 1]);
        timer = new Timer(_ =>
        {
            try
            {
                index = (index + 1) % (Points.GetLength(0));
                SetCursorPos(Points[index, 0], Points[index, 1]);
                if (index == Points.GetLength(0) - 1)
                {
                    // One real click at the final point: it must pass through the
                    // WebView2 wallpaper to the desktop, not reach the page.
                    mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
                    Thread.Sleep(80);
                    mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
                }
            }
            catch { }
        }, null, 1500, 1800);
    }

    public static void Stop()
    {
        timer?.Dispose();
        timer = null;
    }
}
'@

$build = 'C:\Users\legion\WallerpaperBuild'
$hostExe = Join-Path $build 'webhost\WebWallpaperHost.exe'
$parentExe = Join-Path $build 'webparent\WebWallpaperParent.exe'
$html = Join-Path $build 'webhost\wwwroot\test-interaction.html'
$music = 'D:\Desktop\wallerpaper c\test-music.wav'

$sp = New-Object System.Media.SoundPlayer $music
$sp.PlayLooping()
[CursorAnimator]::Start()

try {
    & $parentExe --child $hostExe --html $html --seconds 18 --idle-seconds 12
}
finally {
    [CursorAnimator]::Stop()
    $sp.Stop()
}
