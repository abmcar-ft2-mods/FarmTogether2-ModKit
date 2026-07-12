#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$PublishedDirectory,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$CandidateDirectory,
    [ValidateSet('Mod', 'Reference')][string]$CandidateKind = 'Mod'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$toolRunner = Join-Path $PSScriptRoot 'Invoke-ModKitTool.ps1'
& $toolRunner candidate verify-published `
    --published-directory $PublishedDirectory `
    --candidate-directory $CandidateDirectory `
    --kind $CandidateKind
if ($LASTEXITCODE -ne 0) { throw "Published asset verification failed with exit code $LASTEXITCODE." }
