#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$CandidateDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$ExpectedCommit,
    [Parameter(Mandatory)][ValidateRange(1, [long]::MaxValue)][long]$ExpectedRunId,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ExpectedArtifactName,
    [ValidateSet('Mod', 'Reference')][string]$CandidateKind = 'Mod'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$toolRunner = Join-Path $PSScriptRoot 'Invoke-ModKitTool.ps1'
& $toolRunner candidate verify `
    --directory $CandidateDirectory `
    --kind $CandidateKind `
    --expected-commit $ExpectedCommit `
    --expected-run-id $ExpectedRunId `
    --expected-artifact-name $ExpectedArtifactName
if ($LASTEXITCODE -ne 0) { throw "Candidate verification failed with exit code $LASTEXITCODE." }
