[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $root ".dotnet\dotnet.exe"
$dotnet = if (Test-Path -LiteralPath $localDotnet) {
    $localDotnet
} else {
    (Get-Command dotnet -ErrorAction Stop).Source
}

$projects = @(
    (Join-Path $root "src\CampusNetGuard\CampusNetGuard.csproj"),
    (Join-Path $root "tests\CampusNetGuard.SmokeTests\CampusNetGuard.SmokeTests.csproj")
)

foreach ($project in $projects) {
    & $dotnet restore $project
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet restore failed for $project with exit code $LASTEXITCODE."
    }
}

foreach ($project in $projects) {
    & $dotnet build $project --configuration $Configuration --no-restore --nologo -m:1
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed for $project with exit code $LASTEXITCODE."
    }
}
