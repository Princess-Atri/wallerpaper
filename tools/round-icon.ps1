# round-icon.ps1 - gives the app icon rounded corners and rebuilds the multi-size ICO.
param(
    [string]$SourceIco = 'D:\Desktop\wallerpaper c\samples\WallerpaperShell\Assets\app.ico',
    [string]$OutIco = 'D:\Desktop\wallerpaper c\samples\WallerpaperShell\Assets\app.ico',
    [double]$CornerPercent = 20,
    [string]$PreviewPng = 'D:\Desktop\wallerpaper c\icon-rounded-preview.png'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# 1) Pull the largest frame (PNG-compressed 256) out of the current .ico.
$bytes = [System.IO.File]::ReadAllBytes($SourceIco)
$count = [BitConverter]::ToUInt16($bytes, 4)
$best = $null
for ($i = 0; $i -lt $count; $i++) {
    $off = 6 + 16 * $i
    $w = $bytes[$off]; if ($w -eq 0) { $w = 256 }
    $size = [BitConverter]::ToInt32($bytes, $off + 8)
    $pos = [BitConverter]::ToInt32($bytes, $off + 12)
    if ($best -eq $null -or $w -gt $best.W) { $best = [pscustomobject]@{ W = $w; Size = $size; Pos = $pos } }
}
$png = New-Object byte[] $best.Size
[Array]::Copy($bytes, $best.Pos, $png, 0, $best.Size)
$srcStream = New-Object System.IO.MemoryStream(,$png)
$src = [System.Drawing.Image]::FromStream($srcStream)
Write-Host ("source frame: {0}x{1}" -f $src.Width, $src.Height)

# 2) Draw it clipped to a rounded square (anti-aliased), transparent outside.
$w = $src.Width; $h = $src.Height
$radius = [int]([Math]::Round([Math]::Min($w, $h) * $CornerPercent / 100.0))
$rounded = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($rounded)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$g.Clear([System.Drawing.Color]::Transparent)
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$d = $radius * 2
$path.AddArc(0, 0, $d, $d, 180, 90)
$path.AddArc($w - $d, 0, $d, $d, 270, 90)
$path.AddArc($w - $d, $h - $d, $d, $d, 0, 90)
$path.AddArc(0, $h - $d, $d, $d, 90, 90)
$path.CloseFigure()
$g.SetClip($path)
$g.DrawImage($src, 0, 0, $w, $h)
$g.ResetClip()
# A very light inner outline keeps the rounded shape readable on white
# backgrounds (Explorer list view, taskbar tooltips, light dialogs).
$pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(58, 120, 165, 205), 2)
$g.DrawPath($pen, $path)
$pen.Dispose()
$g.Dispose()
$path.Dispose()
$src.Dispose()
$srcStream.Dispose()

$rounded.Save($PreviewPng, [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host ("preview: {0} (radius {1}px on {2}px)" -f $PreviewPng, $radius, $w)

# 3) Rebuild a multi-size ICO from the rounded art.
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$entries = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $sg = [System.Drawing.Graphics]::FromImage($bmp)
    $sg.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $sg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $sg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $sg.Clear([System.Drawing.Color]::Transparent)
    $r2 = [int]([Math]::Round($s * $CornerPercent / 100.0))
    $p2 = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d2 = [Math]::Max(2, $r2 * 2)
    $p2.AddArc(0, 0, $d2, $d2, 180, 90)
    $p2.AddArc($s - $d2, 0, $d2, $d2, 270, 90)
    $p2.AddArc($s - $d2, $s - $d2, $d2, $d2, 0, 90)
    $p2.AddArc(0, $s - $d2, $d2, $d2, 90, 90)
    $p2.CloseFigure()
    $sg.SetClip($p2)
    $sg.DrawImage($rounded, (New-Object System.Drawing.Rectangle(0, 0, $s, $s)))
    $sg.ResetClip()
    $sg.Dispose()
    $p2.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $entries += [pscustomobject]@{ Size = $s; Bytes = $ms.ToArray() }
    $ms.Dispose()
    $bmp.Dispose()
}
$rounded.Dispose()

$fs = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$entries.Count)
$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$e.Bytes.Length); $bw.Write([UInt32]$offset)
    $offset += $e.Bytes.Length
}
foreach ($e in $entries) { $bw.Write($e.Bytes) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($OutIco, $fs.ToArray())
$bw.Dispose(); $fs.Dispose()
Write-Host ("icon written: {0} bytes, {1} sizes" -f (Get-Item $OutIco).Length, $entries.Count)
