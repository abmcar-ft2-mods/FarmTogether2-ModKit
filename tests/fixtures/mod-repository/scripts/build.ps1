#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Hosted', 'LocalInterop')][string]$GameApiMode = 'Hosted',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$InteropDir,
    [string]$GameDir,
    [switch]$DeployToGame
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'Resolve-ModKit.ps1') -LockFile (Join-Path $root 'modkit.lock.json') -Destination (Join-Path $root '.modkit/packages') -PropsOutput (Join-Path $root '.modkit/generated/ModKit.lock.props')
if ($LASTEXITCODE -ne 0) { throw "ModKit resolution failed with exit code $LASTEXITCODE." }
$parameters = @{ RepositoryRoot=$root; GameApiMode=$GameApiMode; Configuration=$Configuration; DeployToGame=$DeployToGame }
if (-not [string]::IsNullOrWhiteSpace($InteropDir)) { $parameters.InteropDir = $InteropDir }
if (-not [string]::IsNullOrWhiteSpace($GameDir)) { $parameters.GameDir = $GameDir }
& (Join-Path $root '.modkit/tooling/scripts/Invoke-ModBuild.ps1') @parameters
if ($LASTEXITCODE -ne 0) { throw "Mod build failed with exit code $LASTEXITCODE." }