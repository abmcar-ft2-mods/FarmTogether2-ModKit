#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$OutputDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $root 'artifacts' }
& (Join-Path $PSScriptRoot 'Resolve-ModKit.ps1') -LockFile (Join-Path $root 'modkit.lock.json') -Destination (Join-Path $root '.modkit/packages') -PropsOutput (Join-Path $root '.modkit/generated/ModKit.lock.props')
if ($LASTEXITCODE -ne 0) { throw "ModKit resolution failed with exit code $LASTEXITCODE." }
& (Join-Path $root '.modkit/tooling/scripts/Pack-Mod.ps1') -RepositoryRoot $root -Configuration $Configuration -OutputDirectory $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw "Mod packaging failed with exit code $LASTEXITCODE." }