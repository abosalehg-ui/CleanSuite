<#
.SYNOPSIS
    Builds the CleanSuite installer in one step: tests, self-contained publish of the program and the command-line tool,
    then the Inno Setup compile.

.DESCRIPTION
    Output:
      artifacts\publish\CleanSuite.App.exe, CleanSuite.Cli.exe    (single files, no .NET needed on the target computer)
      artifacts\installer\CleanSuite-Setup-<version>.exe           (with its SHA-256 printed at the end)
    The version comes from <Version> in src\CleanSuite.App\CleanSuite.App.csproj unless -Version is given.
    ISCC.exe is looked up in -Iscc, the INNO_SETUP environment variable, the Inno Setup uninstall registration
    (per-user and per-machine installs) and the usual install folders.

    Signed release builds (CI, see docs\CODE_SIGNING.md) run in steps, with signing in between:
      -SkipInstaller                          publish only
      -SignedUninstallerDir <dir> -PrepareUninstaller
                                              write the unsigned uninstaller (uninst-*.e32) into <dir> for signing
      -SkipPublish -SignedUninstallerDir <dir>
                                              build the installer from the (signed) programs in artifacts\publish and the
                                              signed uninstaller in <dir>

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\build.ps1
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\build.ps1 -SkipTests -Version 1.0.1
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Iscc,
    [switch]$SkipTests,
    # Reuse the programs already in artifacts\publish (e.g. after they were signed).
    [switch]$SkipPublish,
    # Stop after publishing (the programs are signed next).
    [switch]$SkipInstaller,
    # Folder of the separately signed uninstaller (Inno Setup's SignedUninstallerDir).
    [string]$SignedUninstallerDir,
    # Only write the unsigned uninstaller into -SignedUninstallerDir and print its path.
    [switch]$PrepareUninstaller
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish'
$installerOut = Join-Path $artifacts 'installer'

function Step([string]$text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }

function Invoke-Tool([string]$exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$([IO.Path]::GetFileName($exe)) failed with exit code $LASTEXITCODE" }
}

