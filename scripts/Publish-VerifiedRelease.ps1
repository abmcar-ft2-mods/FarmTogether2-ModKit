#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[^/]+/[^/]+$')][string]$Repository,
    [Parameter(Mandatory)][ValidatePattern('^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')][string]$ReleaseTag,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$ExpectedTagObject,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$ExpectedCommit,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$CandidateDirectory,
    [Parameter(Mandatory)][ValidateSet('Mod', 'Reference')][string]$CandidateKind,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$WorkingDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

function Invoke-GhJson {
    param(
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$Label
    )

    $output = @(& gh @Arguments)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) { throw "$Label failed with exit code $exitCode." }
    try {
        return (($output -join "`n") | ConvertFrom-Json)
    } catch {
        throw "$Label returned invalid JSON: $($_.Exception.Message)"
    }
}

function Get-ReleaseByTagIncludingDrafts {
    $endpoint = "repos/$Repository/releases?per_page=100"
    $output = @(& gh api --method GET --paginate --slurp $endpoint)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) { throw "Release list failed with exit code $exitCode." }
    if ($output.Count -eq 0) { throw 'Release list returned no JSON.' }

    try {
        $document = [Text.Json.JsonDocument]::Parse(($output -join "`n"))
    } catch {
        throw "Release list returned invalid JSON: $($_.Exception.Message)"
    }

    try {
        if ($document.RootElement.ValueKind -ne [Text.Json.JsonValueKind]::Array) {
            throw 'Release list pagination root must be an array.'
        }
        if ($document.RootElement.GetArrayLength() -eq 0) {
            throw 'Release list pagination returned no pages.'
        }

        $matches = [Collections.Generic.List[object]]::new()
        foreach ($page in $document.RootElement.EnumerateArray()) {
            if ($page.ValueKind -ne [Text.Json.JsonValueKind]::Array) {
                throw 'Release list pagination page must be an array.'
            }
            foreach ($release in $page.EnumerateArray()) {
                if ($release.ValueKind -ne [Text.Json.JsonValueKind]::Object) {
                    throw 'Release list item must be an object.'
                }
                $tagProperties = @($release.EnumerateObject() | Where-Object {
                    [string]::Equals($_.Name, 'tag_name', [StringComparison]::OrdinalIgnoreCase)
                })
                if ($tagProperties.Count -ne 1 -or
                    $tagProperties[0].Name -cne 'tag_name' -or
                    $tagProperties[0].Value.ValueKind -ne [Text.Json.JsonValueKind]::String) {
                    throw 'Release list item has an invalid tag_name.'
                }
                if ($tagProperties[0].Value.GetString() -ceq $ReleaseTag) {
                    $matches.Add(($release.GetRawText() | ConvertFrom-Json))
                }
            }
        }
    } finally {
        $document.Dispose()
    }

    if ($matches.Count -gt 1) {
        throw "Multiple Releases match the exact tag $ReleaseTag."
    }
    return $matches.Count -eq 1 ? $matches[0] : $null
}

function Invoke-GitOneLine {
    param(
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$Label
    )

    $output = @(& git @Arguments)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) { throw "$Label failed with exit code $exitCode." }
    if ($output.Count -ne 1) { throw "$Label did not return exactly one line." }
    return $output[0].Trim()
}

function Assert-TagIdentity {
    $tagRef = "refs/tags/$ReleaseTag"
    $tagType = Invoke-GitOneLine @('cat-file', '-t', $tagRef) 'Local release tag type query'
    if ($tagType -cne 'tag') { throw 'Local release tag is not annotated.' }
    $tagObject = Invoke-GitOneLine @('rev-parse', '--verify', $tagRef) 'Local release tag object query'
    if ($tagObject -cne $ExpectedTagObject) { throw 'Local release tag object differs from verified evidence.' }
    $commit = Invoke-GitOneLine @('rev-parse', '--verify', "$tagRef^{}") 'Local release tag commit query'
    if ($commit -cne $ExpectedCommit) { throw 'Local release tag commit differs from verified evidence.' }
    $head = Invoke-GitOneLine @('rev-parse', '--verify', 'HEAD') 'Local checkout commit query'
    if ($head -cne $ExpectedCommit) { throw 'Local checkout differs from the verified release commit.' }

    $remoteRef = Invoke-GhJson @('api', '--method', 'GET', "repos/$Repository/git/ref/tags/$ReleaseTag") 'Remote release tag ref query'
    if ([string]$remoteRef.ref -cne $tagRef -or [string]$remoteRef.object.type -cne 'tag' -or
        [string]$remoteRef.object.sha -cne $ExpectedTagObject) {
        throw 'Remote annotated tag object differs from verified evidence.'
    }
    $remoteTag = Invoke-GhJson @('api', '--method', 'GET', "repos/$Repository/git/tags/$ExpectedTagObject") 'Remote annotated tag object query'
    if ([string]$remoteTag.sha -cne $ExpectedTagObject -or [string]$remoteTag.object.type -cne 'commit' -or
        [string]$remoteTag.object.sha -cne $ExpectedCommit) {
        throw 'Remote annotated tag commit differs from verified evidence.'
    }
}

