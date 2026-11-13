$ErrorActionPreference = 'Stop'
$root = 'D:\Desktop\wallerpaper c'
$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish'
$installerOut = Join-Path $artifacts 'installer'
$payload = Join-Path $root 'samples\Installer\WallerpaperSetup\Resources\payload.zip'
$env:DOTNET_CLI_HOME = Join-Path $root '.dotnet'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:NUGET_PACKAGES = Join-Path $root '.nuget\packages'

Get-Process WallerpaperShell, WallerpaperHost, SceneWallpaperHost, VideoWallpaperHost, WebWallpaperHost -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue

Remove-Item $artifacts -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $publish, $installerOut -Force | Out-Null
New-Item -ItemType Directory -Path (Split-Path $payload) -Force | Out-Null

Write-Host '[1/6] publish WinUI shell (self-contained folder)'
dotnet publish (Join-Path $root 'samples\WallerpaperShell\WallerpaperShell.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:WindowsAppSDKSelfContained=true -p:PublishTrimmed=false `
    -o $publish

# Publish every Host self-contained into the SAME app directory. The Shell
# is self-contained, but framework-dependent Hosts previously failed on a
# machine without the .NET 8 runtime ("You must install or update .NET").
$hostProjects = @(
    'samples\WallerpaperHost\WallerpaperHost.csproj',
    'samples\SceneWallpaperHost\SceneWallpaperHost.csproj',
    'samples\VideoWallpaperHost\VideoWallpaperHost.csproj',
    'samples\WebWallpaperHost\WebWallpaperHost.csproj'
)
foreach ($proj in $hostProjects) {
    $projectPath = Join-Path $root $proj
    if (-not (Test-Path $projectPath)) { throw "missing project: $projectPath" }
    dotnet publish $projectPath -c Release -r win-x64 --self-contained true `
        -p:PublishTrimmed=false -p:PublishSingleFile=false -o $publish
}

Write-Host '[2/6] verify payload'
$required = @('WallerpaperShell.exe', 'WallerpaperHost.exe', 'SceneWallpaperHost.exe',
              'VideoWallpaperHost.exe', 'WebWallpaperHost.exe', 'resources.pri')
foreach ($name in $required) {
    if (-not (Test-Path (Join-Path $publish $name))) { throw "payload missing $name" }
}

Write-Host '[3/6] create payload.zip'
Add-Type -AssemblyName System.IO.Compression.FileSystem
Remove-Item $payload -Force -ErrorAction SilentlyContinue
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $publish, $payload, [System.IO.Compression.CompressionLevel]::Optimal, $false)

Write-Host '[4/6] publish single-file installer'
dotnet publish (Join-Path $root 'samples\Installer\WallerpaperSetup\WallerpaperSetup.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $installerOut

$setup = Join-Path $installerOut 'WallerpaperSetup.exe'
if (-not (Test-Path $setup)) { throw 'installer output missing' }
Copy-Item $setup (Join-Path $artifacts 'WallerpaperSetup.exe') -Force

Write-Host '[5/6] single-file WinUI limitation'
Write-Host 'WinUI 3 self-contained app is delivered as a folder inside the installer;'
Write-Host 'PublishSingleFile on WallerpaperShell is NOT supported at runtime (XAML activation failure).'

Write-Host '[6/6] done'
Get-Item (Join-Path $artifacts 'WallerpaperSetup.exe'), $payload |
    Select-Object FullName, Length, LastWriteTime
