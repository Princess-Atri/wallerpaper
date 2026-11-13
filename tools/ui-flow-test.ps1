$ErrorActionPreference = 'Stop'
$exe = 'C:\Users\legion\WallerpaperBuild\shell\WallerpaperShell.exe'
$cfg = Join-Path $env:LOCALAPPDATA 'Wallerpaper\shell-config.json'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runName = 'WallerpaperShell'
$results = New-Object System.Collections.Generic.List[string]
$originalRun = $null

function Log([string]$msg) {
    $line = "$(Get-Date -Format 'HH:mm:ss') $msg"
    Write-Host $line
    $results.Add($line) | Out-Null
}

function Fail([string]$msg) {
    throw "FAIL: $msg"
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class FlowNative {
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int ht, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
}
'@

function Get-AppRoot([int]$procId) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $procId)
    return [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Children, $cond)
}

function Wait-AppRoot([int]$procId, [int]$timeoutMs = 10000) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $r = Get-AppRoot $procId
        if ($r) { return $r }
        Start-Sleep -Milliseconds 200
    }
    return $null
}

function Find-All($root, [string]$name) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Find-One($root, [string]$name, $type = $null) {
    $all = Find-All $root $name
    foreach ($e in $all) {
        if (-not $type -or $e.Current.ControlType -eq $type) { return $e }
    }
    return $null
}

function Invoke-Button($root, [string]$name) {
    $el = Find-One $root $name
    if (-not $el) { Fail "button not found: $name" }
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $cur = $el
    for ($i = 0; $i -lt 4 -and $cur; $i++) {
        if ($cur.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button) {
            $p = $cur.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
            $p.Invoke()
            return
        }
        $cur = $walker.GetParent($cur)
    }
    Fail "invoke target not a button: $name"
}

function Select-Nav($root, [string]$name) {
    $all = Find-All $root $name
    foreach ($e in $all) {
        if ($e.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem) {
            $p = $e.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
            $p.Select()
            return
        }
    }
    Fail "nav item not found: $name"
}

function Select-Card($root, [string]$name) {
    $all = Find-All $root $name
    foreach ($e in $all) {
        if ($e.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem) {
            $p = $e.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
            $p.Select()
            return
        }
    }
    Fail "card not found: $name"
}

function Set-Slider($root, [string]$name, [double]$value) {
    $el = Find-One $root $name ([System.Windows.Automation.ControlType]::Slider)
    if (-not $el) { Fail "slider not found: $name" }
    $p = $el.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
    $p.SetValue($value)
}

function Set-Number($root, [string]$name, [double]$value) {
    $el = Find-One $root $name
    if (-not $el) { Fail "number box not found: $name" }
    try {
        $p = $el.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
        $p.SetValue($value)
        return
    } catch { }
    $p = $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $p.SetValue([string]$value)
}

function Set-Toggle($root, [string]$name, [bool]$on) {
    $all = Find-All $root $name
    foreach ($el in $all) {
        try {
            $p = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
            $want = if ($on) { [System.Windows.Automation.ToggleState]::On } else { [System.Windows.Automation.ToggleState]::Off }
            if ($p.Current.ToggleState -ne $want) { $p.Toggle() }
            return
        } catch { }
    }
    foreach ($el in $all) {
        try {
            $p = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
            $p.Invoke()
            return
        } catch { }
    }
    Fail "toggle not found: $name"
}

function Get-ToggleState($root, [string]$name) {
    $el = Find-One $root $name
    if (-not $el) { return $null }
    try {
        return $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState
    } catch { return $null }
}

function Save-Shot([string]$path) {
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Log "screenshot: $path"
}

function Get-TrayHwnd {
    return [FlowNative]::FindWindow('STATIC', 'WallerpaperTray')
}

function Show-App([int]$procId) {
    $p = Get-Process -Id $procId -ErrorAction SilentlyContinue
    if ($p -and $p.MainWindowHandle -ne 0) {
        [FlowNative]::ShowWindow($p.MainWindowHandle, 9) | Out-Null
        [FlowNative]::SetWindowPos($p.MainWindowHandle, [IntPtr](-1), 0, 0, 0, 0, 0x0001 -bor 0x0002) | Out-Null
        [FlowNative]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
        Start-Sleep -Milliseconds 500
    }
}

function Start-App {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process -FilePath $exe -PassThru
    $root = Wait-AppRoot $p.Id 12000
    if (-not $root) { Fail "app window did not appear within 12s" }
    $sw.Stop()
    Log "app started pid=$($p.Id) in $([math]::Round($sw.Elapsed.TotalSeconds,2))s"
    Show-App $p.Id
    return @{ Process = $p; Root = (Get-AppRoot $p.Id) }
}

