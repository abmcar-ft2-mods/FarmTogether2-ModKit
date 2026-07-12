[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$StatePath,
    [Parameter(Mandatory)][string]$SnapshotRoot,
    [Parameter(Mandatory)][string]$CanonicalGameDirectory,
    [Parameter(Mandatory)][string]$SaveDirectory,
    [Parameter(Mandatory)][string]$AppManifestPath,
    [Parameter(Mandatory)][string]$DownloadedOldDepot,
    [Parameter(Mandatory)][string]$OldBuildId,
    [Parameter(Mandatory)][string]$OldManifestId,
    [Parameter(Mandatory)][string]$CurrentBuildId,
    [Parameter(Mandatory)][string]$CurrentManifestId,
    [Parameter(Mandatory)][string[]]$ExpectedCurrentNativeHash,
    [Parameter(Mandatory)][string[]]$ModBuild,
    [Parameter(Mandatory)][string]$SupportedBuildsOutput,
    [scriptblock]$WaitForLaunchAndClose,
    [string]$ContractExporterPath = (Join-Path $PSScriptRoot 'Export-GameApiContract.ps1'),
    [scriptblock]$OperationHook
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:CaptureScript = $PSCommandPath
$script:SwitchScript = Join-Path $PSScriptRoot 'Switch-GameBuild.ps1'
$script:AssemblyNames = @(
    'Assembly-CSharp',
    'Il2Cppmscorlib',
    'MilkstoneUnityExtensions',
    'UnityEngine.CoreModule',
    'UnityEngine.IMGUIModule',
    'UnityEngine.InputLegacyModule',
    'UnityEngine.TextRenderingModule'
)
$script:SnapshotFields = @('schemaVersion', 'steamBuildId', 'aggregateSha256', 'assemblyMetadataSha256', 'assemblies')
$script:AssemblyFields = @('name', 'version', 'culture', 'publicKeyToken')
$script:ApprovedModIds = @(
    'com.abmcar.farmtogether2.autosellmod',
    'com.abmcar.farmtogether2.qolmod',
    'com.abmcar.farmtogether2.automodrangemod',
    'com.abmcar.farmtogether2.farmhandspeedmod'
)
$script:PathComparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
$script:ExpectedCurrentNativeHash = $ExpectedCurrentNativeHash
$script:ModBuild = $ModBuild
$script:OperationHook = $OperationHook

function Invoke-CaptureOperationPoint {
    param([Parameter(Mandatory)][string]$Name)

    if ($null -ne $script:OperationHook) {
        & $script:OperationHook $Name
    }
    if ([string]::Equals($env:FARMT2_CAPTURE_HARD_CRASH_AT, $Name, [StringComparison]::Ordinal)) {
        [Environment]::Exit(86)
    }
    if ([string]::Equals($env:FARMT2_CAPTURE_FAIL_AT, $Name, [StringComparison]::Ordinal)) {
        throw "Injected capture failure at $Name."
    }
}

function Get-NormalizedAbsolutePath {
    param([Parameter(Mandatory)][string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw 'A required capture path is empty.'
    }
    $full = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetPathRoot($full)
    while ($full.Length -gt $root.Length -and ($full.EndsWith([IO.Path]::DirectorySeparatorChar) -or $full.EndsWith([IO.Path]::AltDirectorySeparatorChar))) {
        $full = $full.Substring(0, $full.Length - 1)
    }
    return $full
}

function Test-PathIsEqualOrDescendant {
    param(
        [Parameter(Mandatory)][string]$Candidate,
        [Parameter(Mandatory)][string]$Ancestor
    )

    if ([string]::Equals($Candidate, $Ancestor, $script:PathComparison)) {
        return $true
    }
    $prefix = if (
        $Ancestor.EndsWith([IO.Path]::DirectorySeparatorChar) -or
        $Ancestor.EndsWith([IO.Path]::AltDirectorySeparatorChar)
    ) {
        $Ancestor
    } else {
        $Ancestor + [IO.Path]::DirectorySeparatorChar
    }
    return $Candidate.StartsWith($prefix, $script:PathComparison)
}

function Assert-NoExistingAncestorLink {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Label
    )

    $cursor = $Path
    while (-not [string]::IsNullOrEmpty($cursor)) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if ($item.LinkType -or (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
                throw "Invalid capture path topology: $Label has an existing symlink or reparse ancestor at $cursor."
            }
        }
        $parent = Split-Path -Parent $cursor
        if ([string]::IsNullOrEmpty($parent) -or [string]::Equals($parent, $cursor, $script:PathComparison)) {
            break
        }
        $cursor = $parent
    }
}

