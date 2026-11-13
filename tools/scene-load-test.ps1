param(
    [Parameter(Mandatory = $true)][string]$Scene,
    [int]$Seconds = 8,
    [int]$Fps = 60,
    [string]$Tag = "load",
    [switch]$Vsync
)

$ErrorActionPreference = 'Stop'
$build = 'C:\Users\legion\WallerpaperBuild'
$parent = Join-Path $build 'sceneparent\SceneWallpaperParent.exe'
$childExe = Join-Path $build 'scenehost\SceneWallpaperHost.exe'
$out = Join-Path $env:TEMP "scene-load-$Tag.out.log"
$err = Join-Path $env:TEMP "scene-load-$Tag.err.log"

Remove-Item -LiteralPath $out, $err -Force -ErrorAction SilentlyContinue

$parentArgs = @(
    '--child', $childExe,
    '--scene', $Scene,
    '--seconds', $Seconds,
    '--fps', $Fps,
    '--no-audio'
)
if ($Vsync) { $parentArgs += '--vsync' }
$p = Start-Process -FilePath $parent -ArgumentList $parentArgs -PassThru -WindowStyle Hidden -RedirectStandardOutput $out -RedirectStandardError $err

# Wait for the child and its first frames.
$child = $null
for ($i = 0; $i -lt 60 -and $null -eq $child; $i++) {
    Start-Sleep -Milliseconds 250
    $child = Get-Process SceneWallpaperHost -ErrorAction SilentlyContinue |
        Sort-Object StartTime -Descending | Select-Object -First 1
}
if ($null -eq $child) {
    if (!$p.HasExited) { $p.Kill($true) }
    throw "SceneWallpaperHost did not start. stderr: $(Get-Content $err -Raw -ErrorAction SilentlyContinue)"
}
$pidValue = $child.Id
Start-Sleep -Seconds 2

$maxDedicated = 0
$maxCommit = 0
$maxWs = 0
$maxPrivate = 0
$maxHandles = 0
$peakCpu = 0

while (!$p.HasExited) {
    try {
        $samples = (Get-Counter '\GPU Process Memory(*)\Dedicated Usage' -ErrorAction SilentlyContinue).CounterSamples |
            Where-Object { $_.InstanceName -like "pid_${pidValue}_*" }
        $ded = ($samples | Measure-Object CookedValue -Sum).Sum
        if ($null -ne $ded -and $ded -gt $maxDedicated) { $maxDedicated = $ded }
    } catch { }

    try {
        $proc = Get-Process -Id $pidValue -ErrorAction SilentlyContinue
        if ($proc) {
            $maxWs = [Math]::Max($maxWs, $proc.WorkingSet64)
            $maxPrivate = [Math]::Max($maxPrivate, $proc.PrivateMemorySize64)
            $maxHandles = [Math]::Max($maxHandles, $proc.HandleCount)
            $peakCpu = [Math]::Max($peakCpu, $proc.TotalProcessorTime.TotalSeconds)
        }
    } catch { }
    Start-Sleep -Milliseconds 500
}

$p.WaitForExit()
$lastStats = Get-Content $out -ErrorAction SilentlyContinue |
    Select-String '"type":"stats"' | Select-Object -Last 1
$statsText = if ($lastStats) { $lastStats.Line } else { '' }
$stats = $null
if ($statsText -match '(\{.*\})') {
    try { $stats = $matches[1] | ConvertFrom-Json } catch { }
}

$result = [pscustomobject]@{
    Tag = $Tag
    Scene = $Scene
    FpsCap = $Fps
    ActualFps = $stats.fps
    CpuPercent = $stats.cpuPercent
    Layers = $stats.layers
    Particles = $stats.particles
    ScriptMs = $stats.scriptMs
    DrawMs = $stats.drawMs
    PresentMs = $stats.presentMs
    ProcessDedicatedVramMb = [Math]::Round($maxDedicated / 1MB, 1)
    ProcessCommittedVramMb = [Math]::Round($maxCommit / 1MB, 1)
    MaxWorkingSetMb = [Math]::Round($maxWs / 1MB, 1)
    MaxPrivateMb = [Math]::Round($maxPrivate / 1MB, 1)
    MaxHandles = $maxHandles
    Stats = $statsText
}

$result | Format-List
$result | ConvertTo-Json -Compress | Add-Content -LiteralPath 'D:\Desktop\wallerpaper c\scene-load-results.jsonl'