function Find-Iscc {
    $candidates = New-Object System.Collections.Generic.List[string]
    if ($Iscc) { $candidates.Add($Iscc) }
    if ($env:INNO_SETUP) { $candidates.Add($env:INNO_SETUP); $candidates.Add((Join-Path $env:INNO_SETUP 'ISCC.exe')) }
    foreach ($hive in 'HKCU:', 'HKLM:', 'HKLM:\SOFTWARE\WOW6432Node') {
        $key = if ($hive -like 'HKLM:\*') { "$hive\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1" }
               else { "$hive\Software\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1" }
        $item = Get-ItemProperty -Path $key -ErrorAction SilentlyContinue
        if ($item -and $item.PSObject.Properties['InstallLocation'] -and $item.InstallLocation) {
            $candidates.Add((Join-Path $item.InstallLocation 'ISCC.exe'))
        }
    }
    $candidates.Add((Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'))
    if (${env:ProgramFiles(x86)}) { $candidates.Add((Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')) }
    $candidates.Add((Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'))
    foreach ($c in $candidates) { if ($c -and (Test-Path -LiteralPath $c -PathType Leaf)) { return (Resolve-Path -LiteralPath $c).Path } }
    throw 'ISCC.exe (Inno Setup 6) was not found. Install Inno Setup 6, or pass -Iscc <path to ISCC.exe>.'
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'The .NET 10 SDK (dotnet) is not on PATH.' }
if (-not (dotnet --list-sdks | Where-Object { $_ -match '^(1[0-9]|[2-9][0-9])\.' })) { throw 'The .NET 10 SDK (or later) is required: https://dotnet.microsoft.com/download' }
if (-not $Version) {
    [xml]$project = Get-Content -LiteralPath (Join-Path $root 'src\CleanSuite.App\CleanSuite.App.csproj') -Raw
    $Version = @($project.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
    if (-not $Version) { throw 'No <Version> in CleanSuite.App.csproj; pass -Version.' }
}
if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw "Version '$Version' is not of the form 1.2.3" }
if ($PrepareUninstaller -and -not $SignedUninstallerDir) { throw '-PrepareUninstaller needs -SignedUninstallerDir.' }
if ($SignedUninstallerDir) {
    New-Item -ItemType Directory -Force -Path $SignedUninstallerDir | Out-Null
    $SignedUninstallerDir = (Resolve-Path -LiteralPath $SignedUninstallerDir).Path
}
$isccPath = if ($SkipInstaller) { '(not needed)' } else { Find-Iscc }
Write-Host "CleanSuite $Version  |  Inno Setup: $isccPath"

if (-not $SkipTests -and -not $SkipPublish) {
    Step 'Tests'
    Invoke-Tool 'dotnet' @('test', (Join-Path $root 'CleanSuite.sln'), '-c', 'Release', '--nologo')
}

if (-not $SkipPublish) {
    Step 'Publish (self-contained, single file, win-x64)'
    if (Test-Path -LiteralPath $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }
    $common = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--nologo',
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true',
        '-p:DebugType=none', '-p:DebugSymbols=false', "-p:Version=$Version", '-o', $publish)
    Invoke-Tool 'dotnet' (@('publish', (Join-Path $root 'src\CleanSuite.App\CleanSuite.App.csproj')) + $common)
    Invoke-Tool 'dotnet' (@('publish', (Join-Path $root 'src\CleanSuite.Cli\CleanSuite.Cli.csproj')) + $common)
}
foreach ($exe in 'CleanSuite.App.exe', 'CleanSuite.Cli.exe') {
    if (-not (Test-Path -LiteralPath (Join-Path $publish $exe))) { throw "$exe is missing from $publish" }
}
$extra = Get-ChildItem -LiteralPath $publish | Where-Object { $_.Name -notin 'CleanSuite.App.exe', 'CleanSuite.Cli.exe' }
if ($extra) { throw ("Unexpected files in the publish folder (the installer only takes the two programs): " + ($extra.Name -join ', ')) }

if ($SkipInstaller) {
    Step 'Done (publish only)'
    Get-ChildItem -LiteralPath $publish | ForEach-Object { '{0,-28} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB) }
    return
}

$isccArgs = @('/Qp', "/DAppVersion=$Version", "/DPublishDir=$publish", "/DOutputDir=$installerOut")
if ($SignedUninstallerDir) { $isccArgs += "/DSignedUninstallerDir=$SignedUninstallerDir" }
$isccArgs += (Join-Path $root 'installer\CleanSuite.iss')

if ($PrepareUninstaller) {
    # In signed-uninstaller mode the first compile writes uninst-<version>-<hash>.e32 and stops (exit code 2) until a signed
    # copy replaces it. That stop is expected here; any other outcome is an error.
    Step 'Uninstaller to sign'
    Get-ChildItem -LiteralPath $SignedUninstallerDir -Filter 'uninst-*.e32' | Remove-Item -Force
    & $isccPath @isccArgs | Out-Host
    $uninstaller = Get-ChildItem -LiteralPath $SignedUninstallerDir -Filter 'uninst-*.e32' | Select-Object -First 1
    if (-not $uninstaller) { throw "Inno Setup did not write the uninstaller to $SignedUninstallerDir (exit code $LASTEXITCODE)" }
    $global:LASTEXITCODE = 0
    $uninstaller.FullName
    return
}

Step 'Installer'
New-Item -ItemType Directory -Force -Path $installerOut | Out-Null
Invoke-Tool $isccPath $isccArgs

$setup = Join-Path $installerOut "CleanSuite-Setup-$Version.exe"
if (-not (Test-Path -LiteralPath $setup)) { throw "The installer was not written: $setup" }
$hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
Step 'Done'
Get-ChildItem -LiteralPath $publish, $setup | ForEach-Object { '{0,-28} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB) }
"SHA-256  $hash"