function Get-CaptureToolSet {
    return @(
        [pscustomobject]@{ Name = 'Capture-InteropSnapshots.ps1'; Path = $script:CaptureScript },
        [pscustomobject]@{ Name = 'Switch-GameBuild.ps1'; Path = $script:SwitchScript },
        [pscustomobject]@{ Name = 'ContractExporterPath'; Path = $ContractExporterPath }
    )
}

function Assert-CapturePathTopology {
    $mutableRoots = @(
        [pscustomobject]@{ Name = 'canonical game'; Path = $CanonicalGameDirectory },
        [pscustomobject]@{ Name = 'save'; Path = $SaveDirectory },
        [pscustomobject]@{ Name = 'downloaded depot'; Path = $DownloadedOldDepot }
    )
    for ($left = 0; $left -lt $mutableRoots.Count; $left++) {
        for ($right = $left + 1; $right -lt $mutableRoots.Count; $right++) {
            if (
                (Test-PathIsEqualOrDescendant $mutableRoots[$left].Path $mutableRoots[$right].Path) -or
                (Test-PathIsEqualOrDescendant $mutableRoots[$right].Path $mutableRoots[$left].Path)
            ) {
                throw "Invalid capture path topology: $($mutableRoots[$left].Name) and $($mutableRoots[$right].Name) are equal or nested."
            }
        }
    }
    $protectedPaths = @(
        [pscustomobject]@{ Name = 'StatePath'; Path = $StatePath },
        [pscustomobject]@{ Name = 'AppManifestPath'; Path = $AppManifestPath },
        [pscustomobject]@{ Name = 'SnapshotRoot'; Path = $SnapshotRoot },
        [pscustomobject]@{ Name = 'SupportedBuildsOutput'; Path = $SupportedBuildsOutput }
    )
    foreach ($protectedPath in $protectedPaths) {
        foreach ($root in $mutableRoots) {
            if (
                (Test-PathIsEqualOrDescendant $protectedPath.Path $root.Path) -or
                (Test-PathIsEqualOrDescendant $root.Path $protectedPath.Path)
            ) {
                throw "Invalid capture path topology: $($protectedPath.Name) and $($root.Name) are equal or nested."
            }
        }
    }
    for ($left = 0; $left -lt $protectedPaths.Count; $left++) {
        for ($right = $left + 1; $right -lt $protectedPaths.Count; $right++) {
            if (
                (Test-PathIsEqualOrDescendant $protectedPaths[$left].Path $protectedPaths[$right].Path) -or
                (Test-PathIsEqualOrDescendant $protectedPaths[$right].Path $protectedPaths[$left].Path)
            ) {
                throw "Invalid capture path topology: $($protectedPaths[$left].Name) and $($protectedPaths[$right].Name) are equal or nested."
            }
        }
    }
    $tools = @(Get-CaptureToolSet)
    foreach ($tool in $tools) {
        foreach ($root in $mutableRoots) {
            if (
                (Test-PathIsEqualOrDescendant $tool.Path $root.Path) -or
                (Test-PathIsEqualOrDescendant $root.Path $tool.Path)
            ) {
                throw "Invalid capture path topology: $($tool.Name) and $($root.Name) are equal or nested."
            }
        }
    }
    foreach ($protectedPath in $protectedPaths) {
        foreach ($tool in $tools) {
            if (
                (Test-PathIsEqualOrDescendant $protectedPath.Path $tool.Path) -or
                (Test-PathIsEqualOrDescendant $tool.Path $protectedPath.Path)
            ) {
                throw "Invalid capture path topology: $($protectedPath.Name) and $($tool.Name) are equal or nested."
            }
        }
    }
    for ($left = 0; $left -lt $tools.Count; $left++) {
        for ($right = $left + 1; $right -lt $tools.Count; $right++) {
            if (
                (Test-PathIsEqualOrDescendant $tools[$left].Path $tools[$right].Path) -or
                (Test-PathIsEqualOrDescendant $tools[$right].Path $tools[$left].Path)
            ) {
                throw "Invalid capture path topology: $($tools[$left].Name) and $($tools[$right].Name) are equal or nested."
            }
        }
    }
}