function Get-CandidateAssets {
    $candidatePath = Join-Path $script:CandidateRoot 'candidate.json'
    if (-not (Test-Path -LiteralPath $candidatePath -PathType Leaf)) { throw 'Candidate metadata is missing.' }
    $candidate = Get-Content -LiteralPath $candidatePath -Raw | ConvertFrom-Json
    if ([string]$candidate.candidateKind -cne $CandidateKind) { throw 'Candidate kind differs from the requested publication kind.' }
    if ([string]$candidate.commit -cne $ExpectedCommit) { throw 'Candidate commit differs from verified evidence.' }

    $schema = if ($CandidateKind -ceq 'Mod') {
        @(
            @('schemaVersion', $null), @('candidateKind', $null), @('artifactName', $null), @('assemblyName', $null),
            @('version', $null), @('commit', $null), @('runId', $null), @('playerAsset', 'playerSha256'),
            @('playerSha256', $null), @('symbolsAsset', 'symbolsSha256'), @('symbolsSha256', $null),
            @('checksumsAsset', 'checksumsSha256'), @('checksumsSha256', $null)
        )
    } else {
        @(
            @('schemaVersion', $null), @('candidateKind', $null), @('artifactName', $null), @('packageId', $null),
            @('packageVersion', $null), @('commit', $null), @('runId', $null), @('packageAsset', 'packageSha256'),
            @('packageSha256', $null), @('checksumsAsset', 'checksumsSha256'), @('checksumsSha256', $null)
        )
    }
    $expectedProperties = @($schema | ForEach-Object { [string]$_[0] })
    $actualProperties = @($candidate.PSObject.Properties | ForEach-Object Name)
    if ($actualProperties.Count -ne $expectedProperties.Count -or
        @(Compare-Object $expectedProperties $actualProperties -CaseSensitive).Count -ne 0) {
        throw 'Candidate metadata does not match its closed publication schema.'
    }

    $assets = [Collections.Generic.List[object]]::new()
    foreach ($entry in @($schema | Where-Object { $null -ne $_[1] })) {
        $name = [string]$candidate.($entry[0])
        $digest = [string]$candidate.($entry[1])
        if ($name -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$' -or [IO.Path]::GetFileName($name) -cne $name) {
            throw "Candidate asset name is invalid: $name"
        }
        if ($digest -cnotmatch '^[0-9a-f]{64}$') { throw "Candidate asset SHA-256 is invalid: $name" }
        $path = Join-Path $script:CandidateRoot $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Candidate asset is missing: $name" }
        $actualDigest = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualDigest -cne $digest) { throw "Candidate asset SHA-256 differs from metadata: $name" }
        $assets.Add([pscustomobject]@{
            Name = $name
            Path = [IO.Path]::GetFullPath($path)
            Digest = "sha256:$digest"
            Size = [long](Get-Item -LiteralPath $path).Length
        })
    }
    $names = @($assets | ForEach-Object Name)
    if ($names.Count -ne ($CandidateKind -ceq 'Mod' ? 3 : 2) -or
        @($names | Sort-Object -Unique).Count -ne $names.Count) {
        throw 'Candidate release asset set is invalid.'
    }
    $expectedFiles = @('candidate.json') + $names
    $actualFiles = @(Get-ChildItem -LiteralPath $script:CandidateRoot -File -Force | ForEach-Object Name)
    $unexpectedDirectories = @(Get-ChildItem -LiteralPath $script:CandidateRoot -Directory -Force)
    if ($actualFiles.Count -ne $expectedFiles.Count -or $unexpectedDirectories.Count -ne 0 -or
        @(Compare-Object $expectedFiles $actualFiles -CaseSensitive).Count -ne 0) {
        throw 'Candidate directory does not contain exactly its frozen publication files.'
    }
    return $assets.ToArray()
}

function Assert-ReleaseBaseState {
    param(
        [Parameter(Mandatory)][object]$Release,
        [Parameter(Mandatory)][bool]$ExpectedDraft,
        [Parameter(Mandatory)][bool]$ExpectedImmutable,
        [Parameter(Mandatory)][string]$Label
    )

    if ([long]$Release.id -le 0 -or [string]$Release.tag_name -cne $ReleaseTag -or
        -not ($Release.draft -is [bool]) -or [bool]$Release.draft -ne $ExpectedDraft -or
        -not ($Release.prerelease -is [bool]) -or [bool]$Release.prerelease -or
        -not ($Release.immutable -is [bool]) -or [bool]$Release.immutable -ne $ExpectedImmutable) {
        throw "$Label identity, state, or immutability is invalid."
    }
}

