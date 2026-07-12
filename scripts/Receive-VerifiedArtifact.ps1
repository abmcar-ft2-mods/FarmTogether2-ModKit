#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string]$Repository,
    [Parameter(Mandatory)][long]$ArtifactId,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ExpectedArtifactName,
    [Parameter(Mandatory)][long]$ExpectedRunId,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$ExpectedCommit,
    [Parameter(Mandatory)][ValidatePattern('^sha256:[0-9a-f]{64}$')][string]$ExpectedDigest,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ArchivePath,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$DestinationDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$pathComparison = if ($IsWindows) {
    [System.StringComparison]::OrdinalIgnoreCase
} else {
    [System.StringComparison]::Ordinal
}

function Get-NormalizedPath([string]$Path) {
    return [System.IO.Path]::GetFullPath($Path)
}

function Get-LowerSha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-SafeDirectory([string]$Path, [string]$Label) {
    $fullPath = Get-NormalizedPath $Path
    $root = [System.IO.Path]::GetPathRoot($fullPath)
    if ([string]::IsNullOrWhiteSpace($root)) {
        throw "$Label has no filesystem root: $fullPath"
    }

    $components = [System.Collections.Generic.List[string]]::new()
    [void]$components.Add($root)
    $remaining = $fullPath.Substring($root.Length)
    $separators = [char[]]@(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $current = $root
    foreach ($segment in $remaining.Split($separators, [StringSplitOptions]::RemoveEmptyEntries)) {
        $current = [System.IO.Path]::Combine($current, $segment)
        [void]$components.Add($current)
    }

    foreach ($component in $components) {
        if (-not (Test-Path -LiteralPath $component -PathType Container)) {
            throw "$Label does not exist or has a non-directory ancestor: $component"
        }
        $item = Get-Item -LiteralPath $component -Force
        if (-not ($item -is [System.IO.DirectoryInfo])) {
            throw "$Label has a non-directory ancestor: $component"
        }
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Label must not contain a reparse-point ancestor: $component"
        }
    }
}

function Assert-RegularFile([string]$Path, [string]$Label) {
    $fullPath = Get-NormalizedPath $Path
    $parent = [System.IO.Path]::GetDirectoryName($fullPath)
    if ([string]::IsNullOrWhiteSpace($parent)) {
        throw "$Label has no parent directory: $fullPath"
    }
    Assert-SafeDirectory $parent "$Label parent directory"
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "$Label does not exist or is not a file: $fullPath"
    }
    $item = Get-Item -LiteralPath $fullPath -Force
    if (-not ($item -is [System.IO.FileInfo])) {
        throw "$Label is not a regular file: $fullPath"
    }
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Label must not be a reparse point: $fullPath"
    }
}

function Test-SameOrDescendantPath([string]$Candidate, [string]$Directory) {
    if ([string]::Equals($Candidate, $Directory, $pathComparison)) {
        return $true
    }
    $prefix = $Directory.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    return $Candidate.StartsWith($prefix, $pathComparison)
}

function Get-RequiredProperty([object]$Object, [string]$Name, [string]$Label) {
    if ($null -eq $Object) {
        throw "$Label is missing."
    }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) {
        throw "$Label is missing."
    }
    return $property.Value
}

function Get-ExactInt64([object]$Value, [string]$Label) {
    $acceptedTypes = @(
        [byte], [sbyte], [int16], [uint16], [int32], [uint32], [int64]
    )
    $accepted = $false
    foreach ($type in $acceptedTypes) {
        if ($Value -is $type) {
            $accepted = $true
            break
        }
    }
    if (-not $accepted) {
        throw "$Label must be an integer."
    }
    try {
        return [Convert]::ToInt64($Value, [Globalization.CultureInfo]::InvariantCulture)
    } catch {
        throw "$Label is outside the Int64 range."
    }
}

