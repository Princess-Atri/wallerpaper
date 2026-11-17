# wallpaper-cover-test.ps1
# Measures how long the renderer stays covered when the system wallpaper is
# changed underneath it (SPI_SETDESKWALLPAPER makes Explorer insert a new
# WorkerW above the render window until the z-order guard restores it).
#
# It paints a solid magenta test image, samples a strip of the screen that only
# the desktop is showing, and reports how many frames (and ms) the magenta was
# visible. No key injection is used.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools\wallpaper-cover-test.ps1
param(
    [int]$DelayMs = 800,
    [int]$AfterMs = 2500,
    [int]$IntervalMs = 20
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$testImage = Join-Path $env:TEMP 'wp-cover-test-magenta.png'
$bmp = New-Object System.Drawing.Bitmap(1920, 1200)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.Clear([System.Drawing.Color]::FromArgb(255, 255, 0, 255))
$g.Dispose()
$bmp.Save($testImage, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host "test image: $testImage"

$current = (Get-ItemProperty 'HKCU:\Control Panel\Desktop' -Name Wallpaper).Wallpaper
Write-Host "current wallpaper: $current"

if (-not ('CoverTest' -as [type])) {
    Add-Type -ReferencedAssemblies 'System.Drawing','System.Core' -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class CoverTest
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool SystemParametersInfo(uint action, uint param, string pvParam, uint winIni);
    [DllImport("shcore.dll")] static extern int SetProcessDpiAwareness(int value);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);

    const uint SPI_SETDESKWALLPAPER = 0x0014;
    const uint SPIF_UPDATEINIFILE = 0x01;
    const uint SPIF_SENDCHANGE = 0x02;

    public static bool SetWallpaper(string path)
    {
        return SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, path,
            SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
    }

    /// <summary>Sample a horizontal strip near the top of the screen (desktop
    /// is visible there even with windows open) and report when it becomes
    /// magenta.</summary>
    public static string Run(string testImage, int delayMs, int afterMs, int intervalMs)
    {
        try { SetProcessDpiAwareness(2); } catch { }
        int w = GetSystemMetrics(0);
        const int SH = 40, SW = 400;
        var sb = new StringBuilder();
        sb.AppendLine("ms,shift,note");
        var sw = Stopwatch.StartNew();
        bool changed = false;
        long coverStart = -1, coverEnd = -1;
        int coverFrames = 0;
        using (var bmp = new Bitmap(SW, SH, PixelFormat.Format32bppArgb))
        using (var g = Graphics.FromImage(bmp))
        {
            while (sw.ElapsedMilliseconds <= delayMs + afterMs)
            {
                if (!changed && sw.ElapsedMilliseconds >= delayMs)
                {
                    sb.AppendLine(sw.ElapsedMilliseconds + ",,SPI magenta");
                    SetWallpaper(testImage);
                    changed = true;
                }

                long t0 = sw.ElapsedMilliseconds;
                g.CopyFromScreen(200, 4, 0, 0, new Size(SW, SH), CopyPixelOperation.SourceCopy);
                // A magenta cover means R and B high, G low.
                long sumR = 0, sumG = 0, sumB = 0;
                int n = 0;
                for (int y = 0; y < SH; y += 4)
                for (int x = 0; x < SW; x += 8)
                {
                    Color c = bmp.GetPixel(x, y);
                    sumR += c.R; sumG += c.G; sumB += c.B; n++;
                }
                double r = sumR / (double)n, gg = sumG / (double)n, b = sumB / (double)n;
                bool magenta = r > 180 && b > 180 && gg < 90;
                if (magenta)
                {
                    coverFrames++;
                    if (coverStart < 0) coverStart = t0;
                    coverEnd = t0;
                    sb.AppendLine(t0 + ",magenta,");
                }

                int sleep = intervalMs - (int)(sw.ElapsedMilliseconds - t0);
                if (sleep > 0) Thread.Sleep(sleep);
            }
        }
        string result = "coverFrames=" + coverFrames;
        if (coverStart >= 0)
            result += " coveredFor=" + (coverEnd - coverStart + intervalMs) + "ms";
        else
            result += " coveredFor=0ms (never visible)";
        return result + "\n" + sb.ToString();
    }
}
'@
}

$result = [CoverTest]::Run($testImage, $DelayMs, $AfterMs, $IntervalMs)
Write-Host $result

if ($current) {
    [void][CoverTest]::SetWallpaper($current)
    Write-Host "restored wallpaper: $current"
}