function Get-StrictJsonObjectPropertyMap {
    param(
        [Parameter(Mandatory)][Text.Json.JsonElement]$Element,
        [Parameter(Mandatory)][string[]]$Expected,
        [Parameter(Mandatory)][string]$Label
    )

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

function Get-OrdinalMapNameSet {
    param([Parameter(Mandatory)]$Value)

    [string[]]$names = if ($Value -is [System.Collections.IDictionary]) {
        @($Value.Keys | ForEach-Object { [string]$_ })
    } else {
        @($Value.PSObject.Properties.Name)
    }
    [Array]::Sort($names, [StringComparer]::Ordinal)
    return $names
}

function Get-MapValue {
    param(
        [Parameter(Mandatory)]$Map,
        [Parameter(Mandatory)][string]$Name
    )

    if ($Map -is [System.Collections.IDictionary]) {
        return [string]$Map[$Name]
    }
    return [string]$Map.PSObject.Properties[$Name].Value
}

function Read-SnapshotDocument {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ExpectedBuildId
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Snapshot record is missing: $Path"
    }
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.LinkType -or (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw "Snapshot record must not be a link: $Path"
    }
    $options = [Text.Json.JsonDocumentOptions]::new()
    $options.AllowTrailingCommas = $false
    $options.CommentHandling = [Text.Json.JsonCommentHandling]::Disallow
    try {
        $document = [Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($Path), $options)
    } catch {
        throw "Snapshot record is invalid JSON: $Path. $($_.Exception.Message)"
    }
    try {
        $root = Get-StrictJsonObjectPropertyMap -Element $document.RootElement -Expected $script:SnapshotFields -Label 'Snapshot record'
        $schema = 0
        if ($root['schemaVersion'].ValueKind -ne [Text.Json.JsonValueKind]::Number -or -not $root['schemaVersion'].TryGetInt32([ref]$schema) -or $schema -ne 1) {
            throw 'Snapshot schemaVersion must be the JSON integer 1.'
        }
        if ($root['steamBuildId'].ValueKind -ne [Text.Json.JsonValueKind]::String -or $root['steamBuildId'].GetString() -cne $ExpectedBuildId) {
            throw "Snapshot identity mismatch for build $ExpectedBuildId."
        }
        if ($root['aggregateSha256'].ValueKind -ne [Text.Json.JsonValueKind]::String) {
            throw "Snapshot aggregate must be a JSON string for build $ExpectedBuildId."
        }
        $aggregate = $root['aggregateSha256'].GetString()
        if ($aggregate -cnotmatch '^[0-9a-f]{64}$') {
            throw "Snapshot aggregate is not lowercase 64-hex for build $ExpectedBuildId."
        }

        $hashObject = Get-StrictJsonObjectPropertyMap -Element $root['assemblyMetadataSha256'] -Expected $script:AssemblyNames -Label 'Snapshot assemblyMetadataSha256'
        $hashes = [Collections.Generic.SortedDictionary[string, string]]::new([StringComparer]::Ordinal)
        foreach ($name in $script:AssemblyNames) {
            $hashElement = $hashObject[$name]
            if ($hashElement.ValueKind -ne [Text.Json.JsonValueKind]::String -or $hashElement.GetString() -cnotmatch '^[0-9a-f]{64}$') {
                throw "Snapshot assembly hash is not a lowercase 64-hex JSON string: $ExpectedBuildId/$name."
            }
            $hashes.Add($name, $hashElement.GetString())
        }

        $assemblyArray = $root['assemblies']
        if ($assemblyArray.ValueKind -ne [Text.Json.JsonValueKind]::Array -or $assemblyArray.GetArrayLength() -ne $script:AssemblyNames.Count) {
            throw "Snapshot assembly identities are not a closed JSON array for build $ExpectedBuildId."
        }
        $identityNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $assemblies = [Collections.Generic.List[object]]::new()
        foreach ($assemblyElement in $assemblyArray.EnumerateArray()) {
            $identity = Get-StrictJsonObjectPropertyMap -Element $assemblyElement -Expected $script:AssemblyFields -Label 'Snapshot assembly identity'
            foreach ($field in $script:AssemblyFields) {
                if ($identity[$field].ValueKind -ne [Text.Json.JsonValueKind]::String) {
                    throw "Snapshot assembly identity field must be a JSON string: $field."
                }
            }
            $name = $identity['name'].GetString()
            $version = $identity['version'].GetString()
            if ([string]::IsNullOrWhiteSpace($name) -or [string]::IsNullOrWhiteSpace($version) -or -not $identityNames.Add($name)) {
                throw "Snapshot assembly identity is invalid for build $ExpectedBuildId."
            }
            $assemblies.Add([pscustomobject]@{
                name = $name
                version = $version
                culture = $identity['culture'].GetString()
                publicKeyToken = $identity['publicKeyToken'].GetString()
            })
        }
        [string[]]$observedIdentityNames = @($identityNames)
        [string[]]$expectedNames = @($script:AssemblyNames)
        [Array]::Sort($observedIdentityNames, [StringComparer]::Ordinal)
        [Array]::Sort($expectedNames, [StringComparer]::Ordinal)
        if (($observedIdentityNames -join "`n") -cne ($expectedNames -join "`n")) {
            throw "Snapshot assembly identity names are not the closed seven assemblies for build $ExpectedBuildId."
        }
        return [pscustomobject]@{
            schemaVersion = $schema
            steamBuildId = $root['steamBuildId'].GetString()
            aggregateSha256 = $aggregate
            assemblyMetadataSha256 = $hashes
            assemblies = @($assemblies)
        }
    } finally {
        $document.Dispose()
    }
}