function Assert-ReleaseAssetSet {
    param(
        [Parameter(Mandatory)][object]$Release,
        [Parameter(Mandatory)][bool]$AllowMissing,
        [Parameter(Mandatory)][string]$Label
    )

    $remoteAssets = @($Release.assets)
    $remoteNames = @($remoteAssets | ForEach-Object { [string]$_.name })
    if (@($remoteNames | Sort-Object -Unique).Count -ne $remoteNames.Count -or
        @($remoteNames | Where-Object { $_ -cnotin $script:AssetNames }).Count -ne 0 -or
        (-not $AllowMissing -and ($remoteNames.Count -ne $script:AssetNames.Count -or
            @(Compare-Object $script:AssetNames $remoteNames -CaseSensitive).Count -ne 0))) {
        throw "$Label contains missing, duplicate, or unexpected assets."
    }
    foreach ($remoteAsset in $remoteAssets) {
        $expected = @($script:CandidateAssets | Where-Object Name -CEQ ([string]$remoteAsset.name))
        if ($expected.Count -ne 1 -or [string]::IsNullOrWhiteSpace([string]$remoteAsset.url) -or
            [string]$remoteAsset.state -cne 'uploaded' -or [long]$remoteAsset.size -ne [long]$expected[0].Size -or
            [string]$remoteAsset.digest -cne [string]$expected[0].Digest) {
            throw "$Label asset metadata differs from the frozen candidate: $([string]$remoteAsset.name)"
        }
    }
}

function Get-VerifiedReleaseSnapshot {
    param(
        [Parameter(Mandatory)][long]$ReleaseId,
        [Parameter(Mandatory)][bool]$ExpectedDraft,
        [Parameter(Mandatory)][bool]$ExpectedImmutable,
        [Parameter(Mandatory)][string]$Label
    )

    $release = Invoke-GhJson @('api', '--method', 'GET', "repos/$Repository/releases/$ReleaseId") "$Label query"
    if ([long]$release.id -ne $ReleaseId) { throw "$Label query returned a different numeric Release ID." }
    Assert-ReleaseBaseState $release $ExpectedDraft $ExpectedImmutable $Label
    Assert-ReleaseAssetSet $release $false $Label
    $metadata = @($release.assets | Sort-Object name | ForEach-Object {
        "$([string]$_.url)`t$([string]$_.name)`t$([string]$_.state)`t$([long]$_.size)`t$([string]$_.digest)"
    })
    return [pscustomobject]@{
        ReleaseId = [long]$release.id
        AssetMetadata = [string[]]$metadata
    }
}

function Assert-ReleaseSnapshotEqual {
    param(
        [Parameter(Mandatory)][object]$Expected,
        [Parameter(Mandatory)][object]$Actual,
        [Parameter(Mandatory)][string]$Message
    )

    if ([long]$Actual.ReleaseId -ne [long]$Expected.ReleaseId -or
        @(Compare-Object @($Expected.AssetMetadata) @($Actual.AssetMetadata) -CaseSensitive).Count -ne 0) {
        throw $Message
    }
}

function New-FreshDirectory {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Label)
    if (Test-Path -LiteralPath $Path) { throw "$Label already exists: $Path" }
    New-Item -ItemType Directory -Path $Path | Out-Null
}

