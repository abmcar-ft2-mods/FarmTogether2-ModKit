#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Source,
    [Parameter(Mandatory)][string]$Destination,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')][string]$ExpectedSha256,
    [Parameter()][ValidatePattern('^[0-9a-f]{64}$')][string[]]$AllowedCurrentSha256 = @(),
    [Parameter(Mandatory)][string]$TemporaryPath,
    [switch]$AllowMissingCurrent
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-NormalizedPath([string]$Path) {
    return [System.IO.Path]::GetFullPath($Path)
}

function Get-LowerSha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-RegularFile([string]$Path, [string]$Label) {
    $item = Get-Item -LiteralPath $Path -Force
    if (-not ($item -is [System.IO.FileInfo])) {
        throw "$Label is not a regular file: $Path"
    }
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Label must not be a reparse point: $Path"
    }
}

function Invoke-TestFailure([string]$Point) {
    if ($env:FARMT2_MODKIT_TEST_FAIL_AT -ceq $Point) {
        throw "Injected failure at $Point."
    }
}

$sourcePath = Get-NormalizedPath $Source
$destinationPath = Get-NormalizedPath $Destination
$temporary = Get-NormalizedPath $TemporaryPath

if ($sourcePath -ceq $destinationPath -or $sourcePath -ceq $temporary -or $destinationPath -ceq $temporary) {
    throw 'Source, Destination, and TemporaryPath must be distinct.'
}

if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
    throw "Source file does not exist: $sourcePath"
}
Assert-RegularFile $sourcePath 'Source'

$destinationParent = [System.IO.Path]::GetDirectoryName($destinationPath)
$temporaryParent = [System.IO.Path]::GetDirectoryName($temporary)
if ([string]::IsNullOrWhiteSpace($destinationParent) -or [string]::IsNullOrWhiteSpace($temporaryParent)) {
    throw 'Destination and TemporaryPath must have parent directories.'
}

$pathComparison = if ($IsWindows) {
    [System.StringComparison]::OrdinalIgnoreCase
} else {
    [System.StringComparison]::Ordinal
}
if (-not [string]::Equals($destinationParent, $temporaryParent, $pathComparison)) {
    throw 'TemporaryPath must be in the exact Destination parent directory.'
}
if (-not (Test-Path -LiteralPath $destinationParent -PathType Container)) {
    throw "Destination parent directory does not exist: $destinationParent"
}

$allowed = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($hash in $AllowedCurrentSha256) {
    if (-not $allowed.Add($hash)) {
        throw "AllowedCurrentSha256 contains a duplicate: $hash"
    }
}

$sourceHash = Get-LowerSha256 $sourcePath
if ($sourceHash -cne $ExpectedSha256) {
    throw "Source SHA-256 mismatch: expected=$ExpectedSha256 actual=$sourceHash"
}

$destinationExists = Test-Path -LiteralPath $destinationPath
if ($destinationExists) {
    Assert-RegularFile $destinationPath 'Destination'
    $currentHash = Get-LowerSha256 $destinationPath
    if ($currentHash -ceq $ExpectedSha256) {
        if (Test-Path -LiteralPath $temporary) {
            Assert-RegularFile $temporary 'TemporaryPath'
            Remove-Item -LiteralPath $temporary -Force
        }
        if ((Get-LowerSha256 $sourcePath) -cne $ExpectedSha256 -or
            (Get-LowerSha256 $destinationPath) -cne $ExpectedSha256) {
            throw 'Source or Destination changed during idempotent reconciliation.'
        }
        return
    }
    if (-not $allowed.Contains($currentHash)) {
        throw "Destination has an unapproved SHA-256: $currentHash"
    }
} elseif (-not $AllowMissingCurrent) {
    throw "Destination is missing and AllowMissingCurrent was not specified: $destinationPath"
}

if (Test-Path -LiteralPath $temporary) {
    Assert-RegularFile $temporary 'TemporaryPath'
    if ((Get-LowerSha256 $temporary) -cne $ExpectedSha256) {
        Remove-Item -LiteralPath $temporary -Force
    }
}

if (-not (Test-Path -LiteralPath $temporary)) {
    Invoke-TestFailure 'before-stage-copy'
    Copy-Item -LiteralPath $sourcePath -Destination $temporary
    Invoke-TestFailure 'after-stage-copy'
}

Assert-RegularFile $temporary 'TemporaryPath'
$temporaryHash = Get-LowerSha256 $temporary
if ($temporaryHash -cne $ExpectedSha256) {
    throw "TemporaryPath SHA-256 mismatch: expected=$ExpectedSha256 actual=$temporaryHash"
}
Invoke-TestFailure 'after-stage-hash'

if ((Get-LowerSha256 $sourcePath) -cne $ExpectedSha256) {
    throw 'Source changed after staging.'
}

if (Test-Path -LiteralPath $destinationPath) {
    Assert-RegularFile $destinationPath 'Destination'
    $currentHash = Get-LowerSha256 $destinationPath
    if ($currentHash -ceq $ExpectedSha256) {
        Remove-Item -LiteralPath $temporary -Force
        return
    }
    if (-not $allowed.Contains($currentHash)) {
        throw "Destination changed to an unapproved SHA-256 before promotion: $currentHash"
    }
} elseif (-not $AllowMissingCurrent) {
    throw 'Destination became missing before promotion.'
}

Invoke-TestFailure 'before-atomic-move'
[System.IO.File]::Move($temporary, $destinationPath, $true)
Invoke-TestFailure 'after-atomic-move'

Assert-RegularFile $destinationPath 'Destination'
$promotedHash = Get-LowerSha256 $destinationPath
if ($promotedHash -cne $ExpectedSha256) {
    throw "Promoted Destination SHA-256 mismatch: expected=$ExpectedSha256 actual=$promotedHash"
}
Invoke-TestFailure 'after-destination-hash'