function Stop-App([int]$procId) {
    $h = Get-TrayHwnd
    if ($h -ne [IntPtr]::Zero) {
        [FlowNative]::PostMessage($h, 0x0111, [IntPtr]2, [IntPtr]::Zero) | Out-Null
        Start-Sleep -Milliseconds 400
        [FlowNative]::PostMessage($h, 0x0111, [IntPtr]4, [IntPtr]::Zero) | Out-Null
        Start-Sleep -Seconds 2
    }
    Get-Process -Id $procId -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Get-Process WallerpaperHost, VideoWallpaperHost, SceneWallpaperHost, WebWallpaperHost -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

# --- baseline ---
Get-Process WallerpaperShell, WallerpaperHost, VideoWallpaperHost, SceneWallpaperHost, WebWallpaperHost -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
$originalRun = $null
try { $originalRun = (Get-ItemProperty -Path $runKey -Name $runName -ErrorAction Stop).$runName } catch { }

$started = Start-App
$app = $started.Process
$root = $started.Root

# --- browse ---
$items = Find-All $root 'demo'
if ($items.Count -eq 0) { Fail 'video card demo not found' }
Log "video cards named 'demo': $($items.Count)"
Select-Card $root 'demo'
Start-Sleep -Milliseconds 700
$root = Get-AppRoot $app.Id
$thumbCache = Join-Path $env:LOCALAPPDATA 'Wallerpaper\cache'
$thumbs = @(Get-ChildItem $thumbCache -Filter '*.png' -ErrorAction SilentlyContinue)
Log "thumbnail cache files: $($thumbs.Count)"
if ($thumbs.Count -eq 0) { Fail 'no thumbnail generated' }
Save-Shot 'D:\Desktop\wallerpaper c\docs\assets\shell-flow-video.png'

# --- apply + properties ---
Invoke-Button $root '应用壁纸'
Start-Sleep -Seconds 4
$hv = Get-Process WallerpaperHost, VideoWallpaperHost -ErrorAction SilentlyContinue
Log "host processes after apply: $(($hv | ForEach-Object { $_.ProcessName }) -join ',')"
if (-not ($hv | Where-Object ProcessName -eq 'VideoWallpaperHost')) { Fail 'VideoWallpaperHost not running' }

$mute = Find-One $root '静音'
if ($mute) {
    try { Set-Toggle $root '静音' $false } catch { Log "mute toggle skipped: $($_.Exception.Message)" }
}
Set-Slider $root '音量' 33
Set-Slider $root '播放速度' 1.25
Start-Sleep -Seconds 1
$saved = Get-Content $cfg -Raw | ConvertFrom-Json
$demoSaved = $saved.wallpapers | Where-Object title -eq 'demo'
Log "config after property edit: volume=$($demoSaved.volume) speed=$($demoSaved.speed) muted=$($demoSaved.muted)"
Save-Shot 'D:\Desktop\wallerpaper c\docs\assets\shell-flow-properties.png'

# --- preview ---
Invoke-Button $root '预览'
Start-Sleep -Seconds 2
$preview = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
    [System.Windows.Automation.TreeScope]::Children,
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)))
$previewWin = $preview | Where-Object { $_.Current.Name -like '预览*' } | Select-Object -First 1
if (-not $previewWin) { Fail 'preview window not found' }
Log "preview window: $($previewWin.Current.Name)"
Save-Shot 'D:\Desktop\wallerpaper c\docs\assets\shell-flow-preview.png'
$previewWin.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
Start-Sleep -Milliseconds 600
$root = Get-AppRoot $app.Id

# --- minimize to tray ---
$root.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
Start-Sleep -Seconds 1
if (-not (Get-Process -Id $app.Id -ErrorAction SilentlyContinue)) { Fail 'app exited instead of minimizing to tray' }
$tray = Get-TrayHwnd
if ($tray -eq [IntPtr]::Zero) { Fail 'tray window not found' }
Log "minimized to tray, process alive, tray hwnd=0x$($tray.ToInt64().ToString('X'))"

