#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$RepositoryRoot,
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string]$ModKitRepository = 'abmcar-ft2-mods/FarmTogether2-ModKit',
    [Parameter(Mandatory)][ValidatePattern('^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')][string]$Tag,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ExpectedReleaseManifest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:PathComparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
$script:LockFields = @('schemaVersion','repository','workflowCommit','packageId','packageVersion','releaseTag','assetName','sha256')

function Get-NormalizedPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw 'A required lock-writer path is empty.'
    }
    return [IO.Path]::GetFullPath($Path)
}

function Assert-NoReparseAncestor([string]$Path, [string]$Label) {
    $current = Get-NormalizedPath $Path
    while (-not [string]::IsNullOrEmpty($current)) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "$Label contains a symlink or reparse-point ancestor: $current"
            }
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ([string]::IsNullOrEmpty($parent) -or [string]::Equals($parent, $current, $script:PathComparison)) {
            break
        }
        $current = $parent
    }
}

function Assert-RegularFile([string]$Path, [string]$Label) {
    Assert-NoReparseAncestor $Path $Label
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label does not exist: $Path"
    }
    $item = Get-Item -LiteralPath $Path -Force
    if (-not ($item -is [IO.FileInfo]) -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Label must be a regular file: $Path"
    }
}

function Get-StrictPropertyMap(
    [Text.Json.JsonElement]$Element,
    [string[]]$Expected,
    [string]$Label
) {
    if ($Element.ValueKind -ne [Text.Json.JsonValueKind]::Object) {
        throw "$Label must be a JSON object."
    }
    $properties = [Collections.Generic.Dictionary[string, Text.Json.JsonElement]]::new([StringComparer]::Ordinal)
    foreach ($property in $Element.EnumerateObject()) {
        if (-not $properties.TryAdd($property.Name, $property.Value.Clone())) {
            throw "$Label contains duplicate field $($property.Name)."
        }
    }
    if ($properties.Count -ne $Expected.Count) {
        throw "$Label has missing or extra fields."
    }
    foreach ($field in $Expected) {
        if (-not $properties.ContainsKey($field)) {
            throw "$Label is missing or incorrectly cased field $field."
        }
    }
    return ,$properties
}

function Read-ClosedLockJson([string]$Path, [string]$Label) {
    Assert-RegularFile $Path $Label
    $options = [Text.Json.JsonDocumentOptions]::new()
    $options.AllowTrailingCommas = $false
    $options.CommentHandling = [Text.Json.JsonCommentHandling]::Disallow
    try {
        $document = [Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($Path), $options)
    } catch {
        throw "$Label is invalid JSON: $($_.Exception.Message)"
    }
    try {
        $properties = Get-StrictPropertyMap $document.RootElement $script:LockFields $Label
        $schema = 0
        if ($properties['schemaVersion'].ValueKind -ne [Text.Json.JsonValueKind]::Number -or
            -not $properties['schemaVersion'].TryGetInt32([ref]$schema) -or $schema -ne 1 -or
            $properties['schemaVersion'].GetRawText() -cne '1') {
            throw "$Label schemaVersion must be the JSON integer 1."
        }
        $values = [ordered]@{ schemaVersion = 1 }
        foreach ($field in $script:LockFields | Where-Object { $_ -cne 'schemaVersion' }) {
            if ($properties[$field].ValueKind -ne [Text.Json.JsonValueKind]::String) {
                throw "$Label field $field must be a JSON string."
            }
            $values[$field] = $properties[$field].GetString()
        }
        $value = [pscustomobject]$values
    } finally {
        $document.Dispose()
    }

    $repositorySegments = @([string]$value.repository -split '/')
    if ($value.repository -cnotmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or
        $repositorySegments -contains '.' -or $repositorySegments -contains '..' -or
        $value.workflowCommit -cnotmatch '^[0-9a-f]{40}$' -or
        $value.packageId -cne 'FarmTogether2.GameApi.Ref' -or
        $value.packageVersion -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$' -or
        $value.releaseTag -cne "v$($value.packageVersion)" -or
        $value.assetName -cne "$($value.packageId).$($value.packageVersion).nupkg" -or
        $value.sha256 -cnotmatch '^[0-9a-f]{64}$') {
        throw "$Label identity is noncanonical."
    }
    return $value
}

function Assert-LockValuesEqual([object]$Expected, [object]$Actual, [string]$Label) {
    foreach ($field in $script:LockFields) {
        if ([string]$Actual.$field -cne [string]$Expected.$field) {
            throw "$Label differs at $field."
        }
    }
}

function Invoke-Tool([string[]]$Arguments) {
    & dotnet run --project $toolProject -c Release --no-restore -- @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "ModKit lock tool failed with exit code $LASTEXITCODE."
    }
}

