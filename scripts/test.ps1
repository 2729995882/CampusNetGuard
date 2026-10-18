[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$app = Join-Path $root "artifacts\bin\CampusNetGuard\$Configuration\net48\CampusNetGuard.exe"
$tests = Join-Path $root "artifacts\bin\CampusNetGuard.SmokeTests\$Configuration\net48\CampusNetGuard.SmokeTests.exe"

& (Join-Path $PSScriptRoot "build.ps1") -Configuration $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE."
}

& $tests $app
if ($LASTEXITCODE -ne 0) {
    throw "Smoke tests failed with exit code $LASTEXITCODE."
}

Write-Host "Smoke tests passed."
