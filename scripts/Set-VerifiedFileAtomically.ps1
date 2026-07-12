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

function Stop-ForInjectedCrash([string]$Point) {
    if ($env:FARMT2_MODKIT_TEST_FAIL_AT -ceq $Point) {
        [System.Environment]::Exit(197)
    }
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

function Get-LowerSha256WithCrash([string]$Path, [string]$DuringPoint) {
    if ($env:FARMT2_MODKIT_TEST_FAIL_AT -ceq $DuringPoint) {
        $stream = [System.IO.File]::OpenRead($Path)
        try {
            if ($stream.Length -gt 0) {
                [void]$stream.ReadByte()
            }
            [System.Environment]::Exit(197)
        } finally {
            $stream.Dispose()
        }
    }
    return Get-LowerSha256 $Path
}

$sourcePath = Get-NormalizedPath $Source
$destinationPath = Get-NormalizedPath $Destination
$temporary = Get-NormalizedPath $TemporaryPath

$pathComparison = if ($IsWindows) {
    [System.StringComparison]::OrdinalIgnoreCase
} else {
    [System.StringComparison]::Ordinal
}
if ([string]::Equals($sourcePath, $destinationPath, $pathComparison) -or
    [string]::Equals($sourcePath, $temporary, $pathComparison) -or
    [string]::Equals($destinationPath, $temporary, $pathComparison)) {
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

if (-not [string]::Equals($destinationParent, $temporaryParent, $pathComparison)) {
    throw 'TemporaryPath must be in the exact Destination parent directory.'
}
if (-not (Test-Path -LiteralPath $destinationParent -PathType Container)) {
    throw "Destination parent directory does not exist: $destinationParent"
}

$destinationStem = [System.IO.Path]::GetFileNameWithoutExtension($destinationPath)
$temporaryName = [System.IO.Path]::GetFileName($temporary)
$temporaryPrefix = ".$destinationStem."
if (-not $temporaryName.StartsWith($temporaryPrefix, $pathComparison) -or
    -not $temporaryName.EndsWith('.tmp', $pathComparison) -or
    $temporaryName.Length -le ($temporaryPrefix.Length + '.tmp'.Length)) {
    throw "TemporaryPath is not attempt-qualified for Destination: $temporary"
}
$temporaryQualifier = $temporaryName.Substring(
    $temporaryPrefix.Length,
    $temporaryName.Length - $temporaryPrefix.Length - '.tmp'.Length)
if ($temporaryQualifier -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') {
    throw "TemporaryPath has an invalid attempt qualifier: $temporary"
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
    Stop-ForInjectedCrash 'before-stage-copy'
    if ($env:FARMT2_MODKIT_TEST_FAIL_AT -ceq 'during-stage-copy') {
        $sourceStream = [System.IO.File]::OpenRead($sourcePath)
        try {
            $temporaryStream = [System.IO.File]::Open(
                $temporary,
                [System.IO.FileMode]::CreateNew,
                [System.IO.FileAccess]::Write,
                [System.IO.FileShare]::None)
            try {
                $bytesToCopy = [Math]::Max(1L, [Math]::Floor($sourceStream.Length / 2))
                $buffer = [byte[]]::new([Math]::Min(81920L, $bytesToCopy))
                $read = $sourceStream.Read($buffer, 0, $buffer.Length)
                if ($read -gt 0) {
                    $temporaryStream.Write($buffer, 0, $read)
                    $temporaryStream.Flush($true)
                }
                [System.Environment]::Exit(197)
            } finally {
                $temporaryStream.Dispose()
            }
        } finally {
            $sourceStream.Dispose()
        }
    }
    Copy-Item -LiteralPath $sourcePath -Destination $temporary
    Stop-ForInjectedCrash 'after-stage-copy'
}

Assert-RegularFile $temporary 'TemporaryPath'
Stop-ForInjectedCrash 'before-stage-hash'
$temporaryHash = Get-LowerSha256WithCrash $temporary 'during-stage-hash'
if ($temporaryHash -cne $ExpectedSha256) {
    throw "TemporaryPath SHA-256 mismatch: expected=$ExpectedSha256 actual=$temporaryHash"
}
Stop-ForInjectedCrash 'after-stage-hash'

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

Stop-ForInjectedCrash 'before-atomic-move'
[System.IO.File]::Move($temporary, $destinationPath, $true)
Stop-ForInjectedCrash 'during-atomic-move'
Stop-ForInjectedCrash 'after-atomic-move'

Assert-RegularFile $destinationPath 'Destination'
Stop-ForInjectedCrash 'before-destination-hash'
$promotedHash = Get-LowerSha256WithCrash $destinationPath 'during-destination-hash'
if ($promotedHash -cne $ExpectedSha256) {
    throw "Promoted Destination SHA-256 mismatch: expected=$ExpectedSha256 actual=$promotedHash"
}
Stop-ForInjectedCrash 'after-destination-hash'
Stop-ForInjectedCrash 'before-caller-journal-promotion'
