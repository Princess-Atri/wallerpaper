param(
    [int]$ProcessId,
    [int]$Seconds = 600,
    [int]$SampleSeconds = 15,
    [string]$Out = 'perf-samples.csv'
)

$ErrorActionPreference = 'Continue'
$deadline = (Get-Date).AddSeconds($Seconds)
$rows = New-Object System.Collections.Generic.List[string]
$rows.Add('timestamp,workingSetMB,privateMB,cpuTotalSeconds,cpuPercentOneCore,threads,handles')

$lastCpu = $null
$lastTime = $null

while ((Get-Date) -lt $deadline) {
    $p = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if (-not $p) {
        $rows.Add("$(Get-Date -Format o),process-exited,,,,,")
        break
    }

    $now = Get-Date
    $cpu = $p.TotalProcessorTime.TotalSeconds
    $cpuPercent = ''
    if ($null -ne $lastCpu) {
        $elapsed = ($now - $lastTime).TotalSeconds
        if ($elapsed -gt 0) {
            $cpuPercent = [math]::Round(($cpu - $lastCpu) / $elapsed * 100.0, 1)
        }
    }

    $rows.Add((
        '{0},{1},{2},{3},{4},{5},{6}' -f
        $now.ToString('o'),
        [math]::Round($p.WorkingSet64 / 1MB, 1),
        [math]::Round($p.PrivateMemorySize64 / 1MB, 1),
        [math]::Round($cpu, 2),
        $cpuPercent,
        $p.Threads.Count,
        $p.HandleCount
    ))

    $lastCpu = $cpu
    $lastTime = $now
    Start-Sleep -Seconds $SampleSeconds
}

$rows | Out-File -FilePath $Out -Encoding utf8
Write-Host "wrote $Out ($($rows.Count - 1) samples)"
