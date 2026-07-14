#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$SourceDll,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$SourcePdb,
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

function Invoke-DeployRollbackPoint([string]$Name) {
    if ([string]::Equals($env:FARMT2_DEPLOY_ROLLBACK_FAIL_AT, $Name, [StringComparison]::Ordinal)) { throw "Injected deployment rollback failure at $Name." }
}

if ($AssemblyName -cnotmatch '^[A-Za-z_][A-Za-z0-9_.]*$') { throw 'AssemblyName is noncanonical.' }
$sourceDll = [IO.Path]::GetFullPath($SourceDll)
$sourcePdb = [IO.Path]::GetFullPath($SourcePdb)
$expectedSourcePdb = [IO.Path]::ChangeExtension($sourceDll, '.pdb')
if (-not [string]::Equals($sourcePdb, $expectedSourcePdb, $script:PathComparison)) {
    throw 'SourcePdb must be the same-name PDB next to SourceDll.'
}
$game = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($GameDir))
Assert-RegularFile $sourceDll 'Plugin DLL deployment source'
Assert-RegularFile $sourcePdb 'Plugin PDB deployment source'
Assert-NoReparseAncestor $game 'Game directory'
if (-not (Test-Path -LiteralPath $game -PathType Container)) { throw "Game directory does not exist: $game" }
$destinationDirectory = Join-Path $game "BepInEx/plugins/$AssemblyName"
Assert-NoReparseAncestor $destinationDirectory 'Plugin deployment directory'
[IO.Directory]::CreateDirectory($destinationDirectory) | Out-Null
Assert-NoReparseAncestor $destinationDirectory 'Plugin deployment directory'
$targetDll = Join-Path $destinationDirectory "$AssemblyName.dll"
$targetPdb = Join-Path $destinationDirectory "$AssemblyName.pdb"
$mutex = [Threading.Mutex]::new($false, (Get-PathLockName $targetDll))
$mutexAcquired = $false
try {
    try { $mutexAcquired = $mutex.WaitOne() } catch [Threading.AbandonedMutexException] { $mutexAcquired = $true }
    if (-not $mutexAcquired) { throw 'Could not acquire the plugin deployment lock.' }
    $attempt = [guid]::NewGuid().ToString('N')
    $artifacts = @(
        [pscustomobject]@{
            Key = 'dll'
            Label = 'Plugin DLL'
            Source = $sourceDll
            Target = $targetDll
            Staging = Join-Path $destinationDirectory ".$AssemblyName.dll.modkit-deploy-$attempt.preparing"
            Backup = Join-Path $destinationDirectory ".$AssemblyName.dll.modkit-deploy-$attempt.backup"
            HadTarget = $false
            OldHash = $null
            NewHash = $null
            Promoted = $false
        },
        [pscustomobject]@{
            Key = 'pdb'
            Label = 'Plugin PDB'
            Source = $sourcePdb
            Target = $targetPdb
            Staging = Join-Path $destinationDirectory ".$AssemblyName.pdb.modkit-deploy-$attempt.preparing"
            Backup = Join-Path $destinationDirectory ".$AssemblyName.pdb.modkit-deploy-$attempt.backup"
            HadTarget = $false
            OldHash = $null
            NewHash = $null
            Promoted = $false
        }
    )
    foreach ($artifact in $artifacts) {
        Assert-NoReparseAncestor $artifact.Target "$($artifact.Label) deployment target"
        $artifact.HadTarget = Test-Path -LiteralPath $artifact.Target
        if ($artifact.HadTarget) {
            Assert-RegularFile $artifact.Target "Existing $($artifact.Label)"
            $artifact.OldHash = Get-Sha256 $artifact.Target
        }
        foreach ($ownedPath in @($artifact.Staging, $artifact.Backup)) {
            Assert-NoReparseAncestor $ownedPath "$($artifact.Label) transaction path"
            if (Test-Path -LiteralPath $ownedPath) { throw "$($artifact.Label) transaction path already exists: $ownedPath" }
        }
    }
    $complete = $false
    try {
        foreach ($artifact in $artifacts) {
            Copy-DurableVerifiedFile $artifact.Source $artifact.Staging $artifact.Label
            $artifact.NewHash = Get-Sha256 $artifact.Staging
            if ($artifact.HadTarget) {
                Copy-DurableVerifiedFile $artifact.Target $artifact.Backup "Existing $($artifact.Label) backup"
            }
        }
        Invoke-DeployOperationPoint 'after-staging'
        foreach ($artifact in $artifacts) {
            Assert-NoReparseAncestor $artifact.Target "$($artifact.Label) deployment target"
            if ($artifact.HadTarget) {
                Assert-RegularFile $artifact.Target "Existing $($artifact.Label)"
                if ((Get-Sha256 $artifact.Target) -cne $artifact.OldHash) {
                    throw "Existing $($artifact.Label) changed before atomic replacement."
                }
            } elseif (Test-Path -LiteralPath $artifact.Target) {
                throw "$($artifact.Label) deployment target appeared before atomic replacement."
            }
        }

        [IO.File]::Move($artifacts[0].Staging, $artifacts[0].Target, $true)
        $artifacts[0].Promoted = $true
        Invoke-DeployOperationPoint 'after-dll-promote'
        Assert-RegularFile $artifacts[0].Target 'Promoted plugin DLL'
        if ((Get-Sha256 $artifacts[0].Target) -cne $artifacts[0].NewHash) { throw 'Promoted plugin DLL verification failed.' }

        [IO.File]::Move($artifacts[1].Staging, $artifacts[1].Target, $true)
        $artifacts[1].Promoted = $true
        Invoke-DeployOperationPoint 'after-pdb-promote'
        Assert-RegularFile $artifacts[1].Target 'Promoted plugin PDB'
        if ((Get-Sha256 $artifacts[1].Target) -cne $artifacts[1].NewHash) { throw 'Promoted plugin PDB verification failed.' }
        Invoke-DeployOperationPoint 'after-promote'
        $complete = $true
        foreach ($artifact in $artifacts) {
            if ($artifact.HadTarget) { Invoke-OwnedFileCleanup $artifact.Backup "$($artifact.Label) deployment backup" }
        }
    } finally {
        $rollbackFailures = [Collections.Generic.List[string]]::new()
        if (-not $complete) {
            foreach ($artifact in @($artifacts[1], $artifacts[0])) {
                if (-not $artifact.Promoted) { continue }
                try {
                    Invoke-DeployRollbackPoint "before-$($artifact.Key)-rollback"
                    Assert-RegularFile $artifact.Target "Failed $($artifact.Label) deployment target"
                    if ((Get-Sha256 $artifact.Target) -cne $artifact.NewHash) {
                        throw "$($artifact.Label) rollback refuses to replace a target that no longer matches this attempt."
                    }
                    if ($artifact.HadTarget) {
                        if (-not (Test-Path -LiteralPath $artifact.Backup -PathType Leaf)) {
                            throw "$($artifact.Label) rollback backup is missing."
                        }
                        Assert-RegularFile $artifact.Backup "$($artifact.Label) rollback backup"
                        [IO.File]::Move($artifact.Backup, $artifact.Target, $true)
                        Assert-RegularFile $artifact.Target "Restored $($artifact.Label)"
                        if ((Get-Sha256 $artifact.Target) -cne $artifact.OldHash) {
                            throw "$($artifact.Label) rollback verification failed."
                        }
                    } else {
                        [IO.File]::Delete($artifact.Target)
                    }
                } catch {
                    $rollbackFailures.Add("$($artifact.Label): $($_.Exception.Message)")
                }
            }
        }
        $cleanupFailures = [Collections.Generic.List[string]]::new()
        if ($rollbackFailures.Count -eq 0) {
            foreach ($artifact in $artifacts) {
                foreach ($ownedFile in @(
                    [pscustomobject]@{ Path = $artifact.Staging; Label = "$($artifact.Label) deployment staging file" },
                    [pscustomobject]@{ Path = $artifact.Backup; Label = "$($artifact.Label) deployment backup" }
                )) {
                    try {
                        if (Test-Path -LiteralPath $ownedFile.Path) { Invoke-OwnedFileCleanup $ownedFile.Path $ownedFile.Label }
                    } catch {
                        $cleanupFailures.Add("$($ownedFile.Label): $($_.Exception.Message)")
                    }
                }
            }
        }
        if ($rollbackFailures.Count -ne 0) {
            $details = ($rollbackFailures | ForEach-Object { " - $_" }) -join [Environment]::NewLine
            throw "Plugin deployment rollback failed after attempting every promoted artifact:$([Environment]::NewLine)$details"
        }
        if ($cleanupFailures.Count -ne 0) {
            $details = ($cleanupFailures | ForEach-Object { " - $_" }) -join [Environment]::NewLine
            throw "Plugin deployment transaction cleanup failed after attempting every owned file:$([Environment]::NewLine)$details"
        }
    }
} finally {
    if ($mutexAcquired) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
