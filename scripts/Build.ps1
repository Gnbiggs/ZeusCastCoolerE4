[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $repositoryRoot 'ZeusCastEnhanced.sln'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET SDK 9 is required. Install it from https://dotnet.microsoft.com/download/dotnet/9.0'
}

Write-Host "Building Zeus Cast Enhanced ($Configuration)..." -ForegroundColor Cyan
& dotnet restore $solutionPath
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& dotnet build $solutionPath -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host 'Build completed successfully.' -ForegroundColor Green
