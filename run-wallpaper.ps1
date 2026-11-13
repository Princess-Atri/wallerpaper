param(
    [switch]$Interactive,
    [string]$Video,
    [int]$DurationSeconds = 0,
    [int]$SampleSeconds = 15,
    [int]$Fps = 60
)

$ErrorActionPreference = 'Continue'

$root = $PSScriptRoot
$log = Join-Path $root 'run-wallpaper.log'
$child = 'D:\Desktop\wallerpaper c\samples\VideoWallpaperHost\bin\Release\net8.0-windows\VideoWallpaperHost.exe'
$parent = 'D:\Desktop\wallerpaper c\samples\VideoWallpaperParent'

# No non-ASCII literal here (Windows PowerShell 5.1 reads BOM-less files as
# ANSI). Use -Video to choose a specific file, otherwise pick the largest mp4.
if ($Video) {
    $video = $Video
}
else {
    $video = Get-ChildItem -LiteralPath 'C:\Users\legion\Videos' -Recurse -Filter '*.mp4' -ErrorAction SilentlyContinue |
        Sort-Object Length -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}

function Write-Log([string]$text) {
    $text | Out-File -FilePath $log -Append -Encoding utf8
}

function Write-LiveLog([string]$text) {
    Write-Host $text
    Write-Log $text
}

'============================================================' | Out-File -FilePath $log -Encoding utf8
Write-Log "Wallerpaper phase-2 test"
Write-Log "time=$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
Write-Log "user=$env:USERDOMAIN\$env:USERNAME"
Write-Log "computer=$env:COMPUTERNAME"
Write-Log "session=$env:SESSIONNAME"
Write-Log "powershell=$((Get-Process -Id $PID).Path)"
Write-Log ""

Write-Log '---- whoami /groups (look for S-1-16) ----'
$groups = & whoami.exe /groups 2>&1
$groups | ForEach-Object { Write-Log $_.ToString() }

Write-Log ''
Write-Log '---- explorer.exe ----'
& tasklist.exe /fi "imagename eq explorer.exe" 2>&1 | ForEach-Object { Write-Log $_.ToString() }

Write-Log ''
Write-Log "CHILD=$child"
Write-Log "VIDEO=$video"
Write-Log "PARENT=$parent"
Write-Log ''

$lowIntegrity = ($groups -join "`n") -match 'S-1-16-4096'
if ($lowIntegrity) {
    Write-Log 'E_INTEGRITY: current PowerShell is Low integrity (S-1-16-4096).'
    Write-Log 'A normal desktop PowerShell must be Medium (S-1-16-8192).'
    Write-Log 'This process cannot attach to Explorer/WorkerW.'
    Write-Log 'Open this .cmd by double-clicking it in Windows Explorer, not from DSH.'
    Write-Host ''
    Write-Host 'E_INTEGRITY: this PowerShell is Low integrity.' -ForegroundColor Red
    Write-Host "Log: $log"
    if ($env:WALLERPAPER_NO_UI -ne '1') {
        Start-Process notepad.exe $log
        Read-Host 'Press Enter to close'
    }
    exit 3
}

if (-not (Test-Path -LiteralPath $child)) {
    Write-Log "ERROR: child not found: $child"
    exit 2
}

if (-not $video -or -not (Test-Path -LiteralPath $video)) {
    Write-Log "ERROR: no local mp4 found under C:\Users\legion\Videos"
    exit 2
}

$childProject = Join-Path $root 'samples\VideoWallpaperHost\VideoWallpaperHost.csproj'
$parentProject = Join-Path $root 'samples\VideoWallpaperParent\VideoWallpaperParent.csproj'
$parentExe = Join-Path $parent 'bin\Release\net8.0-windows\VideoWallpaperParent.exe'
$buildRoot = Join-Path $env:USERPROFILE 'WallerpaperBuild'

Write-Log '---- executable integrity labels ----'
$childLabel = (& icacls.exe $child 2>&1) -join "`n"
$parentLabel = if (Test-Path -LiteralPath $parentExe) { (& icacls.exe $parentExe 2>&1) -join "`n" } else { '' }
Write-Log "child-label: $childLabel"
Write-Log "parent-label: $parentLabel"

