[CmdletBinding()]
param(
    [string]$Version = "1.0.0",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$releaseRoot = Join-Path $root "artifacts\releases"
$stage = Join-Path $root "artifacts\package\CampusNetGuard-$Version"
$zip = Join-Path $releaseRoot "CampusNetGuard-$Version.zip"

& (Join-Path $PSScriptRoot "build.ps1") -Configuration $Configuration
& (Join-Path $PSScriptRoot "test.ps1") -Configuration $Configuration

if (Test-Path -LiteralPath $stage) {
    Remove-Item -LiteralPath $stage -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $stage, $releaseRoot | Out-Null

Copy-Item -LiteralPath (Join-Path $root "artifacts\bin\$Configuration\CampusNetGuard.exe") -Destination $stage
Copy-Item -LiteralPath (Join-Path $root "README.md"), (Join-Path $root "CHANGELOG.md") -Destination $stage
New-Item -ItemType Directory -Force -Path (Join-Path $stage "docs") | Out-Null
Copy-Item -LiteralPath (Join-Path $root "docs\使用说明.md"), (Join-Path $root "docs\CampusNetGuard开发设计与实现说明.docx") -Destination (Join-Path $stage "docs")

if (Test-Path -LiteralPath $zip) {
    Remove-Item -LiteralPath $zip -Force
}
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -CompressionLevel Optimal
$hash = Get-FileHash -LiteralPath $zip -Algorithm SHA256
"$($hash.Hash)  $(Split-Path -Leaf $zip)" | Set-Content -LiteralPath ($zip + ".sha256") -Encoding ascii

Write-Host "Created $zip"
