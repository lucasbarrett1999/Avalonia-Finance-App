<#
.SYNOPSIS
  Builds Keel's Windows installers with Velopack (PRD 8, ADR 0083).

.DESCRIPTION
  Publishes Keel self-contained for each runtime and runs `vpk pack`, which produces Keel-<rid>-Setup.exe,
  a portable zip and the update packages under <Out>\releases\<rid>. The version comes from
  Directory.Build.props (VersionPrefix/VersionSuffix). Linux and macOS packages are built by
  build/package.sh on those systems.

  Code signing is optional: set KEEL_WIN_SIGN_TEMPLATE to a signtool command template, e.g.
  'signtool sign /fd sha256 /tr http://timestamp.digicert.com /td sha256 /f cert.pfx /p {password} {{file}}'.

.EXAMPLE
  pwsh build/package.ps1
  pwsh build/package.ps1 -Rid win-x64 -Out artifacts
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string[]] $Rid = @('win-x64', 'win-arm64'),
    [string] $Out = (Join-Path $PSScriptRoot '..' 'artifacts'),
    [switch] $SkipPublish,
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $root 'src/Keel.Desktop/Keel.Desktop.csproj'
$icon = Join-Path $root 'src/Keel.Desktop/Assets/keel.ico'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

function Invoke-Step {
    param([string] $File, [string[]] $Arguments)
    Write-Host "+ $File $($Arguments -join ' ')"
    if (-not $DryRun) {
        & $File @Arguments
        if ($LASTEXITCODE -ne 0) { throw "$File failed with exit code $LASTEXITCODE" }
    }
}

Push-Location $root
try {
    $version = (& dotnet msbuild $project -getProperty:Version).Trim()
    if (-not $version) { throw 'Could not read the version from Directory.Build.props' }
    Write-Host "Keel $version`: $($Rid -join ', ')"
    Invoke-Step dotnet @('tool', 'restore')

    foreach ($r in $Rid) {
        $publish = Join-Path $Out "publish/$r"
        $releases = Join-Path $Out "releases/$r"
        New-Item -ItemType Directory -Force -Path $releases | Out-Null

        if (-not ($SkipPublish -and (Test-Path $publish))) {
            if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
            Invoke-Step dotnet @('publish', $project, '-c', 'Release', '-r', $r, '--self-contained', 'true',
                '-p:DebugType=none', '-p:DebugSymbols=false', '-p:PublishReadyToRun=false', '-o', $publish)
        }

        # Budget files open through SQLCipher (ADR 0101): the native library must ship with the app.
        $sqlcipher = Join-Path $publish 'e_sqlcipher.dll'
        Write-Host "+ check $sqlcipher (SQLCipher native library)"
        if (-not $DryRun -and -not (Test-Path $sqlcipher)) { throw "The SQLCipher native library is missing from $publish" }

        $pack = @('tool', 'run', 'vpk', 'pack',
            '--packId', 'Keel', '--packVersion', $version, '--packDir', $publish,
            '--mainExe', 'Keel.Desktop.exe', '--packTitle', 'Keel', '--packAuthors', 'Keel contributors',
            '--icon', $icon, '--channel', $r, '--runtime', $r, '--outputDir', $releases,
            '--shortcuts', 'Desktop,StartMenuRoot')
        if ($env:KEEL_WIN_SIGN_TEMPLATE) { $pack += @('--signTemplate', $env:KEEL_WIN_SIGN_TEMPLATE) }
        Invoke-Step dotnet $pack
    }

    if (-not $DryRun) {
        Write-Host 'Packages:'
        Get-ChildItem -Recurse -File (Join-Path $Out 'releases') | ForEach-Object { Write-Host "  $($_.FullName) ($($_.Length) bytes)" }
    }
}
finally {
    Pop-Location
}