function Stop-ForInjectedCrash {
    [System.Diagnostics.CodeAnalysis.SuppressMessageAttribute(
        'PSUseShouldProcessForStateChangingFunctions', '',
        Justification = 'Internal deterministic test hook; it is not a public cmdlet.')]
    param([string]$Point)

    if ($env:FARMT2_MODKIT_TEST_FAIL_AT -ceq $Point) {
        [System.Environment]::Exit(197)
    }
}

function Remove-DownloadTemporary {
    [System.Diagnostics.CodeAnalysis.SuppressMessageAttribute(
        'PSUseShouldProcessForStateChangingFunctions', '',
        Justification = 'Internal mandatory cleanup must not be suppressible with WhatIf.')]
    param([string]$Path)

    if ([string]::IsNullOrEmpty($Path) -or -not (Test-Path -LiteralPath $Path)) {
        return
    }
    Assert-RegularFile $Path 'Download temporary file'
    Remove-Item -LiteralPath $Path -Force
}

function Remove-SafeDirectoryTree {
    [System.Diagnostics.CodeAnalysis.SuppressMessageAttribute(
        'PSUseShouldProcessForStateChangingFunctions', '',
        Justification = 'Internal mandatory cleanup must not be suppressible with WhatIf.')]
    param([string]$Path)

    if ([string]::IsNullOrEmpty($Path) -or -not (Test-Path -LiteralPath $Path)) {
        return
    }
    Assert-SafeDirectory $Path 'Extraction temporary directory'
    Remove-SafeDirectoryContent $Path
    [System.IO.Directory]::Delete($Path, $false)
}

