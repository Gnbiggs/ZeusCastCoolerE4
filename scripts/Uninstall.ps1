[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
$isAdministrator = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdministrator) {
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -Verb RunAs -Wait
    exit
}

$installDirectory = Join-Path $env:LOCALAPPDATA 'Programs\ZeusCastEnhanced'
$programsRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs')).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$installDirectoryFull = [IO.Path]::GetFullPath($installDirectory)
if (-not $installDirectoryFull.StartsWith($programsRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing to remove an install directory outside LocalAppData\Programs.'
}

Get-Process -Name 'Zeus Cast Enhanced' -ErrorAction SilentlyContinue | Stop-Process -Force
& "$env:WINDIR\System32\schtasks.exe" /Delete /TN 'Zeus Cast Enhanced' /F 2>$null | Out-Null
& "$env:WINDIR\System32\schtasks.exe" /Change /TN 'ZEUS Switch Utility' /Enable 2>$null | Out-Null

$originalSwitchUtility = Join-Path ${env:ProgramFiles(x86)} 'GAMDIAS\ZEUS CAST\ZEUS Switch Utility.exe'
if (Test-Path $originalSwitchUtility) {
    $runKeyPath = 'Software\Microsoft\Windows\CurrentVersion\Run'
    $localMachine = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::LocalMachine,
        [Microsoft.Win32.RegistryView]::Registry32)
    $runKey = $localMachine.CreateSubKey($runKeyPath, $true)
    $runKey.SetValue('ZEUS Switch Utility', "`"$originalSwitchUtility`"")
    $runKey.Dispose()
    $localMachine.Dispose()
}

$shortcutPaths = @(
    (Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'Zeus Cast Enhanced.lnk'),
    (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Zeus Cast Enhanced.lnk')
)
foreach ($shortcutPath in $shortcutPaths) {
    Remove-Item -LiteralPath $shortcutPath -Force -ErrorAction SilentlyContinue
}

if (Test-Path $installDirectory) {
    Remove-Item -LiteralPath $installDirectory -Recurse -Force
}

Write-Host 'Zeus Cast Enhanced was removed and the original startup was restored.' -ForegroundColor Green