function Get-SnapshotIdentityVector {
    param([Parameter(Mandatory)]$Snapshot)

    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add("schemaVersion`t$($Snapshot.schemaVersion)")
    $lines.Add("steamBuildId`t$($Snapshot.steamBuildId)")
    $lines.Add("aggregateSha256`t$($Snapshot.aggregateSha256)")
    [string[]]$hashNames = @(Get-OrdinalMapNameSet $Snapshot.assemblyMetadataSha256)
    foreach ($name in $hashNames) {
        $lines.Add("hash`t$name`t$(Get-MapValue $Snapshot.assemblyMetadataSha256 $name)")
    }
    [string[]]$identityNames = @($Snapshot.assemblies | ForEach-Object { $_.name })
    [Array]::Sort($identityNames, [StringComparer]::Ordinal)
    foreach ($name in $identityNames) {
        $identity = @($Snapshot.assemblies | Where-Object { [string]::Equals($_.name, $name, [StringComparison]::Ordinal) })[0]
        $lines.Add("identity`t$($identity.name)`t$($identity.version)`t$($identity.culture)`t$($identity.publicKeyToken)")
    }
    return $lines -join "`n"
}

function Assert-SnapshotDirectoryFileSet {
    param(
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][string]$BuildId
    )

    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) {
        throw "Completed snapshot directory is missing for build ${BuildId}: $Directory"
    }
    $rootItem = Get-Item -LiteralPath $Directory -Force
    if ($rootItem.LinkType -or (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw "Completed snapshot directory must not be a link: $Directory"
    }
    [string[]]$expected = @($script:AssemblyNames | ForEach-Object { "$_.dll" }) + @('snapshot.json')
    [Array]::Sort($expected, [StringComparer]::Ordinal)
    $items = @(Get-ChildItem -LiteralPath $Directory -Force)
    if ($items.Count -ne $expected.Count) {
        throw "Completed snapshot has partial or extra files for build $BuildId."
    }
    foreach ($item in $items) {
        if ($item.PSIsContainer -or $item.LinkType -or (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw "Completed snapshot contains a directory or link for build ${BuildId}: $($item.Name)"
        }
    }
    [string[]]$actual = @($items.Name)
    [Array]::Sort($actual, [StringComparer]::Ordinal)
    if (($actual -join "`n") -cne ($expected -join "`n")) {
        throw "Completed snapshot file allowlist mismatch for build $BuildId."
    }
}

function Invoke-SnapshotExporter {
    param(
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][string]$BuildId,
        [Parameter(Mandatory)][string]$Output
    )

    $null = & $ContractExporterPath -SnapshotInteropDirectory $Directory -SteamBuildId $BuildId -SnapshotOutput $Output
    if (-not (Test-Path -LiteralPath $Output -PathType Leaf)) {
        throw "Snapshot exporter did not create its closed record: $Output"
    }
}

function Test-CompletedSnapshot {
    param([Parameter(Mandatory)][string]$BuildId)

    $directory = Join-Path $SnapshotRoot $BuildId
    if (-not (Test-Path -LiteralPath $directory)) {
        return $false
    }
    Assert-SnapshotDirectoryFileSet $directory $BuildId
    $recordPath = Join-Path $directory 'snapshot.json'
    $existing = Read-SnapshotDocument $recordPath $BuildId
    $validationPath = Join-Path $SnapshotRoot ".$BuildId-$([guid]::NewGuid().ToString('N')).validation.json"
    try {
        Invoke-SnapshotExporter -Directory $directory -BuildId $BuildId -Output $validationPath
        $computed = Read-SnapshotDocument $validationPath $BuildId
        if ((Get-SnapshotIdentityVector $existing) -cne (Get-SnapshotIdentityVector $computed)) {
            throw "Completed snapshot fingerprint validation failed for build $BuildId."
        }
    } finally {
        if (Test-Path -LiteralPath $validationPath -PathType Leaf) {
            [IO.File]::Delete($validationPath)
        }
    }
    return $true
}