function Receive-And-VerifyReleaseAssets {
    param([Parameter(Mandatory)][string]$Destination, [Parameter(Mandatory)][string]$Label)

    New-FreshDirectory $Destination $Label
    foreach ($asset in $script:CandidateAssets) {
        & gh release download $ReleaseTag --repo $Repository --pattern $asset.Name --dir $Destination
        $downloadExit = $LASTEXITCODE
        if ($downloadExit -ne 0) { throw "$Label download failed for $($asset.Name) with exit code $downloadExit." }
    }
    & (Join-Path $PSScriptRoot 'Test-PublishedAssets.ps1') `
        -PublishedDirectory $Destination `
        -CandidateDirectory $script:CandidateRoot `
        -CandidateKind $CandidateKind
}

$script:CandidateRoot = [IO.Path]::GetFullPath($CandidateDirectory)
if (-not (Test-Path -LiteralPath $script:CandidateRoot -PathType Container)) { throw 'Candidate directory does not exist.' }
$script:WorkingRoot = [IO.Path]::GetFullPath($WorkingDirectory)
New-FreshDirectory $script:WorkingRoot 'Release publication working directory'
$script:CandidateAssets = @(Get-CandidateAssets)
$script:AssetNames = @($script:CandidateAssets | ForEach-Object Name)

Assert-TagIdentity
$release = Get-ReleaseByTagIncludingDrafts
if ($null -eq $release) {
    Assert-TagIdentity
    $assetPaths = @($script:CandidateAssets | ForEach-Object Path)
    & gh release create $ReleaseTag --repo $Repository --draft --verify-tag --generate-notes --title $ReleaseTag @assetPaths
    $createExit = $LASTEXITCODE
    if ($createExit -ne 0) { throw "Draft Release creation failed with exit code $createExit." }
    $release = Get-ReleaseByTagIncludingDrafts
    if ($null -eq $release) { throw 'Created draft Release was not returned by the authenticated Release list.' }
}

$releaseId = [long]$release.id
$wasDraft = $release.draft -is [bool] -and [bool]$release.draft
Assert-ReleaseBaseState $release $wasDraft (-not $wasDraft) 'Existing Release'
Assert-ReleaseAssetSet $release $wasDraft 'Existing Release'

if ($wasDraft) {
    $remoteNames = @($release.assets | ForEach-Object { [string]$_.name })
    foreach ($asset in @($script:CandidateAssets | Where-Object { $_.Name -cnotin $remoteNames })) {
        Assert-TagIdentity
        & gh release upload $ReleaseTag --repo $Repository $asset.Path
        $uploadExit = $LASTEXITCODE
        if ($uploadExit -ne 0) { throw "Draft Release asset upload failed for $($asset.Name) with exit code $uploadExit." }
    }
}

$beforeDraftDownload = Get-VerifiedReleaseSnapshot $releaseId $wasDraft (-not $wasDraft) 'Release before byte verification'
$draftDirectory = Join-Path $script:WorkingRoot 'before-publication'
Receive-And-VerifyReleaseAssets $draftDirectory 'Release before publication'
$beforePublication = Get-VerifiedReleaseSnapshot $releaseId $wasDraft (-not $wasDraft) 'Release after prepublication byte verification'
Assert-ReleaseSnapshotEqual $beforeDraftDownload $beforePublication 'Release changed during prepublication byte verification.'

if ($wasDraft) {
    Assert-TagIdentity
    $immediateDraft = Get-VerifiedReleaseSnapshot $releaseId $true $false 'Release immediately before publication'
    Assert-ReleaseSnapshotEqual $beforePublication $immediateDraft 'Release changed immediately before publication.'
    $patchResponse = Invoke-GhJson @('api', '--method', 'PATCH', "repos/$Repository/releases/$releaseId", '-F', 'draft=false') 'Numeric Release publication'
    if ([long]$patchResponse.id -ne $releaseId) { throw 'Numeric Release publication returned a different Release ID.' }
    Assert-ReleaseBaseState $patchResponse $false $true 'Published Release response'
    Assert-ReleaseAssetSet $patchResponse $false 'Published Release response'
}

$publishedSnapshot = Get-VerifiedReleaseSnapshot $releaseId $false $true 'Published Release'
Assert-ReleaseSnapshotEqual $beforePublication $publishedSnapshot 'Release identity or assets changed during publication.'
$publishedDirectory = Join-Path $script:WorkingRoot 'published'
Receive-And-VerifyReleaseAssets $publishedDirectory 'Published Release'
$publishedAfterDownload = Get-VerifiedReleaseSnapshot $releaseId $false $true 'Published Release after byte verification'
Assert-ReleaseSnapshotEqual $publishedSnapshot $publishedAfterDownload 'Published Release changed during final byte verification.'
Assert-TagIdentity

& gh release verify $ReleaseTag --repo $Repository
$releaseVerifyExit = $LASTEXITCODE
if ($releaseVerifyExit -ne 0) { throw "Immutable Release attestation verification failed with exit code $releaseVerifyExit." }
foreach ($asset in $script:CandidateAssets) {
    $publishedPath = Join-Path $publishedDirectory $asset.Name
    & gh release verify-asset $ReleaseTag $publishedPath --repo $Repository
    $assetVerifyExit = $LASTEXITCODE
    if ($assetVerifyExit -ne 0) { throw "Immutable Release asset attestation verification failed for $($asset.Name) with exit code $assetVerifyExit." }
}

$finalSnapshot = Get-VerifiedReleaseSnapshot $releaseId $false $true 'Final immutable Release'
Assert-ReleaseSnapshotEqual $publishedAfterDownload $finalSnapshot 'Immutable Release changed during attestation verification.'
Assert-TagIdentity