function Invoke-GhJson([string]$ApiPath) {
    $output = @(& gh api --method GET `
        --header 'Accept: application/vnd.github+json' `
        --header 'X-GitHub-Api-Version: 2026-03-10' `
        $ApiPath)
    if ($LASTEXITCODE -ne 0) {
        throw "gh api failed for $ApiPath with exit code $LASTEXITCODE."
    }
    if ($output.Count -eq 0) {
        throw "gh api returned no JSON for $ApiPath."
    }
    try {
        return ($output -join [Environment]::NewLine) | ConvertFrom-Json -Depth 30
    } catch {
        throw "gh api returned invalid JSON for ${ApiPath}: $($_.Exception.Message)"
    }
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

function Get-ReleaseCommit([string]$Repository, [string]$ReleaseTag) {
    $reference = Invoke-GhJson "repos/$Repository/git/ref/tags/$ReleaseTag"
    $object = Get-RequiredProperty $reference 'object' 'Tag object'
    for ($depth = 0; $depth -lt 8; $depth++) {
        $type = Get-RequiredProperty $object 'type' 'Git object type'
        $sha = Get-RequiredProperty $object 'sha' 'Git object sha'
        if (-not ($type -is [string]) -or -not ($sha -is [string]) -or $sha -cnotmatch '^[0-9a-f]{40}$') {
            throw 'Tag resolution returned a noncanonical Git object.'
        }
        if ($type -ceq 'commit') {
            return $sha
        }
        if ($type -cne 'tag') {
            throw "Tag resolves to unsupported Git object type: $type"
        }
        $tagObject = Invoke-GhJson "repos/$Repository/git/tags/$sha"
        $object = Get-RequiredProperty $tagObject 'object' 'Annotated tag object'
    }
    throw 'Annotated tag chain is too deep.'
}

function Get-ReleaseSnapshot([object]$Manifest) {
    $release = Invoke-GhJson "repos/$($Manifest.repository)/releases/tags/$($Manifest.releaseTag)"
    $releaseId = Get-RequiredProperty $release 'id' 'Release id'
    $releaseTag = Get-RequiredProperty $release 'tag_name' 'Release tag_name'
    $draft = Get-RequiredProperty $release 'draft' 'Release draft state'
    $prerelease = Get-RequiredProperty $release 'prerelease' 'Release prerelease state'
    if (-not ($releaseId -is [long] -or $releaseId -is [int]) -or [long]$releaseId -le 0 -or
        -not ($releaseTag -is [string]) -or $releaseTag -cne [string]$Manifest.releaseTag -or
        -not ($draft -is [bool]) -or $draft -or
        -not ($prerelease -is [bool]) -or $prerelease) {
        throw 'Live Release is not the requested stable published tag.'
    }

    $assets = @(Get-RequiredProperty $release 'assets' 'Release assets')
    $assetMatches = @($assets | Where-Object {
        $name = $_.PSObject.Properties['name']
        $null -ne $name -and $name.Value -is [string] -and $name.Value -ceq [string]$Manifest.assetName
    })
    if ($assetMatches.Count -ne 1) {
        throw 'Live Release must contain exactly one matching reference package asset.'
    }
    $asset = $assetMatches[0]
    $assetId = Get-RequiredProperty $asset 'id' 'Release asset id'
    $assetState = Get-RequiredProperty $asset 'state' 'Release asset state'
    $assetSize = Get-RequiredProperty $asset 'size' 'Release asset size'
    if (-not ($assetId -is [long] -or $assetId -is [int]) -or [long]$assetId -le 0 -or
        -not ($assetState -is [string]) -or $assetState -cne 'uploaded' -or
        -not ($assetSize -is [long] -or $assetSize -is [int]) -or [long]$assetSize -le 0) {
        throw 'Live Release asset is not a nonempty uploaded file.'
    }
    $digestProperty = $asset.PSObject.Properties['digest']
    $digest = if ($null -eq $digestProperty -or $null -eq $digestProperty.Value) { $null } else { [string]$digestProperty.Value }
    if ($null -ne $digest -and $digest -cne "sha256:$($Manifest.sha256)") {
        throw 'Live Release asset API digest differs from the expected release manifest.'
    }
    $commit = Get-ReleaseCommit ([string]$Manifest.repository) ([string]$Manifest.releaseTag)
    if ($commit -cne [string]$Manifest.workflowCommit) {
        throw 'Release tag commit differs from the expected release manifest.'
    }
    return [pscustomobject]@{
        releaseId = [long]$releaseId
        assetId = [long]$assetId
        assetSize = [long]$assetSize
        assetDigest = $digest
        commit = $commit
    }
}

function Assert-ReleaseSnapshotEqual([object]$Expected, [object]$Actual) {
    foreach ($field in 'releaseId','assetId','assetSize','assetDigest','commit') {
        if ([string]$Actual.$field -cne [string]$Expected.$field) {
            throw "Live Release identity changed during lock creation at $field."
        }
    }
}

$root = Get-NormalizedPath $RepositoryRoot
Assert-NoReparseAncestor $root 'Repository root'
if (-not (Test-Path -LiteralPath $root -PathType Container)) {
    throw "Repository root does not exist: $root"
}
$manifestPath = Get-NormalizedPath $ExpectedReleaseManifest
$manifest = Read-ClosedLockJson $manifestPath 'Expected release manifest'
if ([string]$manifest.repository -cne $ModKitRepository) {
    throw 'Expected release manifest repository does not match ModKitRepository.'
}
if ([string]$manifest.releaseTag -cne $Tag) {
    throw 'Expected release manifest tag does not match Tag.'
}

$toolProject = Join-Path (Split-Path -Parent $PSScriptRoot) 'tools/FarmTogether2.ModKit.Tool/FarmTogether2.ModKit.Tool.csproj'
Assert-RegularFile $toolProject 'ModKit lock tool project'
$output = Join-Path $root 'modkit.lock.json'
Assert-NoReparseAncestor $output 'Lock output'
if (Test-Path -LiteralPath $output -PathType Container) {
    throw "Lock output is a directory: $output"
}
if (Test-Path -LiteralPath $output -PathType Leaf) {
    Assert-RegularFile $output 'Existing lock output'
}

$initialRelease = Get-ReleaseSnapshot $manifest
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "farmtogether2-modkit-lock-$([guid]::NewGuid().ToString('N'))"
Assert-NoReparseAncestor ([IO.Path]::GetDirectoryName($temporaryRoot)) 'Temporary root parent'
[IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
$lockPreparing = Join-Path $root ".modkit.lock.$([guid]::NewGuid().ToString('N')).preparing"
try {
    & gh release download $Tag -R $ModKitRepository `
        --pattern ([string]$manifest.assetName) `
        --dir $temporaryRoot
    if ($LASTEXITCODE -ne 0) {
        throw "gh release download failed with exit code $LASTEXITCODE."
    }
    $downloaded = @(Get-ChildItem -LiteralPath $temporaryRoot -File -Force)
    if ($downloaded.Count -ne 1 -or $downloaded[0].Name -cne [string]$manifest.assetName) {
        throw 'Release download did not produce the one expected package asset.'
    }
    Assert-RegularFile $downloaded[0].FullName 'Downloaded Release package'
    $actualHash = (Get-FileHash -LiteralPath $downloaded[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -cne [string]$manifest.sha256) {
        throw 'Downloaded Release package SHA-256 differs from the expected release manifest.'
    }
    $finalRelease = Get-ReleaseSnapshot $manifest
    Assert-ReleaseSnapshotEqual $initialRelease $finalRelease

    & dotnet restore $toolProject --locked-mode
    if ($LASTEXITCODE -ne 0) {
        throw 'Locked ModKit tool restore failed.'
    }
    Invoke-Tool @('lock', 'verify', '--file', $manifestPath)
    Invoke-Tool @(
        'ref-package', 'verify',
        '--package', $downloaded[0].FullName,
        '--package-id', ([string]$manifest.packageId),
        '--version', ([string]$manifest.packageVersion)
    )
    Invoke-Tool @(
        'lock', 'write',
        '--output', $lockPreparing,
        '--schema-version', '1',
        '--repository', ([string]$manifest.repository),
        '--workflow-commit', ([string]$manifest.workflowCommit),
        '--package-id', ([string]$manifest.packageId),
        '--package-version', ([string]$manifest.packageVersion),
        '--release-tag', ([string]$manifest.releaseTag),
        '--asset-name', ([string]$manifest.assetName),
        '--sha256', ([string]$manifest.sha256)
    )
    $prepared = Read-ClosedLockJson $lockPreparing 'Prepared lock'
    Assert-LockValuesEqual $manifest $prepared 'Prepared lock'

    Assert-NoReparseAncestor $output 'Lock output'
    if (Test-Path -LiteralPath $output -PathType Leaf) {
        Assert-RegularFile $output 'Existing lock output'
    }
    [IO.File]::Move($lockPreparing, $output, $true)
    $written = Read-ClosedLockJson $output 'Written lock'
    Assert-LockValuesEqual $manifest $written 'Written lock'
} finally {
    if (Test-Path -LiteralPath $lockPreparing -PathType Leaf) {
        [IO.File]::Delete($lockPreparing)
    }
    if (Test-Path -LiteralPath $temporaryRoot -PathType Container) {
        [IO.Directory]::Delete($temporaryRoot, $true)
    }
}