function Get-OwnedPreparingPath {
    param(
        [Parameter(Mandatory)][string]$BuildId,
        [Parameter(Mandatory)][string]$AttemptId
    )

    if ($AttemptId -cnotmatch '^[0-9a-f]{32}$') {
        throw "Cannot derive owned snapshot temporary from invalid attemptId: $AttemptId"
    }
    return Join-Path $SnapshotRoot ".$BuildId-$AttemptId.preparing"
}

function Clear-OwnedPreparingDirectorySet {
    param(
        [Parameter(Mandatory)][string]$AttemptId,
        [Parameter(Mandatory)][string[]]$BuildIds
    )

    foreach ($buildId in $BuildIds) {
        $path = Get-OwnedPreparingPath $buildId $AttemptId
        if (-not (Test-Path -LiteralPath $path)) {
            continue
        }
        $item = Get-Item -LiteralPath $path -Force
        if (-not $item.PSIsContainer -or $item.LinkType -or (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw "Owned snapshot preparing path is not a real directory: $path"
        }
        if (-not [string]::Equals((Split-Path -Parent $item.FullName), $SnapshotRoot, $script:PathComparison)) {
            throw "Owned snapshot preparing path escaped SnapshotRoot: $path"
        }
        foreach ($child in @(Get-ChildItem -LiteralPath $path -Force -Recurse)) {
            if ($child.LinkType -or (($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
                throw "Owned snapshot preparing directory contains a link and requires manual inspection: $($child.FullName)"
            }
        }
        [IO.Directory]::Delete($path, $true)
    }
}

function Invoke-SnapshotCapture {
    param(
        [Parameter(Mandatory)][ValidateSet('old', 'current')][string]$Label,
        [Parameter(Mandatory)][string]$BuildId,
        [Parameter(Mandatory)][string]$AttemptId
    )

    $finalDirectory = Join-Path $SnapshotRoot $BuildId
    if (Test-Path -LiteralPath $finalDirectory) {
        throw "Snapshot promotion ambiguity: final directory already exists for build $BuildId."
    }
    $preparing = Get-OwnedPreparingPath $BuildId $AttemptId
    if (Test-Path -LiteralPath $preparing) {
        throw "Owned snapshot preparing directory already exists: $preparing"
    }
    [IO.Directory]::CreateDirectory($preparing) | Out-Null
    Invoke-CaptureOperationPoint "after-create-$Label-preparing"

    $interop = Join-Path $CanonicalGameDirectory 'BepInEx/interop'
    foreach ($name in $script:AssemblyNames) {
        $source = Join-Path $interop "$name.dll"
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Clean launch did not produce required interop assembly: $source"
        }
        $sourceItem = Get-Item -LiteralPath $source -Force
        if ($sourceItem.LinkType -or (($sourceItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw "Interop capture refuses linked assembly: $source"
        }
        [IO.File]::Copy($source, (Join-Path $preparing "$name.dll"), $false)
        Invoke-CaptureOperationPoint "after-copy-$Label-$name.dll"
    }
    $recordPath = Join-Path $preparing 'snapshot.json'
    Invoke-SnapshotExporter -Directory $preparing -BuildId $BuildId -Output $recordPath
    Invoke-CaptureOperationPoint "after-fingerprint-$Label"
    Assert-SnapshotDirectoryFileSet $preparing $BuildId
    $null = Read-SnapshotDocument $recordPath $BuildId
    if (Test-Path -LiteralPath $finalDirectory) {
        throw "Snapshot promotion ambiguity: final directory appeared for build $BuildId."
    }
    [IO.Directory]::Move($preparing, $finalDirectory)
    Invoke-CaptureOperationPoint "after-promote-$Label"
}

function Get-SwitchTemporaryFileSet {
    $parent = Split-Path -Parent $StatePath
    $leaf = Split-Path -Leaf $StatePath
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        return @()
    }
    return @(Get-ChildItem -LiteralPath $parent -File -Force | Where-Object {
        $_.Name.StartsWith("$leaf.", [StringComparison]::Ordinal) -and $_.Name.EndsWith('.preparing', [StringComparison]::Ordinal)
    })
}

function Test-SwitchRecordPresence {
    return (Test-Path -LiteralPath $StatePath -PathType Leaf) -or @(Get-SwitchTemporaryFileSet).Count -ne 0
}

function Invoke-GameSwitch {
    param([Parameter(Mandatory)][ValidateSet('ActivateOld', 'ActivateCurrent', 'Restore')][string]$RequestedAction)

    if ($RequestedAction -ceq 'ActivateOld') {
        $parameters = @{
            Action = 'ActivateOld'
            StatePath = $StatePath
            CanonicalGameDirectory = $CanonicalGameDirectory
            SaveDirectory = $SaveDirectory
            AppManifestPath = $AppManifestPath
            DownloadedOldDepot = $DownloadedOldDepot
            OldBuildId = $OldBuildId
            OldManifestId = $OldManifestId
            CurrentBuildId = $CurrentBuildId
            CurrentManifestId = $CurrentManifestId
            ExpectedCurrentNativeHash = $script:ExpectedCurrentNativeHash
        }
    } else {
        $parameters = @{ Action = $RequestedAction; StatePath = $StatePath }
    }
    $null = & $script:SwitchScript @parameters
}

function Read-RestoredSwitchState {
    if (-not (Test-Path -LiteralPath $StatePath -PathType Leaf)) {
        throw "Restored game-switch record is missing: $StatePath"
    }
    $state = Get-Content -Raw -LiteralPath $StatePath | ConvertFrom-Json
    if ([string]$state.phase -cne 'restored' -or [string]$state.attemptId -cnotmatch '^[0-9a-f]{32}$') {
        throw "Game-switch restore did not reach a valid terminal state: $StatePath"
    }
    if ([string]$state.oldBuildId -cne $OldBuildId -or [string]$state.currentBuildId -cne $CurrentBuildId) {
        throw 'Restored game-switch record does not match the requested old/current build IDs.'
    }
    return $state
}

function Read-ActiveSwitchAttempt {
    param([Parameter(Mandatory)][string]$ExpectedPhase)

    if (-not (Test-Path -LiteralPath $StatePath -PathType Leaf)) {
        throw "Active game-switch record is missing: $StatePath"
    }
    $state = Get-Content -Raw -LiteralPath $StatePath | ConvertFrom-Json
    if ([string]$state.phase -cne $ExpectedPhase -or [string]$state.attemptId -cnotmatch '^[0-9a-f]{32}$') {
        throw "Game-switch state did not reach $ExpectedPhase."
    }
    return [string]$state.attemptId
}

function Get-RestoredArchivePath {
    param([Parameter(Mandatory)][string]$AttemptId)

    $parent = Split-Path -Parent $StatePath
    $leaf = Split-Path -Leaf $StatePath
    $extension = [IO.Path]::GetExtension($leaf)
    $stem = [IO.Path]::GetFileNameWithoutExtension($leaf)
    return Join-Path $parent "$stem-$AttemptId-restored$extension"
}

function Restore-AndArchivePriorSwitch {
    if (-not (Test-SwitchRecordPresence)) {
        return
    }
    Invoke-GameSwitch Restore
    Invoke-CaptureOperationPoint 'after-restore-pending-state'
    $state = Read-RestoredSwitchState
    Clear-OwnedPreparingDirectorySet -AttemptId ([string]$state.attemptId) -BuildIds @([string]$state.oldBuildId, [string]$state.currentBuildId)
    $archive = Get-RestoredArchivePath ([string]$state.attemptId)
    if (Test-Path -LiteralPath $archive) {
        throw "Restored switch archive ambiguity: $archive already exists."
    }
    [IO.File]::Move($StatePath, $archive, $false)
    Invoke-CaptureOperationPoint 'after-archive-restored-state'
}

function Assert-ExpectedNativeHashInputSet {
    if ($script:ExpectedCurrentNativeHash.Count -eq 0) {
        throw 'ExpectedCurrentNativeHash must not be empty.'
    }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $script:ExpectedCurrentNativeHash) {
        $separator = $entry.IndexOf('=')
        if ($separator -le 0 -or $separator -eq $entry.Length - 1) {
            throw "ExpectedCurrentNativeHash entry must be relative-path=lowercase-sha256: $entry"
        }
        $path = $entry.Substring(0, $separator).Replace('\', '/')
        $hash = $entry.Substring($separator + 1)
        if (
            [string]::IsNullOrWhiteSpace($path) -or
            $path.StartsWith('/', [StringComparison]::Ordinal) -or
            $path.EndsWith('/', [StringComparison]::Ordinal) -or
            $path -match '^[A-Za-z]:' -or
            $path.Contains('//', [StringComparison]::Ordinal) -or
            @($path.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -ne 0
        ) {
            throw "ExpectedCurrentNativeHash contains a non-canonical path: $entry"
        }
        if ($hash -cnotmatch '^[0-9a-f]{64}$') {
            throw "ExpectedCurrentNativeHash must use lowercase 64-hex SHA-256: $entry"
        }
        if (-not $seen.Add($path)) {
            throw "ExpectedCurrentNativeHash contains a duplicate normalized path: $path"
        }
    }
}

function Assert-ModBuildInputSet {
    if ($script:ModBuild.Count -ne 4) {
        throw 'Capture requires exactly four closed ModBuild mappings.'
    }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $usedBuilds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($entry in $script:ModBuild) {
        $separator = $entry.IndexOf('=')
        if ($separator -le 0 -or $separator -eq $entry.Length - 1) {
            throw "Invalid ModBuild mapping: $entry"
        }
        $modId = $entry.Substring(0, $separator)
        $buildId = $entry.Substring($separator + 1)
        if (-not ($script:ApprovedModIds -ccontains $modId) -or -not $seen.Add($modId)) {
            throw "Unknown or duplicate ModBuild ID: $modId"
        }
        if ($buildId -cne $OldBuildId -and $buildId -cne $CurrentBuildId) {
            throw "ModBuild references an unknown capture build: $entry"
        }
        $null = $usedBuilds.Add($buildId)
    }
    if ($seen.Count -ne $script:ApprovedModIds.Count -or -not $usedBuilds.Contains($OldBuildId) -or -not $usedBuilds.Contains($CurrentBuildId)) {
        throw 'ModBuild mappings must cover all approved mods and both capture builds.'
    }
}

function Write-SupportedBuildFile {
    [string[]]$snapshots = @(
        (Join-Path (Join-Path $SnapshotRoot $OldBuildId) 'snapshot.json'),
        (Join-Path (Join-Path $SnapshotRoot $CurrentBuildId) 'snapshot.json')
    )
    [string[]]$modBuilds = @($script:ModBuild)
    $null = & $ContractExporterPath -BuildSnapshot $snapshots -ModBuild $modBuilds -SupportedBuildsOutput $SupportedBuildsOutput
    if (-not (Test-Path -LiteralPath $SupportedBuildsOutput -PathType Leaf)) {
        throw "Supported-build exporter did not write output: $SupportedBuildsOutput"
    }
    Invoke-CaptureOperationPoint 'after-write-supported-builds'
}

function Invoke-CaptureWorkflow {
    Restore-AndArchivePriorSwitch
    $oldComplete = Test-CompletedSnapshot $OldBuildId
    $currentComplete = Test-CompletedSnapshot $CurrentBuildId

    if ($oldComplete -and $currentComplete) {
        Write-SupportedBuildFile
        return
    }

    Invoke-GameSwitch ActivateOld
    Invoke-CaptureOperationPoint 'after-activate-old'
    $attemptId = Read-ActiveSwitchAttempt 'old-active'
    if (-not $oldComplete) {
        $null = & $WaitForLaunchAndClose $OldBuildId $CanonicalGameDirectory
        Invoke-CaptureOperationPoint 'after-wait-old'
        Invoke-SnapshotCapture -Label old -BuildId $OldBuildId -AttemptId $attemptId
    }

    Invoke-GameSwitch ActivateCurrent
    Invoke-CaptureOperationPoint 'after-activate-current'
    $currentAttemptId = Read-ActiveSwitchAttempt 'current-smoke-active'
    if ($currentAttemptId -cne $attemptId) {
        throw 'Game-switch attemptId changed between old and current capture.'
    }
    if (-not $currentComplete) {
        $null = & $WaitForLaunchAndClose $CurrentBuildId $CanonicalGameDirectory
        Invoke-CaptureOperationPoint 'after-wait-current'
        Invoke-SnapshotCapture -Label current -BuildId $CurrentBuildId -AttemptId $attemptId
    }

    Write-SupportedBuildFile
}

$StatePath = Get-NormalizedAbsolutePath $StatePath
$SnapshotRoot = Get-NormalizedAbsolutePath $SnapshotRoot
$CanonicalGameDirectory = Get-NormalizedAbsolutePath $CanonicalGameDirectory
$SaveDirectory = Get-NormalizedAbsolutePath $SaveDirectory
$AppManifestPath = Get-NormalizedAbsolutePath $AppManifestPath
$DownloadedOldDepot = Get-NormalizedAbsolutePath $DownloadedOldDepot
$SupportedBuildsOutput = Get-NormalizedAbsolutePath $SupportedBuildsOutput
$ContractExporterPath = Get-NormalizedAbsolutePath $ContractExporterPath
$script:CaptureScript = Get-NormalizedAbsolutePath $script:CaptureScript
$script:SwitchScript = Get-NormalizedAbsolutePath $script:SwitchScript

if ($OldBuildId -cnotmatch '^[0-9]+$' -or $CurrentBuildId -cnotmatch '^[0-9]+$' -or $OldBuildId -ceq $CurrentBuildId) {
    throw 'Capture requires two distinct decimal Steam build IDs.'
}
if ($OldManifestId -cnotmatch '^[0-9]+$' -or $CurrentManifestId -cnotmatch '^[0-9]+$') {
    throw 'Capture requires decimal old/current manifest IDs.'
}
foreach ($tool in @(Get-CaptureToolSet)) {
    if (-not (Test-Path -LiteralPath $tool.Path -PathType Leaf)) {
        throw "Capture tooling is missing: $($tool.Name): $($tool.Path)"
    }
    $toolItem = Get-Item -LiteralPath $tool.Path -Force
    if ($toolItem.LinkType -or (($toolItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw "Capture tooling must not be a symlink or reparse point: $($tool.Path)"
    }
}
if (Test-Path -LiteralPath $SupportedBuildsOutput) {
    $supportedItem = Get-Item -LiteralPath $SupportedBuildsOutput -Force
    if ($supportedItem.PSIsContainer -or $supportedItem.LinkType -or (($supportedItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw "SupportedBuildsOutput must be a regular file path: $SupportedBuildsOutput"
    }
}
Assert-ExpectedNativeHashInputSet
Assert-ModBuildInputSet
Assert-CapturePathTopology
Assert-NoExistingAncestorLink -Path $CanonicalGameDirectory -Label 'CanonicalGameDirectory'
Assert-NoExistingAncestorLink -Path $SaveDirectory -Label 'SaveDirectory'
Assert-NoExistingAncestorLink -Path $DownloadedOldDepot -Label 'DownloadedOldDepot'
Assert-NoExistingAncestorLink -Path $AppManifestPath -Label 'AppManifestPath'
Assert-NoExistingAncestorLink -Path $StatePath -Label 'StatePath'
Assert-NoExistingAncestorLink -Path $SnapshotRoot -Label 'SnapshotRoot'
Assert-NoExistingAncestorLink -Path $SupportedBuildsOutput -Label 'SupportedBuildsOutput'
foreach ($tool in @(Get-CaptureToolSet)) {
    Assert-NoExistingAncestorLink -Path $tool.Path -Label $tool.Name
}
if (-not (Test-Path -LiteralPath $CanonicalGameDirectory -PathType Container)) {
    throw "CanonicalGameDirectory is missing: $CanonicalGameDirectory"
}
if ((Test-Path -LiteralPath $SaveDirectory) -and -not (Test-Path -LiteralPath $SaveDirectory -PathType Container)) {
    throw "SaveDirectory exists but is not a directory: $SaveDirectory"
}
if (-not (Test-Path -LiteralPath $DownloadedOldDepot -PathType Container)) {
    throw "DownloadedOldDepot is missing: $DownloadedOldDepot"
}
if (-not (Test-Path -LiteralPath $AppManifestPath -PathType Leaf)) {
    throw "AppManifestPath is missing: $AppManifestPath"
}
[IO.Directory]::CreateDirectory($SnapshotRoot) | Out-Null
$snapshotRootItem = Get-Item -LiteralPath $SnapshotRoot -Force
if ($snapshotRootItem.LinkType -or (($snapshotRootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
    throw "SnapshotRoot must not be a symlink or reparse point: $SnapshotRoot"
}
[IO.Directory]::CreateDirectory((Split-Path -Parent $SupportedBuildsOutput)) | Out-Null
if ($null -eq $WaitForLaunchAndClose) {
    $WaitForLaunchAndClose = {
        param($BuildId, $GameDirectory)
        $null = Read-Host "Launch build $BuildId from '$GameDirectory' offline, wait for the menu, close it, then press Enter"
    }
}

$captureError = $null
$restoreError = $null
try {
    try {
        Invoke-CaptureWorkflow
    } catch {
        $captureError = $_
    }
} finally {
    try {
        if (Test-SwitchRecordPresence) {
            Invoke-GameSwitch Restore
            $restoredState = Read-RestoredSwitchState
            Clear-OwnedPreparingDirectorySet -AttemptId ([string]$restoredState.attemptId) -BuildIds @([string]$restoredState.oldBuildId, [string]$restoredState.currentBuildId)
        }
    } catch {
        $restoreError = $_
    }
}

if ($null -ne $restoreError) {
    if ($null -ne $captureError) {
        throw "Interop capture failed: $($captureError.Exception.Message) Restoration also failed: $($restoreError.Exception.Message)"
    }
    throw $restoreError
}
if ($null -ne $captureError) {
    throw $captureError
}