function Remove-SafeDirectoryContent {
    [System.Diagnostics.CodeAnalysis.SuppressMessageAttribute(
        'PSUseShouldProcessForStateChangingFunctions', '',
        Justification = 'Internal mandatory cleanup must not be suppressible with WhatIf.')]
    param([string]$Directory)

    $children = @(Get-ChildItem -LiteralPath $Directory -Force)
    foreach ($child in $children) {
        $isReparsePoint = ($child.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
        if ($child -is [System.IO.DirectoryInfo]) {
            if (-not $isReparsePoint) {
                Remove-SafeDirectoryContent $child.FullName
            }
            [System.IO.Directory]::Delete($child.FullName, $false)
        } elseif ($child -is [System.IO.FileInfo]) {
            [System.IO.File]::Delete($child.FullName)
        } elseif ($isReparsePoint) {
            throw "Extraction temporary contains an unknown reparse point: $($child.FullName)"
        } else {
            throw "Extraction temporary contains an unsupported filesystem object: $($child.FullName)"
        }
    }
}

function New-UniqueExtractionRoot {
    [System.Diagnostics.CodeAnalysis.SuppressMessageAttribute(
        'PSUseShouldProcessForStateChangingFunctions', '',
        Justification = 'Internal staging is required by the receiver transaction.')]
    param([string]$Parent, [string]$DestinationName)

    Assert-SafeDirectory $Parent 'Extraction parent directory'
    for ($attempt = 0; $attempt -lt 16; $attempt++) {
        $candidate = Join-Path $Parent ".$DestinationName.extract-$([Guid]::NewGuid().ToString('N'))"
        if (Test-Path -LiteralPath $candidate) {
            continue
        }
        [void][System.IO.Directory]::CreateDirectory($candidate)
        Assert-SafeDirectory $candidate 'Extraction root'
        return $candidate
    }
    throw 'Unable to allocate a unique extraction root.'
}

function Test-LoopbackUri([Uri]$Uri) {
    [System.Net.IPAddress]$address = $null
    if (-not [System.Net.IPAddress]::TryParse($Uri.DnsSafeHost, [ref]$address)) {
        return $false
    }
    return [System.Net.IPAddress]::IsLoopback($address)
}

function Get-TestSwitch([string]$Name) {
    $value = [System.Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrEmpty($value)) {
        return $false
    }
    if ($value -cne '1') {
        throw "$Name is a test-only switch and accepts only the exact value '1'."
    }
    return $true
}

function Wait-ForReceiverTestHook(
    [string]$Point,
    [string]$ConfiguredPoint,
    [string]$SignalPath,
    [string]$ContinuePath
) {
    if ([string]::IsNullOrEmpty($ConfiguredPoint) -or $ConfiguredPoint -cne $Point) {
        return
    }

    Assert-SafeDirectory ([System.IO.Path]::GetDirectoryName($SignalPath)) 'Receiver test signal parent'
    Assert-SafeDirectory ([System.IO.Path]::GetDirectoryName($ContinuePath)) 'Receiver test continue parent'
    if (Test-Path -LiteralPath $SignalPath) {
        throw "Receiver test signal path already exists: $SignalPath"
    }
    $signalStream = [System.IO.File]::Open(
        $SignalPath,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None)
    $signalStream.Dispose()

    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while (-not (Test-Path -LiteralPath $ContinuePath)) {
        if ([DateTime]::UtcNow -ge $deadline) {
            throw "Timed out waiting for receiver test hook: $Point"
        }
        Start-Sleep -Milliseconds 10
    }
    Assert-RegularFile $ContinuePath 'Receiver test continue file'
}

function Get-PortableZipPath([System.IO.Compression.ZipArchiveEntry]$Entry) {
    $rawName = $Entry.FullName
    if ([string]::IsNullOrEmpty($rawName)) {
        throw 'ZIP contains an empty entry name.'
    }

    $normalized = $rawName.Replace('\', '/').Normalize([Text.NormalizationForm]::FormC)
    if ($normalized.StartsWith('/', [StringComparison]::Ordinal)) {
        throw "ZIP entry is absolute or UNC-rooted: $rawName"
    }
    if ($normalized -cmatch '^[A-Za-z]:') {
        throw "ZIP entry is drive-rooted: $rawName"
    }
    if ($normalized.EndsWith('/', [StringComparison]::Ordinal)) {
        throw "ZIP directory entries are not allowed: $rawName"
    }

    $segments = $normalized.Split('/', [StringSplitOptions]::None)
    foreach ($segment in $segments) {
        if ([string]::IsNullOrEmpty($segment)) {
            throw "ZIP entry contains an empty path segment: $rawName"
        }
        if ($segment -ceq '.' -or $segment -ceq '..') {
            throw "ZIP entry contains a dot path segment: $rawName"
        }
        if ($segment.Contains(':', [StringComparison]::Ordinal)) {
            throw "ZIP entry contains an alternate-data-stream colon: $rawName"
        }
        if ($segment.EndsWith('.', [StringComparison]::Ordinal) -or
            $segment.EndsWith(' ', [StringComparison]::Ordinal)) {
            throw "ZIP entry contains a Windows-ambiguous path segment: $rawName"
        }
        if ($segment.IndexOfAny([char[]]@('<', '>', '"', '|', '?', '*')) -ge 0) {
            throw "ZIP entry contains a non-portable path character: $rawName"
        }
        foreach ($character in $segment.ToCharArray()) {
            if ([char]::IsControl($character)) {
                throw "ZIP entry contains a control character: $rawName"
            }
        }
        $baseName = $segment.Split('.', 2, [StringSplitOptions]::None)[0]
        if ($baseName -cmatch '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$') {
            throw "ZIP entry contains a reserved device name: $rawName"
        }
    }

    $attributeBytes = [BitConverter]::GetBytes([int]$Entry.ExternalAttributes)
    $externalAttributes = [BitConverter]::ToUInt32($attributeBytes, 0)
    $unixMode = ($externalAttributes -shr 16) -band 0xffff
    $unixFileType = $unixMode -band 0xf000
    if ($unixFileType -ne 0 -and $unixFileType -ne 0x8000) {
        throw "ZIP entry is not a regular file: $rawName"
    }
    if (($externalAttributes -band [uint32][System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "ZIP entry is marked as a reparse point: $rawName"
    }
    if (($externalAttributes -band [uint32][System.IO.FileAttributes]::Directory) -ne 0) {
        throw "ZIP entry is marked as a directory: $rawName"
    }

    return $normalized
}

function Get-ValidatedZipEntry([System.IO.Compression.ZipArchive]$Zip) {
    $entries = [System.Collections.Generic.List[object]]::new()
    $ordinalNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $portableNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

    foreach ($entry in $Zip.Entries) {
        $normalized = Get-PortableZipPath $entry
        if (-not $ordinalNames.Add($normalized) -or -not $portableNames.Add($normalized)) {
            throw "ZIP contains a duplicate normalized entry name: $normalized"
        }
        [void]$entries.Add([pscustomobject]@{
            Entry = $entry
            RelativePath = $normalized
        })
    }

    foreach ($item in $entries) {
        $parent = $item.RelativePath
        while ($parent.Contains('/', [StringComparison]::Ordinal)) {
            $parent = $parent.Substring(0, $parent.LastIndexOf('/', [StringComparison]::Ordinal))
            if ($portableNames.Contains($parent)) {
                throw "ZIP contains a file/directory path collision: $($item.RelativePath)"
            }
        }
    }

    return $entries
}

function Assert-TargetUnderRoot([string]$Target, [string]$Root, [string]$EntryName) {
    $rootPrefix = $Root.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $Target.StartsWith($rootPrefix, $pathComparison)) {
        throw "ZIP entry escapes the extraction root: $EntryName"
    }
}

function Expand-ValidatedZip(
    [System.Collections.IEnumerable]$Entries,
    [string]$ExtractionRoot
) {
    foreach ($item in $Entries) {
        $relativePlatformPath = $item.RelativePath.Replace(
            '/', [System.IO.Path]::DirectorySeparatorChar)
        $target = [System.IO.Path]::GetFullPath(
            [System.IO.Path]::Combine($ExtractionRoot, $relativePlatformPath))
        Assert-TargetUnderRoot $target $ExtractionRoot $item.RelativePath

        $targetParent = [System.IO.Path]::GetDirectoryName($target)
        if ([string]::IsNullOrWhiteSpace($targetParent)) {
            throw "ZIP entry has no target parent: $($item.RelativePath)"
        }
        [void][System.IO.Directory]::CreateDirectory($targetParent)
        Assert-SafeDirectory $targetParent "ZIP entry target parent for $($item.RelativePath)"

        $entryStream = $item.Entry.Open()
        try {
            $output = [System.IO.File]::Open(
                $target,
                [System.IO.FileMode]::CreateNew,
                [System.IO.FileAccess]::Write,
                [System.IO.FileShare]::None)
            try {
                $entryStream.CopyTo($output)
                $output.Flush($true)
            } finally {
                $output.Dispose()
            }
        } finally {
            $entryStream.Dispose()
        }
    }
}

function Get-TreeManifest([string]$Root, [string]$Label) {
    Assert-SafeDirectory $Root $Label
    $manifest = [System.Collections.Generic.SortedDictionary[string, string]]::new(
        [StringComparer]::Ordinal)
    $items = @(Get-ChildItem -LiteralPath $Root -Force -Recurse)
    foreach ($item in $items) {
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Label contains a reparse point: $($item.FullName)"
        }
        $relative = [System.IO.Path]::GetRelativePath($Root, $item.FullName).Replace('\', '/')
        if ($item -is [System.IO.DirectoryInfo]) {
            $key = "D:$relative"
            $value = '-'
        } elseif ($item -is [System.IO.FileInfo]) {
            $key = "F:$relative"
            $value = Get-LowerSha256 $item.FullName
        } else {
            throw "$Label contains an unsupported file-system object: $($item.FullName)"
        }
        if ($manifest.ContainsKey($key)) {
            throw "$Label contains a duplicate relative path: $relative"
        }
        $manifest.Add($key, $value)
    }
    return ,$manifest
}

function Assert-TreeManifestEqual(
    [System.Collections.Generic.SortedDictionary[string, string]]$Expected,
    [System.Collections.Generic.SortedDictionary[string, string]]$Actual,
    [string]$Label = 'Destination tree'
) {
    if ($Expected.Count -ne $Actual.Count) {
        throw "$Label entry count mismatch: expected=$($Expected.Count) actual=$($Actual.Count)"
    }
    foreach ($entry in $Expected.GetEnumerator()) {
        if (-not $Actual.ContainsKey($entry.Key)) {
            throw "$Label is missing entry: $($entry.Key.Substring(2))"
        }
        if ($Actual[$entry.Key] -cne $entry.Value) {
            throw "$Label hash mismatch: $($entry.Key.Substring(2))"
        }
    }
}

if ($ArtifactId -le 0) {
    throw 'ArtifactId must be positive.'
}
if ($ExpectedRunId -le 0) {
    throw 'ExpectedRunId must be positive.'
}

$archive = Get-NormalizedPath $ArchivePath
$destination = Get-NormalizedPath $DestinationDirectory
$archiveParent = [System.IO.Path]::GetDirectoryName($archive)
$destinationParent = [System.IO.Path]::GetDirectoryName($destination)
if ([string]::IsNullOrWhiteSpace($archiveParent) -or
    [string]::IsNullOrWhiteSpace($destinationParent)) {
    throw 'ArchivePath and DestinationDirectory must have parent directories.'
}
Assert-SafeDirectory $archiveParent 'Archive parent directory'
Assert-SafeDirectory $destinationParent 'Destination parent directory'
if (Test-SameOrDescendantPath $archive $destination) {
    throw 'ArchivePath must not be inside DestinationDirectory.'
}

$artifactIdText = $ArtifactId.ToString([Globalization.CultureInfo]::InvariantCulture)
$apiPath = "repos/$Repository/actions/artifacts/$artifactIdText"
$apiOutput = @(& gh api --method GET `
    --header 'Accept: application/vnd.github+json' `
    --header 'X-GitHub-Api-Version: 2026-03-10' `
    $apiPath)
if ($LASTEXITCODE -ne 0) {
    throw "gh api failed for artifact $ArtifactId."
}
if ($apiOutput.Count -eq 0) {
    throw "gh api returned no artifact metadata for $ArtifactId."
}
try {
    $metadata = ($apiOutput -join [Environment]::NewLine) | ConvertFrom-Json -Depth 20
} catch {
    throw "gh api returned invalid artifact metadata for ${ArtifactId}: $($_.Exception.Message)"
}

$metadataId = Get-ExactInt64 (Get-RequiredProperty $metadata 'id' 'Artifact id') 'Artifact id'
if ($metadataId -ne $ArtifactId) {
    throw "Artifact id mismatch: expected=$ArtifactId actual=$metadataId"
}
$metadataName = Get-RequiredProperty $metadata 'name' 'Artifact name'
if (-not ($metadataName -is [string]) -or $metadataName -cne $ExpectedArtifactName) {
    throw "Artifact name mismatch: expected=$ExpectedArtifactName actual=$metadataName"
}
$metadataExpired = Get-RequiredProperty $metadata 'expired' 'Artifact expired state'
if (-not ($metadataExpired -is [bool])) {
    throw 'Artifact expired state must be a boolean.'
}
if ($metadataExpired) {
    throw "Artifact $ArtifactId has expired."
}
$metadataDigest = Get-RequiredProperty $metadata 'digest' 'Artifact digest'
if (-not ($metadataDigest -is [string]) -or $metadataDigest -cne $ExpectedDigest) {
    throw "Artifact digest mismatch: expected=$ExpectedDigest actual=$metadataDigest"
}
$workflowRun = Get-RequiredProperty $metadata 'workflow_run' 'Artifact workflow_run'
$metadataRunId = Get-ExactInt64 (Get-RequiredProperty $workflowRun 'id' 'Artifact workflow run id') `
    'Artifact workflow run id'
if ($metadataRunId -ne $ExpectedRunId) {
    throw "Artifact workflow run mismatch: expected=$ExpectedRunId actual=$metadataRunId"
}
$metadataCommit = Get-RequiredProperty $workflowRun 'head_sha' 'Artifact workflow commit'
if (-not ($metadataCommit -is [string]) -or $metadataCommit -cne $ExpectedCommit) {
    throw "Artifact workflow commit mismatch: expected=$ExpectedCommit actual=$metadataCommit"
}
$archiveUrl = Get-RequiredProperty $metadata 'archive_download_url' 'Artifact archive_download_url'
[Uri]$parsedArchiveUri = $null
if (-not ($archiveUrl -is [string]) -or
    -not [Uri]::TryCreate($archiveUrl, [UriKind]::Absolute, [ref]$parsedArchiveUri) -or
    ($parsedArchiveUri.Scheme -cne 'https' -and $parsedArchiveUri.Scheme -cne 'http') -or
    -not [string]::IsNullOrEmpty($parsedArchiveUri.UserInfo)) {
    throw 'Artifact archive_download_url must be an absolute HTTP(S) URL without user information.'
}
$allowTestLoopbackHttp = Get-TestSwitch 'FARMT2_MODKIT_TEST_ALLOW_LOOPBACK_HTTP'
$skipTestLoopbackCertificateCheck = Get-TestSwitch `
    'FARMT2_MODKIT_TEST_SKIP_LOOPBACK_CERTIFICATE_CHECK'
$archiveUriIsLoopback = Test-LoopbackUri $parsedArchiveUri
$receiverTestSeamEnabled = $allowTestLoopbackHttp -and $archiveUriIsLoopback -and
    $parsedArchiveUri.Scheme -ceq 'http'
if ($parsedArchiveUri.Scheme -ceq 'http' -and
    (-not $allowTestLoopbackHttp -or -not $archiveUriIsLoopback)) {
    throw 'Artifact archive_download_url must use HTTPS; plain HTTP is allowed only by the loopback test seam.'
}
if ($skipTestLoopbackCertificateCheck -and
    ($parsedArchiveUri.Scheme -cne 'https' -or -not $archiveUriIsLoopback)) {
    throw 'The certificate-check test seam is restricted to a loopback HTTPS archive_download_url.'
}

$testCrashPoint = $env:FARMT2_MODKIT_TEST_FAIL_AT
if (-not [string]::IsNullOrEmpty($testCrashPoint)) {
    if (-not $receiverTestSeamEnabled) {
        throw 'Receiver crash injection is restricted to the explicit loopback HTTP test seam.'
    }
    if ($testCrashPoint -cne 'after-archive-verify') {
        throw "Unsupported receiver crash-injection point: $testCrashPoint"
    }
}

$testHookPoint = $env:FARMT2_MODKIT_TEST_HOOK_POINT
$testHookSignal = $env:FARMT2_MODKIT_TEST_HOOK_SIGNAL
$testHookContinue = $env:FARMT2_MODKIT_TEST_HOOK_CONTINUE
$testHookValues = @($testHookPoint, $testHookSignal, $testHookContinue)
$testHookConfiguredCount = @($testHookValues | Where-Object { -not [string]::IsNullOrEmpty($_) }).Count
if ($testHookConfiguredCount -gt 0) {
    if ($testHookConfiguredCount -ne $testHookValues.Count) {
        throw 'Receiver test hook requires point, signal, and continue values together.'
    }
    if (-not $receiverTestSeamEnabled) {
        throw 'Receiver test hooks are restricted to the explicit loopback HTTP test seam.'
    }
    if ($testHookPoint -cne 'after-fresh-manifest' -and
        $testHookPoint -cne 'after-existing-manifest') {
        throw "Unsupported receiver test hook point: $testHookPoint"
    }
    $testHookSignal = Get-NormalizedPath $testHookSignal
    $testHookContinue = Get-NormalizedPath $testHookContinue
    $testHookSignalParent = [System.IO.Path]::GetDirectoryName($testHookSignal)
    $testHookContinueParent = [System.IO.Path]::GetDirectoryName($testHookContinue)
    if (-not [string]::Equals($testHookSignalParent, $archiveParent, $pathComparison) -or
        -not [string]::Equals($testHookContinueParent, $archiveParent, $pathComparison)) {
        throw 'Receiver test hook files must be direct children of ArchivePath parent.'
    }
    if ([string]::Equals($testHookSignal, $testHookContinue, $pathComparison) -or
        [string]::Equals($testHookSignal, $archive, $pathComparison) -or
        [string]::Equals($testHookContinue, $archive, $pathComparison)) {
        throw 'Receiver test hook files and ArchivePath must be distinct.'
    }
} else {
    $testHookPoint = $null
    $testHookSignal = $null
    $testHookContinue = $null
}

$expectedArchiveHash = $ExpectedDigest.Substring('sha256:'.Length)
$downloadTemporary = $null
try {
    if (Test-Path -LiteralPath $archive) {
        Assert-RegularFile $archive 'ArchivePath'
        $existingHash = Get-LowerSha256 $archive
        if ($existingHash -cne $expectedArchiveHash) {
            throw "ArchivePath SHA-256 mismatch: expected=$expectedArchiveHash actual=$existingHash"
        }
    } else {
        $tokenOutput = @(& gh auth token)
        if ($LASTEXITCODE -ne 0) {
            throw 'gh auth token failed.'
        }
        $tokens = @($tokenOutput | ForEach-Object { $_.ToString().Trim() } |
            Where-Object { $_.Length -gt 0 })
        if ($tokens.Count -ne 1 -or $tokens[0] -notmatch '^\S+$') {
            throw 'gh auth token did not return exactly one non-empty token.'
        }

        $archiveName = [System.IO.Path]::GetFileName($archive)
        $downloadTemporary = Join-Path $archiveParent `
            ".$archiveName.download-$([Guid]::NewGuid().ToString('N')).tmp"
        if (Test-Path -LiteralPath $downloadTemporary) {
            throw "Download temporary path already exists: $downloadTemporary"
        }

        Assert-SafeDirectory $archiveParent 'Archive parent directory before download'
        $webRequestParameters = @{
            Uri = $archiveUrl
            Headers = @{
                Accept = 'application/vnd.github+json'
                Authorization = "Bearer $($tokens[0])"
            }
            MaximumRedirection = 10
            OutFile = $downloadTemporary
        }
        if ($skipTestLoopbackCertificateCheck) {
            $webRequestParameters.SkipCertificateCheck = $true
        }
        Invoke-WebRequest @webRequestParameters
        Assert-RegularFile $downloadTemporary 'Downloaded archive'
        $downloadedHash = Get-LowerSha256 $downloadTemporary
        if ($downloadedHash -cne $expectedArchiveHash) {
            throw "Downloaded archive SHA-256 mismatch: expected=$expectedArchiveHash actual=$downloadedHash"
        }

        try {
            Assert-RegularFile $downloadTemporary 'Downloaded archive before cache promotion'
            Assert-SafeDirectory $archiveParent 'Archive parent directory before cache promotion'
            [System.IO.File]::Move($downloadTemporary, $archive)
            $downloadTemporary = $null
        } catch [System.IO.IOException] {
            if (-not (Test-Path -LiteralPath $archive)) {
                throw
            }
            Assert-RegularFile $archive 'Concurrent ArchivePath'
            $concurrentHash = Get-LowerSha256 $archive
            if ($concurrentHash -cne $expectedArchiveHash) {
                throw "Concurrent ArchivePath SHA-256 mismatch: expected=$expectedArchiveHash actual=$concurrentHash"
            }
        }
    }

    Assert-RegularFile $archive 'ArchivePath'
    $archiveHash = Get-LowerSha256 $archive
    if ($archiveHash -cne $expectedArchiveHash) {
        throw "ArchivePath changed after verification: expected=$expectedArchiveHash actual=$archiveHash"
    }
} finally {
    Remove-DownloadTemporary $downloadTemporary
}

Stop-ForInjectedCrash 'after-archive-verify'

$extractionRoot = $null
$archiveStream = $null
$zip = $null
try {
    Assert-RegularFile $archive 'ArchivePath'
    $archiveStream = [System.IO.File]::Open(
        $archive,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read)
    $hasher = [System.Security.Cryptography.SHA256]::Create()
    try {
        $openedHashBytes = $hasher.ComputeHash($archiveStream)
        $openedHash = [BitConverter]::ToString($openedHashBytes).Replace('-', '').ToLowerInvariant()
    } finally {
        $hasher.Dispose()
    }
    if ($openedHash -cne $expectedArchiveHash) {
        throw "ArchivePath changed before extraction: expected=$expectedArchiveHash actual=$openedHash"
    }
    $archiveStream.Position = 0
    $zip = [System.IO.Compression.ZipArchive]::new(
        $archiveStream,
        [System.IO.Compression.ZipArchiveMode]::Read,
        $true)
    $validatedEntries = @(Get-ValidatedZipEntry $zip)

    $destinationName = [System.IO.Path]::GetFileName($destination)
    if ([string]::IsNullOrWhiteSpace($destinationName)) {
        throw 'DestinationDirectory must not be a filesystem root.'
    }
    $extractionRoot = New-UniqueExtractionRoot $destinationParent $destinationName
    Expand-ValidatedZip $validatedEntries $extractionRoot
    $freshManifest = Get-TreeManifest $extractionRoot 'Fresh extraction'

    # Close every archive handle before the only operation that can create the
    # live destination. No fallible archive cleanup remains after promotion.
    $zip.Dispose()
    $zip = $null
    $archiveStream.Dispose()
    $archiveStream = $null

    Wait-ForReceiverTestHook `
        'after-fresh-manifest' $testHookPoint $testHookSignal $testHookContinue
    Assert-SafeDirectory $destinationParent 'Destination parent directory before promotion'
    $freshManifestAfterHook = Get-TreeManifest `
        $extractionRoot 'Fresh extraction after validation'
    Assert-TreeManifestEqual `
        $freshManifest $freshManifestAfterHook 'Fresh extraction changed after validation'
    if (Test-Path -LiteralPath $destination) {
        $liveManifest = Get-TreeManifest $destination 'DestinationDirectory'
        Assert-TreeManifestEqual $freshManifest $liveManifest 'DestinationDirectory'
        Wait-ForReceiverTestHook `
            'after-existing-manifest' $testHookPoint $testHookSignal $testHookContinue
        $freshManifestBeforeReconciliation = Get-TreeManifest `
            $extractionRoot 'Fresh extraction before destination reconciliation'
        Assert-TreeManifestEqual `
            $freshManifest $freshManifestBeforeReconciliation `
            'Fresh extraction changed before destination reconciliation'
        $liveManifestAfterHook = Get-TreeManifest `
            $destination 'DestinationDirectory after validation'
        Assert-TreeManifestEqual `
            $freshManifest $liveManifestAfterHook 'DestinationDirectory changed during validation'
    } else {
        if ($testHookPoint -ceq 'after-existing-manifest') {
            throw 'The after-existing-manifest test hook requires an existing DestinationDirectory.'
        }
        try {
            [System.IO.Directory]::Move($extractionRoot, $destination)
            $extractionRoot = $null
        } catch [System.IO.IOException] {
            if (-not (Test-Path -LiteralPath $destination)) {
                throw
            }
            $liveManifest = Get-TreeManifest $destination 'Concurrent DestinationDirectory'
            Assert-TreeManifestEqual $freshManifest $liveManifest 'Concurrent DestinationDirectory'
        }
    }
} finally {
    if ($null -ne $zip) {
        $zip.Dispose()
    }
    if ($null -ne $archiveStream) {
        $archiveStream.Dispose()
    }
    Remove-SafeDirectoryTree $extractionRoot
}
