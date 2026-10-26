[CmdletBinding()]
param(
    [string]$Version = 'dev'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'app\ZeusCastEnhanced.csproj'
$artifactsDirectory = Join-Path $repositoryRoot 'artifacts'
$publishDirectory = Join-Path $artifactsDirectory 'ZeusCastEnhanced-win-x64'
$archivePath = Join-Path $artifactsDirectory "ZeusCastEnhanced-$Version-win-x64.zip"
$checksumPath = Join-Path $artifactsDirectory "ZeusCastEnhanced-$Version-win-x64.sha256"

$repositoryRootFull = [IO.Path]::GetFullPath($repositoryRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$publishDirectoryFull = [IO.Path]::GetFullPath($publishDirectory)
if (-not $publishDirectoryFull.StartsWith($repositoryRootFull, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing to clean a publish directory outside the repository.'
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET SDK 9 is required to package the application.'
}

if (Test-Path $publishDirectory) {
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}
if (Test-Path $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}
if (Test-Path $checksumPath) {
    Remove-Item -LiteralPath $checksumPath -Force
}

New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
Write-Host 'Creating self-contained Windows x64 release...' -ForegroundColor Cyan
& dotnet publish $projectPath -c Release -r win-x64 -p:Platform=x64 --self-contained true -o $publishDirectory
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Copy-Item -LiteralPath (Join-Path $repositoryRoot 'README.md') -Destination $publishDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination $publishDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'THIRD-PARTY-NOTICES.md') -Destination $publishDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'licenses') -Destination $publishDirectory -Recurse
Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $archivePath -CompressionLevel Optimal
$checksum = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath $checksumPath -Value "$checksum  $([IO.Path]::GetFileName($archivePath))" -Encoding ASCII

Write-Host "Release created: $archivePath" -ForegroundColor Green
Write-Host "Checksum created: $checksumPath" -ForegroundColor Green
Write-Host 'The proprietary GAMDIAS CPUID SDK is not included.' -ForegroundColor Yellow
