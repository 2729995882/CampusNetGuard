[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$app = Join-Path $root "artifacts\bin\$Configuration\CampusNetGuard.exe"
$tests = Join-Path $root "artifacts\tests\$Configuration\CampusNetGuard.SmokeTests.exe"

if (-not (Test-Path -LiteralPath $app) -or -not (Test-Path -LiteralPath $tests)) {
    & (Join-Path $PSScriptRoot "build.ps1") -Configuration $Configuration
}

& $tests $app
if ($LASTEXITCODE -ne 0) {
    throw "Smoke tests failed with exit code $LASTEXITCODE."
}

Write-Host "Smoke tests passed."
