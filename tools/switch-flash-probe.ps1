# switch-flash-probe.ps1
# Measures whether a wallpaper switch flashes: reveals the desktop (Win+D),
# samples it at ~30 fps, fires a tray "quick switch" command halfway through,
# then reports dark frames / frozen-image runs / motion dips.
#
# A flash caused by touching the system wallpaper shows up as a run of
# identical frames (the still first-frame image covering the video) — a
# playing wallpaper always keeps changing.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools\switch-flash-probe.ps1 -Index 8
#
# -Index is the 0-based position inside the tray "快速切换壁纸" list
# (catalog sorted by title, first 20 entries).
param(
    [int]$Index = 8,
    [int]$Before = 800,
    [int]$After = 3200,
    [int]$IntervalMs = 25,
    [string]$Csv = "",
    [switch]$NoShowDesktop
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not ('FlashProbe' -as [type])) {
    Add-Type -ReferencedAssemblies 'System.Drawing','System.Core' -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class FlashProbe
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindow(string cls, string name);

    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int index);

    [DllImport("shcore.dll")]
    static extern int SetProcessDpiAwareness(int value);

    [DllImport("user32.dll")]
    static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    const byte VK_LWIN = 0x5B;
    const byte VK_D = 0x44;

    /// <summary>Win+D toggle: reveals the desktop so the wallpaper is sampled
    /// instead of whatever window happens to be on top.</summary>
    public static void ToggleDesktop()
    {
        keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
        keybd_event(VK_D, 0, 0, UIntPtr.Zero);
        keybd_event(VK_D, 0, 2, UIntPtr.Zero);
        keybd_event(VK_LWIN, 0, 2, UIntPtr.Zero);
    }

    public static string Run(int switchIndex, int beforeMs, int afterMs, int intervalMs, bool showDesktop)
    {
        try { SetProcessDpiAwareness(2); } catch { }
        int w = GetSystemMetrics(0), h = GetSystemMetrics(1);
        IntPtr hwnd = FindWindow("STATIC", "WallerpaperTray");
        var sb = new StringBuilder();
        sb.AppendLine("ms,luma,diff,note");
        if (hwnd == IntPtr.Zero)
            return "ERROR: WallerpaperTray window not found (is the app running?)\n";

        if (showDesktop)
        {
            ToggleDesktop();
            Thread.Sleep(700);
        }

        const int SW = 128, SH = 80;
        using (var full = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        using (var small = new Bitmap(SW, SH, PixelFormat.Format32bppArgb))
        using (var gFull = Graphics.FromImage(full))
        using (var gSmall = Graphics.FromImage(small))
        {
            var sw = Stopwatch.StartNew();
            bool posted = false;
            byte[] prev = null;
            long frozenSince = -1;
            while (sw.ElapsedMilliseconds <= beforeMs + afterMs)
            {
                if (!posted && sw.ElapsedMilliseconds >= beforeMs)
                {
                    PostMessage(hwnd, 0x0111, (IntPtr)(1000 + switchIndex), IntPtr.Zero);
                    sb.AppendLine(sw.ElapsedMilliseconds + ",-1,-1,POST " + switchIndex);
                    posted = true;
                }

                long t0 = sw.ElapsedMilliseconds;
                gFull.CopyFromScreen(0, 0, 0, 0, full.Size, CopyPixelOperation.SourceCopy);
                gSmall.DrawImage(full, new Rectangle(0, 0, SW, SH));

                byte[] cur;
                double luma = Read(small, out cur);
                double diff = -1;
                if (prev != null)
                {
                    long sum = 0;
                    for (int i = 0; i < cur.Length; i++)
                        sum += Math.Abs(cur[i] - prev[i]);
                    diff = sum / (double)cur.Length;
                }
                prev = cur;

                string note = "";
                if (diff >= 0 && diff < 0.05)
                {
                    if (frozenSince < 0) frozenSince = t0;
                }
                else if (frozenSince >= 0)
                {
                    note = "frozen " + (t0 - frozenSince) + "ms";
                    frozenSince = -1;
                }
                sb.AppendLine(t0 + "," + luma.ToString("F1") + "," +
                              (diff < 0 ? "" : diff.ToString("F2")) + "," + note);

                int sleep = intervalMs - (int)(sw.ElapsedMilliseconds - t0);
                if (sleep > 0) Thread.Sleep(sleep);
            }
        }
        if (showDesktop)
        {
            ToggleDesktop();
            Thread.Sleep(500);
        }
        return sb.ToString();
    }

    /// <summary>Mean luma + per-pixel luma snapshot (for frame differencing).</summary>
    static double Read(Bitmap bmp, out byte[] values)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        values = new byte[bmp.Width * bmp.Height];
        double total = 0;
        try
        {
            int stride = Math.Abs(data.Stride);
            var buffer = new byte[stride * bmp.Height];
            Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
            int i = 0;
            for (int y = 0; y < bmp.Height; y++)
            {
                int row = y * stride;
                for (int x = 0; x < bmp.Width; x++)
                {
                    int p = row + x * 4;
                    double l = 0.2126 * buffer[p + 2] + 0.7152 * buffer[p + 1] + 0.0722 * buffer[p];
                    values[i++] = (byte)l;
                    total += l;
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return total / values.Length;
    }
}
'@
}

if (-not $Csv) {
    $Csv = Join-Path $PSScriptRoot ("switch-flash-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + ".csv")
}

Write-Host ("probing tray command {0} (+1000), {1}ms before / {2}ms after, {3}ms interval" -f $Index, $Before, $After, $IntervalMs)
$text = [FlashProbe]::Run($Index, $Before, $After, $IntervalMs, -not $NoShowDesktop)
if ($text.StartsWith('ERROR')) {
    Write-Host $text -ForegroundColor Red
    exit 1
}
$text | Set-Content -Path $Csv -Encoding UTF8

$rows = $text -split "`r?`n" | Where-Object { $_ -match '^\d+,\d' } | ForEach-Object {
    $p = $_ -split ','
    [pscustomobject]@{
        Ms   = [int]$p[0]
        Luma = [double]$p[1]
        Diff = if ($p[2]) { [double]$p[2] } else { -1 }
        Note = $p[3]
    }
}
$posted = ($text -split "`r?`n" | Where-Object { $_ -match ',POST ' }) | Select-Object -First 1
$postMs = if ($posted) { [int]($posted -split ',')[0] } else { 0 }

$dark = @($rows | Where-Object { $_.Luma -lt 30 })
$bright = @($rows | Where-Object { $_.Luma -gt 235 })
$frozen = @($rows | Where-Object { $_.Note })
$motion = @($rows | Where-Object { $_.Diff -ge 0 -and $_.Ms -gt $postMs - 300 -and $_.Ms -lt $postMs + 2500 })
$minMotion = if ($motion.Count) { ($motion | Measure-Object Diff -Minimum).Minimum } else { -1 }
$avgMotion = if ($motion.Count) { ($motion | Measure-Object Diff -Average).Average } else { -1 }

Write-Host ""
Write-Host ("frames={0}  span={1}ms  switch at {2}ms" -f $rows.Count, $rows[-1].Ms, $postMs)
Write-Host ("dark frames (<30 luma): {0}" -f $dark.Count)
if ($dark.Count -gt 0) {
    Write-Host ("  first at {0}ms (luma {1:F1}), last at {2}ms" -f $dark[0].Ms, $dark[0].Luma, $dark[-1].Ms)
}
Write-Host ("bright frames (>235): {0}" -f $bright.Count)
Write-Host ("frozen-image runs (frame diff < 0.05): {0}" -f $frozen.Count)
foreach ($s in $frozen) { Write-Host ("  t={0}ms  {1}" -f $s.Ms, $s.Note) }
Write-Host ("motion around switch (diff): avg={0:F2}  min={1:F2}" -f $avgMotion, $minMotion)
Write-Host ("csv: {0}" -f $Csv)

Write-Host ""
Write-Host "timeline (switch at $postMs ms):"
$rows | Where-Object { $_.Ms -ge ($postMs - 300) -and $_.Ms -le ($postMs + 2000) } |
    ForEach-Object { "{0,6}ms  luma={1,6:F1}  diff={2,5:F2}  {3}" -f $_.Ms, $_.Luma, $_.Diff, $_.Note }
