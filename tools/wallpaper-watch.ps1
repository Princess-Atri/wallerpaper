$ErrorActionPreference = 'Continue'
$root = 'D:\Desktop\wallerpaper c'
$exe = Join-Path $env:LOCALAPPDATA 'Programs\Wallerpaper\WallerpaperShell.exe'
$hostExe = Join-Path $env:LOCALAPPDATA 'Programs\Wallerpaper\WallerpaperHost.exe'
$videoExe = Join-Path $env:LOCALAPPDATA 'Programs\Wallerpaper\VideoWallpaperHost.exe'
$outDir = Join-Path $root 'artifacts\monitor'
$logPath = Join-Path $outDir 'wallpaper-watch.log'
$jsonPath = Join-Path $outDir 'wallpaper-watch.jsonl'
$end = [datetime]'2026-10-29 21:30:00'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$mutex = New-Object System.Threading.Mutex($false, 'Local\WallerpaperPlaybackWatch')
if (-not $mutex.WaitOne(0)) { exit 0 }

function Write-Watch([string]$message) {
    $line = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $message"
    Add-Content -Path $logPath -Value $line -Encoding UTF8
}

function Get-ActiveProcess([string]$name, [string]$path, [int]$minMb = 10) {
    Get-Process $name -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $path -and $_.WorkingSet64 -gt ($minMb * 1MB) } |
        Select-Object -First 1
}

function Start-Playback {
    if (-not (Test-Path $exe)) {
        Write-Watch "ERROR shell missing: $exe"
        return
    }
    Write-Watch 'starting WallerpaperShell'
    Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) | Out-Null
}

function Read-RecentErrors {
    $logDir = Join-Path $env:LOCALAPPDATA 'Wallerpaper\logs'
    $files = @(
        (Join-Path $logDir 'WallerpaperHost-20261029.log'),
        (Join-Path $logDir 'VideoWallpaperHost-20261029.log'),
        (Join-Path $logDir 'WebWallpaperHost-20261029.log'),
        (Join-Path $logDir 'SceneWallpaperHost-20261029.log')
    )
    $errors = @()
    foreach ($file in $files) {
        if (-not (Test-Path $file)) { continue }
        $matches = Get-Content $file -Tail 120 -ErrorAction SilentlyContinue |
            Select-String -Pattern 'E_CMD|ready timeout|child exited unexpectedly|Unhandled|FATAL|dump written|playback loop crashed' |
            Select-Object -Last 3
        foreach ($m in $matches) {
            $errors += "$(Split-Path $file -Leaf): $($m.Line)"
        }
    }
    return $errors
}

Write-Watch "watch start end=$($end.ToString('HH:mm'))"

while ((Get-Date) -lt $end) {
    $shell = Get-ActiveProcess 'WallerpaperShell' $exe 20
    $hostProcess = Get-ActiveProcess 'WallerpaperHost' $hostExe 10
    $video = Get-ActiveProcess 'VideoWallpaperHost' $videoExe 20

    if (-not $shell) {
        Write-Watch 'shell not running; restart'
        Start-Playback
        Start-Sleep -Seconds 8
        $shell = Get-ActiveProcess 'WallerpaperShell' $exe 20
    }

    # A live shell with no renderer means the scheduler is degraded or the
    # app was closed while the shell stayed around. Restart the shell once.
    if ($shell -and -not $hostProcess) {
        Write-Watch 'host missing; restart shell'
        Get-Process WallerpaperShell,WallerpaperHost,VideoWallpaperHost -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -like "$env:LOCALAPPDATA\Programs\Wallerpaper\*" } |
            Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 1
        Start-Playback
        Start-Sleep -Seconds 8
    }

    $shell = Get-ActiveProcess 'WallerpaperShell' $exe 20
    $hostProcess = Get-ActiveProcess 'WallerpaperHost' $hostExe 10
    $video = Get-ActiveProcess 'VideoWallpaperHost' $videoExe 20
    $errors = Read-RecentErrors

    $record = [ordered]@{
        time = (Get-Date).ToString('o')
        shell = if ($shell) { [math]::Round($shell.WorkingSet64 / 1MB, 1) } else { 0 }
        host = if ($hostProcess) { [math]::Round($hostProcess.WorkingSet64 / 1MB, 1) } else { 0 }
        video = if ($video) { [math]::Round($video.WorkingSet64 / 1MB, 1) } else { 0 }
        errors = @($errors | Select-Object -Last 3)
    }
    $record | ConvertTo-Json -Compress | Add-Content -Path $jsonPath -Encoding UTF8

    if ($errors.Count -gt 0) {
        Write-Watch ("ERROR " + ($errors -join ' | '))
    }
    if (-not $hostProcess -or -not $video) {
        Write-Watch "DEGRADED shell=$([bool]$shell) host=$([bool]$hostProcess) video=$([bool]$video)"
    }

    Start-Sleep -Seconds 60
}

Write-Watch 'watch end; leaving current playback running'
$mutex.ReleaseMutex()
$mutex.Dispose()
