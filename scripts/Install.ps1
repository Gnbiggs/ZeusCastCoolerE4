[CmdletBinding()]
param(
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\ZeusCastEnhanced'),
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'app\ZeusCastEnhanced.csproj'
$applicationPath = Join-Path $InstallDirectory 'Zeus Cast Enhanced.exe'
$vendorSdkPath = Join-Path ${env:ProgramFiles(x86)} 'GAMDIAS\ZEUS CAST\cpuidsdk64.dll'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET SDK 9 is required to install from source. Install it from https://dotnet.microsoft.com/download/dotnet/9.0'
}

$runningProcess = Get-Process -Name 'Zeus Cast Enhanced' -ErrorAction SilentlyContinue
if ($runningProcess) {
    throw 'Zeus Cast Enhanced is running. Right-click its tray icon, choose Exit, then run Install.cmd again.'
}

if (Test-Path $vendorSdkPath) {
    Write-Host 'Official GAMDIAS sensor SDK detected. Native sensor support will be available.' -ForegroundColor Green
}
else {
    Write-Warning 'The official GAMDIAS sensor SDK was not found. LibreHardwareMonitor fallback will be used.'
}

Write-Host "Publishing to $InstallDirectory..." -ForegroundColor Cyan
New-Item -ItemType Directory -Path $InstallDirectory -Force | Out-Null
& dotnet publish $projectPath -c Release -r win-x64 -p:Platform=x64 --self-contained true -o $InstallDirectory
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$shell = New-Object -ComObject WScript.Shell
$desktopShortcutPath = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'Zeus Cast Enhanced.lnk'
$startMenuDirectory = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$startMenuShortcutPath = Join-Path $startMenuDirectory 'Zeus Cast Enhanced.lnk'

foreach ($shortcutPath in @($desktopShortcutPath, $startMenuShortcutPath)) {
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $applicationPath
    $shortcut.WorkingDirectory = $InstallDirectory
    $shortcut.IconLocation = "$applicationPath,0"
    $shortcut.Description = 'Enhanced controller for the GAMDIAS CHIONE Digital cooler display'
    $shortcut.Save()
}

Write-Host 'Installation completed.' -ForegroundColor Green
if (-not $NoLaunch) {
    Write-Host 'Approve the Windows prompt to start Enhanced and configure startup replacement.' -ForegroundColor Yellow
    Start-Process -FilePath $applicationPath -WorkingDirectory $InstallDirectory -Verb RunAs
}
