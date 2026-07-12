#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ModConfig,
    [Parameter(Mandatory)][string]$Artifacts
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$toolRunner = Join-Path $PSScriptRoot 'Invoke-ModKitTool.ps1'
& $toolRunner mod-package verify --mod-config $ModConfig --artifacts $Artifacts
if ($LASTEXITCODE -ne 0) { throw "Player package verification failed with exit code $LASTEXITCODE." }
