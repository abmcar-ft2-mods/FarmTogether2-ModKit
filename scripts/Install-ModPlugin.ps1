#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$SourceDll,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$GameDir,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$AssemblyName
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:PathComparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }

function Assert-NoReparseAncestor([string]$Path, [string]$Label) {
    $current = [IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrEmpty($current)) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "$Label contains a symlink or reparse-point ancestor: $current" }
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ([string]::IsNullOrEmpty($parent) -or [string]::Equals($parent, $current, $script:PathComparison)) { break }
        $current = $parent
    }
}

function Assert-RegularFile([string]$Path, [string]$Label) {
    Assert-NoReparseAncestor $Path $Label
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "$Label does not exist: $Path" }
    $item = Get-Item -LiteralPath $Path -Force
    if (-not ($item -is [IO.FileInfo]) -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "$Label must be a regular file: $Path" }
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-PathLockName([string]$Path) {
    $identity = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Path))
    if ($IsWindows) { $identity = $identity.ToUpperInvariant() }
    $hash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($identity))
    return "FarmTogether2.ModKit.Deploy.$([Convert]::ToHexString($hash).ToLowerInvariant())"
}

function Copy-DurableVerifiedFile([string]$Source, [string]$Destination, [string]$Label) {
    Assert-RegularFile $Source $Label
    Assert-NoReparseAncestor $Destination "$Label destination"
    if (Test-Path -LiteralPath $Destination) { throw "$Label destination already exists: $Destination" }
    $sourceStream = [IO.FileStream]::new($Source, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $destinationStream = [IO.FileStream]::new($Destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None, 81920, [IO.FileOptions]::WriteThrough)
        try {
            $sourceStream.CopyTo($destinationStream)
            $destinationStream.Flush($true)
        } finally { $destinationStream.Dispose() }
    } finally { $sourceStream.Dispose() }
    Assert-RegularFile $Destination "$Label staged copy"
    if ((Get-Sha256 $Source) -cne (Get-Sha256 $Destination) -or
        (Get-Item -LiteralPath $Source -Force).Length -ne (Get-Item -LiteralPath $Destination -Force).Length) { throw "$Label staged copy verification failed." }
}

function Invoke-OwnedFileCleanup([string]$Path, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    Assert-RegularFile $Path $Label
    [IO.File]::Delete($Path)
}

function Invoke-DeployOperationPoint([string]$Name) {
    if ([string]::Equals($env:FARMT2_DEPLOY_FAIL_AT, $Name, [StringComparison]::Ordinal)) { throw "Injected deployment failure at $Name." }
}

if ($AssemblyName -cnotmatch '^[A-Za-z_][A-Za-z0-9_.]*$') { throw 'AssemblyName is noncanonical.' }
$source = [IO.Path]::GetFullPath($SourceDll)
$game = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($GameDir))
Assert-RegularFile $source 'Plugin deployment source'
Assert-NoReparseAncestor $game 'Game directory'
if (-not (Test-Path -LiteralPath $game -PathType Container)) { throw "Game directory does not exist: $game" }
$destinationDirectory = Join-Path $game "BepInEx/plugins/$AssemblyName"
Assert-NoReparseAncestor $destinationDirectory 'Plugin deployment directory'
[IO.Directory]::CreateDirectory($destinationDirectory) | Out-Null
Assert-NoReparseAncestor $destinationDirectory 'Plugin deployment directory'
$target = Join-Path $destinationDirectory "$AssemblyName.dll"
$mutex = [Threading.Mutex]::new($false, (Get-PathLockName $target))
$mutexAcquired = $false
try {
    try { $mutexAcquired = $mutex.WaitOne() } catch [Threading.AbandonedMutexException] { $mutexAcquired = $true }
    if (-not $mutexAcquired) { throw 'Could not acquire the plugin deployment lock.' }
    Assert-NoReparseAncestor $target 'Plugin deployment target'
    $hadTarget = Test-Path -LiteralPath $target
    $oldHash = $null
    if ($hadTarget) {
        Assert-RegularFile $target 'Existing plugin DLL'
        $oldHash = Get-Sha256 $target
    }
    $attempt = [guid]::NewGuid().ToString('N')
    $staging = Join-Path $destinationDirectory ".$AssemblyName.dll.modkit-deploy-$attempt.preparing"
    $backup = Join-Path $destinationDirectory ".$AssemblyName.dll.modkit-deploy-$attempt.backup"
    $promoted = $false
    $complete = $false
    $newHash = $null
    try {
        Copy-DurableVerifiedFile $source $staging 'Plugin DLL'
        $newHash = Get-Sha256 $staging
        if ($hadTarget) { Copy-DurableVerifiedFile $target $backup 'Existing plugin DLL backup' }
        Invoke-DeployOperationPoint 'after-staging'
        Assert-NoReparseAncestor $target 'Plugin deployment target'
        if ($hadTarget) {
            Assert-RegularFile $target 'Existing plugin DLL'
            if ((Get-Sha256 $target) -cne $oldHash) { throw 'Existing plugin DLL changed before atomic replacement.' }
        } elseif (Test-Path -LiteralPath $target) { throw 'Plugin deployment target appeared before atomic replacement.' }
        [IO.File]::Move($staging, $target, $true)
        $promoted = $true
        Invoke-DeployOperationPoint 'after-promote'
        Assert-RegularFile $target 'Promoted plugin DLL'
        if ((Get-Sha256 $target) -cne $newHash) { throw 'Promoted plugin DLL verification failed.' }
        $complete = $true
        if ($hadTarget) { Invoke-OwnedFileCleanup $backup 'Plugin deployment backup' }
    } finally {
        if (-not $complete -and $promoted -and (Test-Path -LiteralPath $target)) {
            Assert-RegularFile $target 'Failed plugin deployment target'
            if ((Get-Sha256 $target) -cne $newHash) { throw 'Deployment rollback refuses to replace a target that no longer matches this attempt.' }
            if ($hadTarget -and (Test-Path -LiteralPath $backup)) {
                [IO.File]::Move($backup, $target, $true)
                Assert-RegularFile $target 'Restored plugin DLL'
                if ((Get-Sha256 $target) -cne $oldHash) { throw 'Plugin deployment rollback verification failed.' }
            } else {
                [IO.File]::Delete($target)
            }
        }
        if (Test-Path -LiteralPath $staging) { Invoke-OwnedFileCleanup $staging 'Plugin deployment staging file' }
        if (Test-Path -LiteralPath $backup) { Invoke-OwnedFileCleanup $backup 'Plugin deployment backup' }
    }
} finally {
    if ($mutexAcquired) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
