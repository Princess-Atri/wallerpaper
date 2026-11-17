# apply-swap-test.ps1
# Exercises the scheduler's preload-and-swap path without the WinUI shell:
# starts WallerpaperHost on a scratch scheduler config, then sends
# {"cmd":"apply"} after rewriting that config to a second wallpaper of the
# requested kind. Prints the host's apply / shown / preload lines.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools\apply-swap-test.ps1 -Kind scene
#   powershell -ExecutionPolicy Bypass -File tools\apply-swap-test.ps1 -Kind web
param(
    [ValidateSet('scene', 'web', 'video', 'sequence', 'static')]
    [string]$Kind = 'scene'
)

$ErrorActionPreference = 'Stop'
$app = 'C:\Users\legion\AppData\Local\Programs\Wallerpaper'
$cfgPath = Join-Path $env:LOCALAPPDATA 'Wallerpaper\scheduler-test.json'
$shellCfg = Join-Path $env:LOCALAPPDATA 'Wallerpaper\shell-config.json'

$items = (Get-Content $shellCfg -Raw | ConvertFrom-Json).wallpapers |
    Where-Object { $_.kind.ToLowerInvariant() -eq $Kind }
if ($items.Count -lt 2) {
    throw "need at least two '$Kind' wallpapers in shell-config.json, found $($items.Count)"
}
$a = $items[0]
$b = $items[1]

function Write-Config([object]$item) {
    $base = Get-Content (Join-Path $env:LOCALAPPDATA 'Wallerpaper\scheduler.json') -Raw | ConvertFrom-Json
    $type = $item.kind.ToLowerInvariant()
    $scenePath = if ($type -eq 'scene') { $item.path } else { '' }
    $base.type = $type
    $base.wallpaperPath = $item.path
    $base.scene = $scenePath
    $m = $base.monitors.'\\.\DISPLAY1'
    $m.type = $type
    $m.wallpaperPath = $item.path
    $m.scene = $scenePath
    $base | ConvertTo-Json -Depth 6 | Set-Content $cfgPath -Encoding UTF8
}

Write-Config $a
Write-Host ("host config: {0} / {1}" -f $a.kind, $a.title)

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = Join-Path $app 'WallerpaperHost.exe'
$psi.Arguments = '--config "' + $cfgPath + '" --seconds 45'
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.CreateNoWindow = $true
$psi.WorkingDirectory = $app

$p = New-Object System.Diagnostics.Process
$p.StartInfo = $psi
$global:hostLines = New-Object System.Collections.ArrayList
$null = Register-ObjectEvent -InputObject $p -EventName OutputDataReceived -Action {
    if ($EventArgs.Data) { [void]$global:hostLines.Add($EventArgs.Data) }
}
$p.Start() | Out-Null
$p.BeginOutputReadLine()
$p.BeginErrorReadLine()

Start-Sleep -Seconds 9
Write-Host '--- after startup ---'
$global:hostLines | Where-Object { $_ -match 'apply|shown|Attached|ready pid|child started' } |
    Select-Object -Last 8

Write-Config $b
Write-Host ("apply -> {0} / {1}" -f $b.kind, $b.title)
$cmd = @{ cmd = 'apply'; device = '\\.\DISPLAY1' } | ConvertTo-Json -Compress
$p.StandardInput.WriteLine($cmd)
$p.StandardInput.Flush()

Start-Sleep -Seconds 12
Write-Host '--- after apply ---'
$global:hostLines | Where-Object { $_ -match 'apply|shown|preload' } | Select-Object -Last 12

try { $p.Kill() } catch { }