# --- tray context menu ---
[FlowNative]::PostMessage($tray, 0x8001, [IntPtr]::Zero, [IntPtr]0x0205) | Out-Null
Start-Sleep -Milliseconds 800
$menuNames = @()
foreach ($n in @('打开 Wallerpaper','播放壁纸','暂停壁纸','快速切换壁纸','设置','退出')) {
    $e = Find-One ([System.Windows.Automation.AutomationElement]::RootElement) $n
    if ($e) { $menuNames += $n }
}
Log "tray menu items visible: $($menuNames -join ', ')"
if ($menuNames.Count -eq 0) { Fail 'tray menu did not open' }
Save-Shot 'D:\Desktop\wallerpaper c\docs\assets\shell-flow-tray.png'
[FlowNative]::keybd_event(0x1B, 0, 0, [UIntPtr]::Zero)
[FlowNative]::keybd_event(0x1B, 0, 2, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 400

# Use the same callback the menu invokes (WM_COMMAND id=2).
[FlowNative]::PostMessage($tray, 0x0111, [IntPtr]2, [IntPtr]::Zero) | Out-Null
Start-Sleep -Milliseconds 500
[FlowNative]::PostMessage($tray, 0x0111, [IntPtr]1, [IntPtr]::Zero) | Out-Null
Start-Sleep -Milliseconds 800
Show-App $app.Id
$root = Get-AppRoot $app.Id
$pauseText = Find-One $root '播放'
Log "pause label switched to: $($pauseText.Current.Name)"
if (-not $pauseText) { Fail 'pause label did not switch to 播放' }

# --- settings + persistence ---
Select-Nav $root '设置'
Start-Sleep -Seconds 1
$root = Get-AppRoot $app.Id
$scrolls = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ClassNameProperty, 'ScrollViewer')))
$scrolled = $false
foreach ($s in $scrolls) {
    try {
        $sp = $s.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
        if ($sp.Current.VerticallyScrollable) {
            $sp.SetScrollPercent(-1, 100)
            $scrolled = $true
            break
        }
    } catch { }
}
Log "settings scrolled to bottom: $scrolled"
Start-Sleep -Milliseconds 700
Set-Number $root '交流电 FPS' 50
Set-Toggle $root '随 Windows 启动' $true
Start-Sleep -Milliseconds 500
Invoke-Button $root '保存设置'
Start-Sleep -Seconds 2
$saved = Get-Content $cfg -Raw | ConvertFrom-Json
Log "config after settings: acFps=$($saved.performance.acFps) autostart=$($saved.general.startWithWindows)"
if ($saved.performance.acFps -ne 50) { Fail 'AC FPS not persisted' }
$runValue = $null
try { $runValue = (Get-ItemProperty -Path $runKey -Name $runName -ErrorAction Stop).$runName } catch { }
Log "HKCU Run value: $runValue"
if (-not $runValue) { Fail 'autostart registry value missing' }
Save-Shot 'D:\Desktop\wallerpaper c\docs\assets\shell-flow-settings.png'

# --- exit and restart persistence ---
Stop-App $app.Id
Start-Sleep -Seconds 1
$started = Start-App
$app = $started.Process
$root = $started.Root
$saved = Get-Content $cfg -Raw | ConvertFrom-Json
Log "relaunch loaded: acFps=$($saved.performance.acFps) autostart=$($saved.general.startWithWindows)"
Start-Sleep -Seconds 3
$hv = Get-Process WallerpaperHost, VideoWallpaperHost -ErrorAction SilentlyContinue
Log "relaunch host processes: $(($hv | ForEach-Object { $_.ProcessName }) -join ',')"
if ($saved.performance.acFps -ne 50) { Fail 'config lost after restart' }

# --- simulated logon autostart (actual reboot is not performed) ---
Stop-App $app.Id
Start-Sleep -Seconds 1
$auto = Start-Process -FilePath $exe -ArgumentList @('--minimized') -PassThru
Start-Sleep -Seconds 4
if (-not (Get-Process -Id $auto.Id -ErrorAction SilentlyContinue)) { Fail 'autostart --minimized exited' }
$tray = Get-TrayHwnd
if ($tray -eq [IntPtr]::Zero) { Fail 'autostart tray window missing' }
$visible = $false
if ($auto.MainWindowHandle -ne 0) { $visible = [FlowNative]::IsWindowVisible($auto.MainWindowHandle) }
Log "autostart simulation: pid=$($auto.Id), tray=0x$($tray.ToInt64().ToString('X')), mainVisible=$visible"
if ($visible) { Fail '--minimized left main window visible' }
Start-Sleep -Seconds 2
$hv = Get-Process WallerpaperHost, VideoWallpaperHost -ErrorAction SilentlyContinue
Log "autostart simulation host processes: $(($hv | ForEach-Object { $_.ProcessName }) -join ',')"
Stop-App $auto.Id

# --- restore autostart to original ---
if ($originalRun) {
    Set-ItemProperty -Path $runKey -Name $runName -Value $originalRun
    Log "autostart restored to original value"
} else {
    Remove-ItemProperty -Path $runKey -Name $runName -ErrorAction SilentlyContinue
    Log "autostart registry value removed after test"
}

$results | Set-Content 'D:\Desktop\wallerpaper c\tools\ui-flow-results.txt' -Encoding UTF8
Log 'FLOW PASS'