# The project may live in a Low-integrity directory (for example, created by a
# sandboxed terminal). New exes then inherit Low, and Windows starts them Low
# even when launched from a Medium PowerShell. Build a Medium copy under the
# user profile instead of trying to relabel the whole workspace.
if ($childLabel -match 'Low Mandatory' -or $parentLabel -match 'Low Mandatory') {
    Write-Log 'Detected Low-integrity EXE; rebuilding into Medium user profile...'
    Write-Log "buildRoot=$buildRoot"

    if (Test-Path -LiteralPath $buildRoot) {
        Remove-Item -LiteralPath $buildRoot -Recurse -Force -ErrorAction Continue
    }

    & dotnet.exe build $childProject -c Release -o (Join-Path $buildRoot 'host') 2>&1 |
        ForEach-Object { Write-LiveLog $_.ToString() }

    & dotnet.exe build $parentProject -c Release -o (Join-Path $buildRoot 'parent') 2>&1 |
        ForEach-Object { Write-LiveLog $_.ToString() }

    $child = Join-Path $buildRoot 'host\VideoWallpaperHost.exe'
    $parent = Join-Path $buildRoot 'parent'
    $parentExe = Join-Path $parent 'VideoWallpaperParent.exe'

    if (-not (Test-Path -LiteralPath (Join-Path $buildRoot 'host\libmpv-2.dll'))) {
        Copy-Item -LiteralPath (Join-Path $root 'samples\VideoWallpaperHost\libmpv-2.dll') `
            -Destination (Join-Path $buildRoot 'host\libmpv-2.dll') -Force
    }

    $childLabel = (& icacls.exe $child 2>&1) -join "`n"
    $parentLabel = (& icacls.exe $parentExe 2>&1) -join "`n"
    Write-Log "child-label-after: $childLabel"
    Write-Log "parent-label-after: $parentLabel"
}

if ($childLabel -match 'Low Mandatory' -or $parentLabel -match 'Low Mandatory') {
    Write-Log 'E_EXE_LOW: EXE still has Low integrity.'
    Write-Log 'Run an elevated PowerShell once to relabel the workspace:'
    Write-Log "  icacls `"$root`" /setintegritylevel `"(OI)(CI)M`" /T /C"
    Write-Host ''
    Write-Host 'E_EXE_LOW: EXE still has Low integrity.' -ForegroundColor Red
    Write-Host "Log: $log"
    if ($env:WALLERPAPER_NO_UI -ne '1') {
        Start-Process notepad.exe $log
        Read-Host 'Press Enter to close'
    }
    exit 3
}

Write-Log '---- parent start ----'
Write-Log "parentExe=$parentExe"
Write-Log "childExe=$child"
Write-Log ''
$runArgs = @('--child', $child, '--video', $video, '--fps', "$Fps", '--volume', '0')
if ($DurationSeconds -gt 0) {
    $runArgs += '--duration'
    $runArgs += "$DurationSeconds"
    $runArgs += '--sample'
    $runArgs += "$SampleSeconds"
    Write-Log "stability mode: duration=${DurationSeconds}s sample=${SampleSeconds}s fps=$Fps"
}
elseif (-not $Interactive) {
    $runArgs += '--auto-test'
}
else {
    Write-Log 'interactive mode: p/play, s/pause, r/resume, i/stats, f/fps, q/stop'
}

Push-Location $parent
try {
    if (Test-Path -LiteralPath $parentExe) {
        & $parentExe @runArgs 2>&1 |
            ForEach-Object { Write-LiveLog $_.ToString() }
    }
    else {
        & dotnet.exe run -- @runArgs 2>&1 |
            ForEach-Object { Write-LiveLog $_.ToString() }
    }
    $rc = $LASTEXITCODE
}
finally {
    Pop-Location
}

Write-Log ''
Write-Log "---- finished, exitcode=$rc ----"
Write-Log "log=$log"

Write-Host ''
Write-Host "Log: $log"
if ($env:WALLERPAPER_NO_UI -ne '1') {
    Start-Process notepad.exe $log
    Read-Host 'Press Enter to close'
}
exit $rc
