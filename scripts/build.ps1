[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$UseCompilerFallback
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root "CampusNetGuard.sln"
$frameworkDir = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319"
$outputRoot = Join-Path $root "artifacts\bin\$Configuration"
$testOutputRoot = Join-Path $root "artifacts\tests\$Configuration"
$referenceAssemblies = Join-Path ${env:ProgramFiles(x86)} "Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8"

function Find-MSBuild {
    $candidates = @()
    if ($env:MSBUILD_EXE_PATH) {
        $candidates += $env:MSBUILD_EXE_PATH
    }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path -LiteralPath $vswhere) {
        $found = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe | Select-Object -First 1
        if ($found) {
            $candidates += $found
        }
    }
    $candidates += (Join-Path $frameworkDir "MSBuild.exe")
    return $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}

function Invoke-CompilerFallback {
    $csc = Join-Path $frameworkDir "csc.exe"
    if (-not (Test-Path -LiteralPath $csc)) {
        throw "No supported MSBuild installation or C# compiler was found."
    }

    New-Item -ItemType Directory -Force -Path $outputRoot, $testOutputRoot | Out-Null
    $sourceDir = Join-Path $root "src\CampusNetGuard"
    $sourceFiles = Get-ChildItem -LiteralPath $sourceDir -Filter "*.cs" | Sort-Object Name | Select-Object -ExpandProperty FullName
    $references = @(
        "System.dll",
        "System.Core.dll",
        "System.Drawing.dll",
        "System.Web.dll",
        "System.Web.Extensions.dll",
        "System.Windows.Forms.dll"
    ) | ForEach-Object { "/reference:$(Join-Path $frameworkDir $_)" }

    $optimize = if ($Configuration -eq "Release") { "/optimize+" } else { "/optimize-" }
    $debug = if ($Configuration -eq "Release") { "/debug:pdbonly" } else { "/debug:full" }
    $appOutput = Join-Path $outputRoot "CampusNetGuard.exe"
    $appArgs = @(
        "/nologo",
        "/target:winexe",
        "/platform:anycpu",
        $optimize,
        $debug,
        "/define:TRACE",
        "/win32manifest:$(Join-Path $sourceDir 'app.manifest')",
        "/out:$appOutput"
    ) + $references + $sourceFiles

    & $csc @appArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Application compilation failed with exit code $LASTEXITCODE."
    }

    $testOutput = Join-Path $testOutputRoot "CampusNetGuard.SmokeTests.exe"
    $testArgs = @(
        "/nologo",
        "/target:exe",
        "/platform:anycpu",
        $optimize,
        $debug,
        "/out:$testOutput",
        "/reference:$(Join-Path $frameworkDir 'System.dll')",
        "/reference:$(Join-Path $frameworkDir 'System.Core.dll')",
        (Join-Path $root "tests\CampusNetGuard.SmokeTests\ReflectionSmokeTest.cs")
    )
    & $csc @testArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Smoke-test compilation failed with exit code $LASTEXITCODE."
    }

    Write-Host "Built with the C# compiler fallback."
}

if ($UseCompilerFallback) {
    Invoke-CompilerFallback
    exit 0
}

if (-not (Test-Path -LiteralPath $referenceAssemblies)) {
    Write-Host ".NET Framework 4.8 reference assemblies are not installed; using the local compiler."
    Invoke-CompilerFallback
    exit 0
}

$msbuild = Find-MSBuild
if (-not $msbuild) {
    Invoke-CompilerFallback
    exit 0
}

& $msbuild $solution /m /t:Rebuild "/p:Configuration=$Configuration" "/p:Platform=Any CPU"
if ($LASTEXITCODE -ne 0) {
    Write-Warning "MSBuild failed. This usually means the .NET Framework 4.8 targeting pack is missing; retrying with the local compiler."
    Invoke-CompilerFallback
}
