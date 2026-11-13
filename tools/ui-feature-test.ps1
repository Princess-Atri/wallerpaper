$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

$exe = Join-Path $env:LOCALAPPDATA 'Programs\Wallerpaper\WallerpaperShell.exe'
$configPath = Join-Path $env:LOCALAPPDATA 'Wallerpaper\shell-config.json'
$backupPath = Join-Path $env:TEMP 'wallerpaper-shell-config-feature-backup.json'
$testDelete = Join-Path $env:USERPROFILE 'Videos\Wallerpaper\feature-delete.mp4'
$testSearch = Join-Path $env:USERPROFILE 'Downloads\feature-search.mp4'

Copy-Item $configPath $backupPath -Force
Copy-Item (Join-Path $env:USERPROFILE 'Videos\Wallerpaper\demo.mp4') $testDelete -Force
Copy-Item (Join-Path $env:USERPROFILE 'Videos\Wallerpaper\demo.mp4') $testSearch -Force

Get-Process WallerpaperShell -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400
$script:process = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 5
$script:processId = [int]$script:process.Id

function Get-Root {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $script:processId)
    return [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Children, $cond)
}

function Find-Name([string]$name, $type = $null) {
    $root = Get-Root
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    foreach ($e in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
        if (-not $type -or $e.Current.ControlType -eq $type) { return $e }
    }
    return $null
}

function Find-Prefix([string]$prefix, $type = $null) {
    $root = Get-Root
    foreach ($e in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition)) {
        if (($e.Current.Name -like "$prefix*") -and (-not $type -or $e.Current.ControlType -eq $type)) {
            return $e
        }
    }
    return $null
}

function Invoke-Button([string]$name) {
    $button = Find-Name $name ([System.Windows.Automation.ControlType]::Button)
    if (-not $button) { throw "button not found: $name" }
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Select-Item([string]$name) {
    $item = Find-Name $name ([System.Windows.Automation.ControlType]::ListItem)
    if (-not $item) { throw "item not found: $name" }
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 300
    return $item
}

function Read-Titles {
    $cfg = Get-Content $configPath -Raw | ConvertFrom-Json
    return @($cfg.wallpapers | ForEach-Object { $_.title })
}

$demoTitle = (Get-Content $configPath -Raw | ConvertFrom-Json).wallpapers |
    Where-Object { $_.path -like '*\demo.mp4' } |
    Select-Object -First 1 -ExpandProperty title
if (-not $demoTitle) { throw 'demo.mp4 not found in config' }

Write-Host "--- rename ($demoTitle) ---"
Select-Item $demoTitle | Out-Null
Invoke-Button '重命名壁纸'
Start-Sleep -Seconds 1
$edit = Find-Name '输入新的壁纸名称' ([System.Windows.Automation.ControlType]::Edit)
$edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('demo 重命名测试')
Invoke-Button '保存'
Start-Sleep -Seconds 1
$titles = Read-Titles
if ($titles -notcontains 'demo 重命名测试') { throw 'rename did not persist' }
Write-Host "rename persisted: $($titles -join ', ')"

# Restore original title for the rest of the test.
Select-Item 'demo 重命名测试' | Out-Null
Invoke-Button '重命名壁纸'
Start-Sleep -Seconds 1
$edit = Find-Name '输入新的壁纸名称' ([System.Windows.Automation.ControlType]::Edit)
$edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($demoTitle)
Invoke-Button '保存'
Start-Sleep -Seconds 1

Write-Host '--- delete ---'
Invoke-Button '刷新目录'
Start-Sleep -Seconds 2
Select-Item 'feature-delete' | Out-Null
Invoke-Button '删除壁纸'
Start-Sleep -Seconds 1
Invoke-Button '删除'
Start-Sleep -Seconds 2
$cfg = Get-Content $configPath -Raw | ConvertFrom-Json
if ($cfg.wallpapers.title -contains 'feature-delete') { throw 'deleted item still in config' }
if (Test-Path $testDelete) { Write-Host 'source file kept: yes' } else { throw 'source file was deleted' }
Write-Host 'delete persisted (source file kept)'

Write-Host '--- local search ---'
$search = Find-Name '搜索本机视频' ([System.Windows.Automation.ControlType]::Edit)
$search.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('feature-search')
$search.SetFocus()
Start-Sleep -Milliseconds 300
[System.Windows.Forms.SendKeys]::SendWait('{ENTER}')

$found = $false
$sw = [Diagnostics.Stopwatch]::StartNew()
while ($sw.ElapsedMilliseconds -lt 90000) {
    $title = Find-Prefix '找到' ([System.Windows.Automation.ControlType]::Text)
    $result = Find-Prefix 'feature-search' ([System.Windows.Automation.ControlType]::ListItem)
    if ($result) { $found = $true; break }
    Start-Sleep -Milliseconds 500
}
if (-not $found) { throw 'local search returned no feature-search result' }

$result = Find-Prefix 'feature-search' ([System.Windows.Automation.ControlType]::ListItem)
$result.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Invoke-Button '添加壁纸'
Start-Sleep -Seconds 2
$cfg = Get-Content $configPath -Raw | ConvertFrom-Json
if (-not ($cfg.library | Where-Object { $_.path -eq $testSearch })) {
    throw 'search result was not added to library'
}
Write-Host 'search add persisted'

Get-Process WallerpaperShell -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
Copy-Item $backupPath $configPath -Force
Remove-Item $testDelete, $testSearch -Force -ErrorAction SilentlyContinue
Write-Host 'FEATURE TEST PASS'
