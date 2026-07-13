[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('ActivateOld', 'ActivateCurrent', 'Restore')]
    [string]$Action,

    [Parameter(Mandatory)]
    [string]$StatePath,

    [string]$CanonicalGameDirectory,
    [string]$SaveDirectory,
    [string]$AppManifestPath,
    [string]$DownloadedOldDepot,
    [string]$OldBuildId,
    [string]$OldManifestId,
    [string]$CurrentBuildId,
    [string]$CurrentManifestId,
    [string[]]$ExpectedOldNativeHash,
    [string[]]$ExpectedCurrentNativeHash,

    [scriptblock]$OperationHook
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:PathComparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }

$script:LegacyStateFields = @(
    'schemaVersion',
    'attemptId',
    'phase',
    'canonical',
    'saveDirectory',
    'appManifestPath',
    'downloadedOldDepot',
    'originalGameDirectory',
    'oldSmokeGameDirectory',
    'currentSmokeGameDirectory',
    'originalSaveDirectory',
    'oldSmokeSaveDirectory',
    'currentSmokeSaveDirectory',
    'saveWasPresent',
    'originalSaveFingerprint',
    'appManifestSha256',
    'oldBuildId',
    'currentBuildId',
    'oldManifestId',
    'currentManifestId',
    'oldHashes',
    'currentHashes',
    'oldFingerprint',
    'currentFingerprint'
)

$script:StateFields = @(
    'schemaVersion',
    'attemptId',
    'phase',
    'canonical',
    'saveDirectory',
    'appManifestPath',
    'downloadedOldDepot',
    'originalGameDirectory',
    'oldSmokeGameDirectory',
    'currentSmokeGameDirectory',
    'originalSaveDirectory',
    'oldSmokeSaveDirectory',
    'currentSmokeSaveDirectory',
    'saveWasPresent',
    'originalSaveFingerprint',
    'appManifestAppId',
    'appManifestNormalizedSha256',
    'currentDepotManifests',
    'oldBuildId',
    'currentBuildId',
    'oldManifestId',
    'currentManifestId',
    'oldHashes',
    'currentHashes',
    'oldFingerprint',
    'currentFingerprint'
)

$script:KnownPhases = @(
    'prepared',
    'original-game-move-pending',
    'original-game-preserved',
    'original-save-move-pending',
    'originals-preserved',
    'old-copy-preparing',
    'old-copy-prepared',
    'old-activate-pending',
    'old-active',
    'old-deactivate-pending',
    'old-game-preserved',
    'old-save-move-pending',
    'old-save-preserved',
    'current-copy-preparing',
    'current-copy-prepared',
    'current-activate-pending',
    'current-smoke-active',
    'restore-old-game-pending',
    'restore-old-game-preserved',
    'restore-current-game-pending',
    'restore-current-game-preserved',
    'restore-no-active-game',
    'restore-old-save-pending',
    'restore-current-save-pending',
    'restore-save-preserved',
    'restore-original-game-pending',
    'restore-original-game-restored',
    'restore-original-save-pending',
    'restored'
)

$script:LoaderAllowlist = @(
    [pscustomobject]@{ Path = 'doorstop_config.ini'; Required = $true },
    [pscustomobject]@{ Path = '.doorstop_version'; Required = $false },
    [pscustomobject]@{ Path = 'winhttp.dll'; Required = $true },
    [pscustomobject]@{ Path = 'dotnet'; Required = $true },
    [pscustomobject]@{ Path = 'BepInEx/core'; Required = $true },
    [pscustomobject]@{ Path = 'BepInEx/unity-libs'; Required = $false },
    [pscustomobject]@{ Path = 'BepInEx/config/BepInEx.cfg'; Required = $false }
)

$script:RequiredLoaderFiles = @(
    'dotnet/coreclr.dll',
    'dotnet/System.Private.CoreLib.dll',
    'BepInEx/core/BepInEx.Unity.IL2CPP.dll'
)

function Invoke-OperationPoint {
    param([Parameter(Mandatory)][string]$Name)

    if ($null -ne $OperationHook) {
        & $OperationHook $Name
    }
    if ([string]::Equals($env:FARMT2_SWITCH_FAIL_AT, $Name, [StringComparison]::Ordinal)) {
        throw "Injected switch failure at $Name."
    }
}

function Get-NormalizedAbsolutePath {
    param([Parameter(Mandatory)][string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw 'A required path is empty.'
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

function Assert-ExistingDirectoryRootIsNotLink {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Label
    )

    Assert-NoExistingAncestorLink -Path $Path -Label $Label
    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }
    $item = Get-Item -LiteralPath $Path -Force
    if (-not $item.PSIsContainer) {
        throw "$Label root is not a directory: $Path"
    }
    if ($item.LinkType -or (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw "$Label root must not be a symlink or reparse point: $Path"
    }
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
                throw "$Label has an existing symlink or reparse ancestor at $cursor."
            }
        }
        $parent = Split-Path -Parent $cursor
        if ([string]::IsNullOrEmpty($parent) -or [string]::Equals($parent, $cursor, $script:PathComparison)) {
            break
        }
        $cursor = $parent
    }
}

function Assert-RequiredLoaderFileSet {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Label
    )

    foreach ($relativePath in $script:RequiredLoaderFiles) {
        $path = Join-Path $Root $relativePath
        Assert-NoExistingAncestorLink -Path $path -Label "$Label required loader file"
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "$Label required loader file is missing: $path"
        }
        $item = Get-Item -LiteralPath $path -Force
        if ($item.LinkType -or (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw "$Label required loader file must not be a symlink or reparse point: $path"
        }
    }
}

function Assert-DirectoryTreeHasNoLinks {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Label
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }
    Assert-ExistingDirectoryRootIsNotLink -Path $Path -Label $Label
    foreach ($item in @(Get-ChildItem -LiteralPath $Path -Force -Recurse)) {
        if ($item.LinkType -or (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw "$Label must not contain symlinks or reparse points: $($item.FullName)"
        }
    }
}

function Assert-InputPathTopology {
    param(
        [Parameter(Mandatory)][string]$Canonical,
        [Parameter(Mandatory)][string]$Save,
        [Parameter(Mandatory)][string]$Depot,
        [Parameter(Mandatory)][string]$Manifest
    )

    $trees = @(
        [pscustomobject]@{ Name = 'canonical game'; Path = $Canonical },
        [pscustomobject]@{ Name = 'save'; Path = $Save },
        [pscustomobject]@{ Name = 'downloaded depot'; Path = $Depot }
    )
    for ($left = 0; $left -lt $trees.Count; $left++) {
        for ($right = $left + 1; $right -lt $trees.Count; $right++) {
            if (
                (Test-PathIsEqualOrDescendant $trees[$left].Path $trees[$right].Path) -or
                (Test-PathIsEqualOrDescendant $trees[$right].Path $trees[$left].Path)
            ) {
                throw "Invalid path topology: $($trees[$left].Name) and $($trees[$right].Name) are equal or nested."
            }
        }
    }
    foreach ($external in @(
        [pscustomobject]@{ Name = 'StatePath'; Path = $StatePath },
        [pscustomobject]@{ Name = 'AppManifestPath'; Path = $Manifest }
    )) {
        foreach ($tree in $trees) {
            if (Test-PathIsEqualOrDescendant $external.Path $tree.Path) {
                throw "Invalid path topology: $($external.Name) is inside the $($tree.Name) tree."
            }
        }
    }
    if ([string]::Equals($StatePath, $Manifest, $script:PathComparison)) {
        throw 'Invalid path topology: StatePath and AppManifestPath are the same file.'
    }
}

function Get-Sha256 {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required file is missing: $Path"
    }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-KeyValuesTokenList {
    param([Parameter(Mandatory)][string]$Text)

    $tokens = [Collections.Generic.List[object]]::new()
    $position = 0
    while ($position -lt $Text.Length) {
        $character = $Text[$position]
        if ([char]::IsWhiteSpace($character) -or $character -eq [char]0xfeff) {
            $position++
            continue
        }
        if ($character -eq '/' -and $position + 1 -lt $Text.Length -and $Text[$position + 1] -eq '/') {
            $position += 2
            while ($position -lt $Text.Length -and $Text[$position] -notin @("`r", "`n")) {
                $position++
            }
            continue
        }
        if ($character -eq '{') {
            [void]$tokens.Add([pscustomobject]@{ Kind = 'OpenBrace'; Value = $null })
            $position++
            continue
        }
        if ($character -eq '}') {
            [void]$tokens.Add([pscustomobject]@{ Kind = 'CloseBrace'; Value = $null })
            $position++
            continue
        }
        if ($character -ne '"') {
            throw "Appmanifest KeyValues syntax has an unexpected character at offset $position."
        }

        $position++
        $rawValueStart = $position
        $value = [Text.StringBuilder]::new()
        $terminated = $false
        while ($position -lt $Text.Length) {
            $character = $Text[$position]
            if ($character -eq '"') {
                $rawValueLength = $position - $rawValueStart
                $position++
                $terminated = $true
                break
            }
            if ($character -in @("`r", "`n")) {
                throw "Appmanifest KeyValues string contains an unescaped line break at offset $position."
            }
            if ($character -eq '\' -and $position + 1 -lt $Text.Length) {
                $escaped = $Text[$position + 1]
                switch ($escaped) {
                    '"' { [void]$value.Append('"'); $position += 2; continue }
                    '\' { [void]$value.Append('\'); $position += 2; continue }
                    'n' { [void]$value.Append("`n"); $position += 2; continue }
                    'r' { [void]$value.Append("`r"); $position += 2; continue }
                    't' { [void]$value.Append("`t"); $position += 2; continue }
                }
            }
            [void]$value.Append($character)
            $position++
        }
        if (-not $terminated) {
            throw 'Appmanifest KeyValues string is not terminated.'
        }
        [void]$tokens.Add([pscustomobject]@{
            Kind = 'String'
            Value = $value.ToString()
            RawValueStart = $rawValueStart
            RawValueLength = $rawValueLength
        })
    }
    return @($tokens)
}

function Read-KeyValuesMap {
    param(
        [Parameter(Mandatory)][object[]]$Tokens,
        [Parameter(Mandatory)][ref]$Position,
        [switch]$RequireClosingBrace
    )

    $map = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    while ($Position.Value -lt $Tokens.Count) {
        $token = $Tokens[$Position.Value]
        if ([string]$token.Kind -ceq 'CloseBrace') {
            if (-not $RequireClosingBrace) {
                throw 'Appmanifest KeyValues syntax has an unmatched closing brace.'
            }
            $Position.Value++
            return ,$map
        }
        if ([string]$token.Kind -cne 'String') {
            throw "Appmanifest KeyValues syntax expected a quoted key at token $($Position.Value)."
        }
        $key = [string]$token.Value
        if ($map.ContainsKey($key)) {
            throw "Appmanifest KeyValues contains a duplicate key: $key"
        }
        $Position.Value++
        if ($Position.Value -ge $Tokens.Count) {
            throw "Appmanifest KeyValues key has no value: $key"
        }

        $valueToken = $Tokens[$Position.Value]
        if ([string]$valueToken.Kind -ceq 'String') {
            $map.Add($key, $valueToken)
            $Position.Value++
            continue
        }
        if ([string]$valueToken.Kind -ceq 'OpenBrace') {
            $Position.Value++
            $value = Read-KeyValuesMap -Tokens $Tokens -Position $Position -RequireClosingBrace
            $map.Add($key, $value)
            continue
        }
        throw "Appmanifest KeyValues key has an invalid value: $key"
    }
    if ($RequireClosingBrace) {
        throw 'Appmanifest KeyValues object is missing a closing brace.'
    }
    return ,$map
}

function Get-RequiredKeyValuesEntry {
    param(
        [Parameter(Mandatory)][Collections.Generic.Dictionary[string, object]]$Map,
        [Parameter(Mandatory)][string]$Key,
        [Parameter(Mandatory)][string]$Context
    )

    foreach ($entry in $Map.GetEnumerator()) {
        if ([string]::Equals([string]$entry.Key, $Key, [StringComparison]::OrdinalIgnoreCase)) {
            if (-not [string]::Equals([string]$entry.Key, $Key, [StringComparison]::Ordinal)) {
                throw "Appmanifest $Context key has incorrect casing: expected $Key, found $($entry.Key)."
            }
            return ,$entry.Value
        }
    }
    throw "Appmanifest $Context is missing required key $Key."
}

function Get-RequiredKeyValuesStringToken {
    param(
        [Parameter(Mandatory)][Collections.Generic.Dictionary[string, object]]$Map,
        [Parameter(Mandatory)][string]$Key,
        [Parameter(Mandatory)][string]$Context
    )

    $entry = Get-RequiredKeyValuesEntry $Map $Key $Context
    if ($entry -isnot [System.Management.Automation.PSCustomObject] -or [string]$entry.Kind -cne 'String') {
        throw "Appmanifest $Context must contain a string value named $Key."
    }
    return $entry
}

function Get-RequiredKeyValuesMap {
    param(
        [Parameter(Mandatory)][Collections.Generic.Dictionary[string, object]]$Map,
        [Parameter(Mandatory)][string]$Key,
        [Parameter(Mandatory)][string]$Context
    )

    $entry = Get-RequiredKeyValuesEntry $Map $Key $Context
    if ($entry -isnot [Collections.Generic.Dictionary[string, object]]) {
        throw "Appmanifest $Context must contain an object named $Key."
    }
    return ,$entry
}

function Get-AppManifestPathAppId {
    param([Parameter(Mandatory)][string]$Path)

    $match = [Text.RegularExpressions.Regex]::Match(
        [IO.Path]::GetFileName($Path),
        '^appmanifest_([0-9]+)\.acf$',
        [Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [Text.RegularExpressions.RegexOptions]::CultureInvariant
    )
    if (-not $match.Success) {
        throw "Appmanifest filename must identify its appid as appmanifest_<appid>.acf: $Path"
    }
    return $match.Groups[1].Value
}

function Get-AppManifestEvidence {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Appmanifest is missing: $Path"
    }
    try {
        $bytes = [IO.File]::ReadAllBytes($Path)
    } catch {
        throw "Appmanifest cannot be read: $Path. $($_.Exception.Message)"
    }
    $bytePreservingText = [Text.Encoding]::Latin1.GetString($bytes)
    $tokens = @(Get-KeyValuesTokenList $bytePreservingText)
    $position = 0
    $root = Read-KeyValuesMap -Tokens $tokens -Position ([ref]$position)
    if ($position -ne $tokens.Count) {
        throw 'Appmanifest KeyValues syntax contains trailing tokens.'
    }
    if ($root.Count -ne 1) {
        throw 'Appmanifest must contain exactly one AppState root object.'
    }
    $appState = Get-RequiredKeyValuesMap $root 'AppState' 'root'
    $appIdToken = Get-RequiredKeyValuesStringToken $appState 'appid' 'AppState'
    $buildIdToken = Get-RequiredKeyValuesStringToken $appState 'buildid' 'AppState'
    $lastPlayedToken = Get-RequiredKeyValuesStringToken $appState 'LastPlayed' 'AppState'
    $appId = [string]$appIdToken.Value
    $buildId = [string]$buildIdToken.Value
    $lastPlayed = [string]$lastPlayedToken.Value
    if ($appId -cnotmatch '^[0-9]+$' -or $buildId -cnotmatch '^[0-9]+$' -or $lastPlayed -cnotmatch '^[0-9]+$') {
        throw 'Appmanifest appid, buildid, and LastPlayed must contain decimal digits only.'
    }
    $rawLastPlayed = $bytePreservingText.Substring([int]$lastPlayedToken.RawValueStart, [int]$lastPlayedToken.RawValueLength)
    if (-not [string]::Equals($rawLastPlayed, $lastPlayed, [StringComparison]::Ordinal)) {
        throw 'Appmanifest LastPlayed must be an unescaped decimal string.'
    }

    $installedDepots = Get-RequiredKeyValuesMap $appState 'InstalledDepots' 'AppState'
    if ($installedDepots.Count -eq 0) {
        throw 'Appmanifest InstalledDepots must not be empty.'
    }
    $depotManifests = [Collections.Generic.SortedDictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($entry in $installedDepots.GetEnumerator()) {
        $depotId = [string]$entry.Key
        if ($depotId -cnotmatch '^[0-9]+$' -or $entry.Value -isnot [Collections.Generic.Dictionary[string, object]]) {
            throw "Appmanifest InstalledDepots entry is invalid: $depotId"
        }
        $manifestToken = Get-RequiredKeyValuesStringToken $entry.Value 'manifest' "InstalledDepots[$depotId]"
        $manifestId = [string]$manifestToken.Value
        if ($manifestId -cnotmatch '^[0-9]+$') {
            throw "Appmanifest InstalledDepots[$depotId] manifest must contain decimal digits only."
        }
        $depotManifests.Add($depotId, $manifestId)
    }
    $normalizedText = $bytePreservingText.Substring(0, [int]$lastPlayedToken.RawValueStart) + '0' +
        $bytePreservingText.Substring([int]$lastPlayedToken.RawValueStart + [int]$lastPlayedToken.RawValueLength)
    $normalizedBytes = [Text.Encoding]::Latin1.GetBytes($normalizedText)
    return [pscustomobject]@{
        AppId = $appId
        BuildId = $buildId
        DepotManifests = $depotManifests
        RawSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
        NormalizedSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($normalizedBytes)).ToLowerInvariant()
    }
}

function Assert-AppManifestCoreIdentity {
    param(
        [Parameter(Mandatory)]$Identity,
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ExpectedAppId,
        [Parameter(Mandatory)][string]$ExpectedBuildId,
        [Parameter(Mandatory)][string]$ExpectedManifestId
    )

    $pathAppId = Get-AppManifestPathAppId $Path
    if (-not [string]::Equals($pathAppId, $ExpectedAppId, [StringComparison]::Ordinal)) {
        throw "Appmanifest path appid mismatch: expected $ExpectedAppId, found $pathAppId."
    }
    if (-not [string]::Equals([string]$Identity.AppId, $ExpectedAppId, [StringComparison]::Ordinal)) {
        throw "Appmanifest appid mismatch: expected $ExpectedAppId, found $($Identity.AppId)."
    }
    if (-not [string]::Equals([string]$Identity.BuildId, $ExpectedBuildId, [StringComparison]::Ordinal)) {
        throw "Appmanifest buildid mismatch: expected $ExpectedBuildId, found $($Identity.BuildId)."
    }
    $matchingDepots = @(Get-MapEntries $Identity.DepotManifests | Where-Object {
        [string]::Equals($_.Value, $ExpectedManifestId, [StringComparison]::Ordinal)
    })
    if ($matchingDepots.Count -ne 1) {
        throw "Appmanifest InstalledDepots must contain current manifest $ExpectedManifestId exactly once; found $($matchingDepots.Count)."
    }
}

function Get-MapEntries {
    param([Parameter(Mandatory)]$Map)

    if ($Map -is [System.Collections.IDictionary]) {
        return @($Map.GetEnumerator() | ForEach-Object {
            [pscustomobject]@{ Key = [string]$_.Key; Value = [string]$_.Value }
        })
    }
    return @($Map.PSObject.Properties | ForEach-Object {
        [pscustomobject]@{ Key = [string]$_.Name; Value = [string]$_.Value }
    })
}

function Get-OrdinalMapKeys {
    param([Parameter(Mandatory)]$Map)

    [string[]]$keys = @(Get-MapEntries $Map | ForEach-Object { $_.Key })
    [Array]::Sort($keys, [StringComparer]::Ordinal)
    return $keys
}

function Get-MapValueOrdinal {
    param(
        [Parameter(Mandatory)]$Map,
        [Parameter(Mandatory)][string]$Key
    )

    foreach ($entry in @(Get-MapEntries $Map)) {
        if ([string]::Equals($entry.Key, $Key, [StringComparison]::Ordinal)) {
            return [string]$entry.Value
        }
    }
    throw "Hash map key is missing: $Key"
}

function ConvertTo-CanonicalNativePath {
    param(
        [Parameter(Mandatory)][string]$Path,
        [string]$Label = 'Native hash map'
    )

    $normalized = $Path.Replace('\', '/')
    if (
        [string]::IsNullOrWhiteSpace($normalized) -or
        $normalized.StartsWith('/', [StringComparison]::Ordinal) -or
        $normalized.EndsWith('/', [StringComparison]::Ordinal) -or
        $normalized -match '^[A-Za-z]:' -or
        $normalized.Contains('//', [StringComparison]::Ordinal)
    ) {
        throw "$Label contains a non-canonical path: $Path"
    }
    foreach ($segment in $normalized.Split('/')) {
        if ($segment.Length -eq 0 -or $segment -eq '.' -or $segment -eq '..') {
            throw "$Label contains a non-canonical path: $Path"
        }
    }
    return $normalized
}

function ConvertTo-ExpectedHashMap {
    param(
        [Parameter(Mandatory)][string[]]$Entries,
        [Parameter(Mandatory)][string]$Label
    )

    if ($Entries.Count -eq 0) {
        throw "$Label must contain at least one path=lowercase-sha256 entry."
    }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $parsed = [Collections.Generic.List[object]]::new()
    foreach ($entry in $Entries) {
        $separator = $entry.IndexOf('=')
        if ($separator -le 0 -or $separator -eq $entry.Length - 1) {
            throw "$Label entry must be relative-path=lowercase-sha256: $entry"
        }
        $relative = ConvertTo-CanonicalNativePath $entry.Substring(0, $separator) $Label
        $hash = $entry.Substring($separator + 1)
        if ($hash -cnotmatch '^[0-9a-f]{64}$') {
            throw "$Label must use lowercase 64-hex SHA-256: $entry"
        }
        if (-not $seen.Add($relative)) {
            throw "$Label contains a duplicate normalized path: $relative"
        }
        $parsed.Add([pscustomobject]@{ Path = $relative; Hash = $hash })
    }

    $result = [ordered]@{}
    [string[]]$paths = @($parsed | ForEach-Object { $_.Path })
    [Array]::Sort($paths, [StringComparer]::Ordinal)
    foreach ($path in $paths) {
        $item = @($parsed | Where-Object { [string]::Equals($_.Path, $path, [StringComparison]::Ordinal) })[0]
        $result[$path] = $item.Hash
    }
    return $result
}

function Get-NativeHashMap {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)]$Template
    )

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        throw "Game tree is missing: $Root"
    }
    $result = [ordered]@{}
    foreach ($key in @(Get-OrdinalMapKeys $Template)) {
        $relative = ConvertTo-CanonicalNativePath $key
        $result[$relative] = Get-Sha256 (Join-Path $Root $relative)
    }
    return $result
}

function Get-HashMapFingerprint {
    param([Parameter(Mandatory)]$Map)

    $lines = @(Get-OrdinalMapKeys $Map | ForEach-Object {
        "$_`t$((Get-MapValueOrdinal $Map $_).ToLowerInvariant())"
    })
    $vector = $lines -join "`n"
    $bytes = [Text.Encoding]::UTF8.GetBytes($vector)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Test-HashMapsEqual {
    param($Left, $Right)

    $leftKeys = @(Get-OrdinalMapKeys $Left)
    $rightKeys = @(Get-OrdinalMapKeys $Right)
    if ($leftKeys.Count -ne $rightKeys.Count) {
        return $false
    }
    for ($index = 0; $index -lt $leftKeys.Count; $index++) {
        if (
            -not [string]::Equals($leftKeys[$index], $rightKeys[$index], [StringComparison]::Ordinal) -or
            -not [string]::Equals((Get-MapValueOrdinal $Left $leftKeys[$index]), (Get-MapValueOrdinal $Right $rightKeys[$index]), [StringComparison]::Ordinal)
        ) {
            return $false
        }
    }
    return $true
}

function Assert-NativeIdentity {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)]$ExpectedMap,
        [Parameter(Mandatory)][string]$ExpectedFingerprint,
        [Parameter(Mandatory)][string]$Label
    )

    $actual = Get-NativeHashMap -Root $Root -Template $ExpectedMap
    $actualFingerprint = Get-HashMapFingerprint $actual
    if (-not (Test-HashMapsEqual $actual $ExpectedMap) -or -not [string]::Equals($actualFingerprint, $ExpectedFingerprint, [StringComparison]::Ordinal)) {
        throw "$Label hash mismatch at $Root."
    }
}

function Get-DirectoryFingerprint {
    param([Parameter(Mandatory)][string]$Root)

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        throw "Directory is missing: $Root"
    }
    $rootItem = Get-Item -LiteralPath $Root -Force
    if ($rootItem.LinkType -or (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw "Directory fingerprints do not follow links: $Root"
    }
    $fileMap = [Collections.Generic.SortedDictionary[string, string]]::new([StringComparer]::Ordinal)
    Get-ChildItem -LiteralPath $Root -Force -Recurse | ForEach-Object {
        if ($_.LinkType -or (($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw "Directory fingerprints do not follow links: $($_.FullName)"
        }
        if (-not $_.PSIsContainer) {
            $relative = [IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\', '/')
            $fileMap[$relative] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    return Get-HashMapFingerprint $fileMap
}

function Assert-SaveIdentity {
    param(
        [Parameter(Mandatory)][string]$Path,
        [AllowNull()][string]$ExpectedFingerprint,
        [Parameter(Mandatory)][string]$Label
    )

    if ([string]::IsNullOrEmpty($ExpectedFingerprint)) {
        if (Test-Path -LiteralPath $Path) {
            throw "$Label was originally absent but now exists: $Path"
        }
        return
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "$Label is missing: $Path"
    }
    $actual = Get-DirectoryFingerprint $Path
    if (-not [string]::Equals($actual, $ExpectedFingerprint, [StringComparison]::Ordinal)) {
        throw "$Label hash mismatch at $Path."
    }
}

function Assert-ExactStateFieldSet {
    param(
        [Parameter(Mandatory)]$State,
        [Parameter(Mandatory)][string[]]$ExpectedFields
    )
    $actualFields = if ($State -is [System.Collections.IDictionary]) {
        @($State.Keys | ForEach-Object { [string]$_ })
    } else {
        @($State.PSObject.Properties.Name)
    }
    if ($actualFields.Count -ne $ExpectedFields.Count) {
        throw 'Game-switch state schema has missing or extra fields.'
    }
    foreach ($field in $ExpectedFields) {
        if (-not ($actualFields -ccontains $field)) {
            throw "Game-switch state schema is missing or has an incorrectly cased field: $field"
        }
    }
}

function Assert-StringStateFieldSet {
    param(
        [Parameter(Mandatory)]$State,
        [Parameter(Mandatory)][string[]]$Fields
    )

    foreach ($field in $Fields) {
        if ($State.$field -isnot [string]) {
            throw "Game-switch state $field must be a JSON string."
        }
    }
}

function Assert-StringMap {
    param(
        [Parameter(Mandatory)]$Map,
        [Parameter(Mandatory)][string]$Label
    )

    if ($Map -is [System.Collections.IDictionary]) {
        foreach ($entry in $Map.GetEnumerator()) {
            if ($entry.Key -isnot [string] -or $entry.Value -isnot [string]) {
                throw "$Label keys and values must be JSON strings."
            }
        }
        return
    }
    if ($Map -is [System.Management.Automation.PSCustomObject]) {
        foreach ($property in $Map.PSObject.Properties) {
            if ($property.Value -isnot [string]) {
                throw "$Label keys and values must be JSON strings."
            }
        }
        return
    }
    throw "$Label must be a JSON object with string values."
}

function Assert-StateCommon {
    param([Parameter(Mandatory)]$State)

    Assert-StringStateFieldSet $State @(
        'attemptId',
        'phase',
        'oldBuildId',
        'currentBuildId',
        'oldManifestId',
        'currentManifestId',
        'oldFingerprint',
        'currentFingerprint',
        'canonical',
        'saveDirectory',
        'appManifestPath',
        'downloadedOldDepot',
        'originalGameDirectory',
        'oldSmokeGameDirectory',
        'currentSmokeGameDirectory',
        'originalSaveDirectory',
        'oldSmokeSaveDirectory',
        'currentSmokeSaveDirectory'
    )
    if ($State.saveWasPresent -isnot [bool]) {
        throw 'Game-switch state saveWasPresent must be a JSON boolean.'
    }
    if ($null -ne $State.originalSaveFingerprint -and $State.originalSaveFingerprint -isnot [string]) {
        throw 'Game-switch state originalSaveFingerprint must be null or a JSON string.'
    }
    Assert-StringMap $State.oldHashes 'Game-switch state oldHashes'
    Assert-StringMap $State.currentHashes 'Game-switch state currentHashes'

    if ($State.attemptId -cnotmatch '^[0-9a-f]{32}$') {
        throw 'Game-switch state attemptId is invalid.'
    }
    if (-not ($script:KnownPhases -ccontains $State.phase)) {
        throw "Game-switch state phase is invalid: $($State.phase)"
    }
    foreach ($field in 'oldBuildId', 'currentBuildId', 'oldManifestId', 'currentManifestId') {
        if ($State.$field -cnotmatch '^[0-9]+$') {
            throw "Game-switch state $field is invalid."
        }
    }
    foreach ($field in 'oldFingerprint', 'currentFingerprint') {
        if ($State.$field -cnotmatch '^[0-9a-f]{64}$') {
            throw "Game-switch state $field is not lowercase 64-hex."
        }
    }
    if ($null -ne $State.originalSaveFingerprint -and $State.originalSaveFingerprint -cnotmatch '^[0-9a-f]{64}$') {
        throw 'Game-switch state originalSaveFingerprint is invalid.'
    }
    if ($State.saveWasPresent -ne ($null -ne $State.originalSaveFingerprint)) {
        throw 'Game-switch state save presence and fingerprint disagree.'
    }
    $oldEntries = @(Get-MapEntries $State.oldHashes)
    $currentEntries = @(Get-MapEntries $State.currentHashes)
    if ($oldEntries.Count -eq 0 -or $oldEntries.Count -ne $currentEntries.Count) {
        throw 'Game-switch state native hash maps are empty or have different paths.'
    }
    foreach ($entry in @($oldEntries + $currentEntries)) {
        if ($entry.Key -cne (ConvertTo-CanonicalNativePath $entry.Key) -or $entry.Value -cnotmatch '^[0-9a-f]{64}$') {
            throw 'Game-switch state native hash map is not canonical.'
        }
    }
    $oldKeys = @(Get-OrdinalMapKeys $State.oldHashes)
    $currentKeys = @(Get-OrdinalMapKeys $State.currentHashes)
    if (($oldKeys -join "`n") -cne ($currentKeys -join "`n")) {
        throw 'Game-switch state native hash maps do not contain the same closed paths.'
    }
    if ((Get-HashMapFingerprint $State.oldHashes) -cne [string]$State.oldFingerprint -or (Get-HashMapFingerprint $State.currentHashes) -cne [string]$State.currentFingerprint) {
        throw 'Game-switch state fingerprint does not match its native hash map.'
    }

    foreach ($field in 'canonical', 'saveDirectory', 'appManifestPath', 'downloadedOldDepot', 'originalGameDirectory', 'oldSmokeGameDirectory', 'currentSmokeGameDirectory', 'originalSaveDirectory', 'oldSmokeSaveDirectory', 'currentSmokeSaveDirectory') {
        $value = [string]$State.$field
        if ($value -cne (Get-NormalizedAbsolutePath $value)) {
            throw "Game-switch state path is not normalized: $field"
        }
    }
    $attempt = [string]$State.attemptId
    if (
        [string]$State.originalGameDirectory -cne "$($State.canonical).modkit-$attempt-original" -or
        [string]$State.oldSmokeGameDirectory -cne "$($State.canonical).modkit-$attempt-old-smoke" -or
        [string]$State.currentSmokeGameDirectory -cne "$($State.canonical).modkit-$attempt-current-smoke" -or
        [string]$State.originalSaveDirectory -cne "$($State.saveDirectory).modkit-$attempt-original" -or
        [string]$State.oldSmokeSaveDirectory -cne "$($State.saveDirectory).modkit-$attempt-old-smoke" -or
        [string]$State.currentSmokeSaveDirectory -cne "$($State.saveDirectory).modkit-$attempt-current-smoke"
    ) {
        throw 'Game-switch state attempt-qualified paths do not match attemptId.'
    }
    Assert-InputPathTopology ([string]$State.canonical) ([string]$State.saveDirectory) ([string]$State.downloadedOldDepot) ([string]$State.appManifestPath)
    Assert-NoExistingAncestorLink ([string]$State.appManifestPath) 'Game-switch state appManifestPath'
    foreach ($field in 'canonical', 'saveDirectory', 'downloadedOldDepot', 'originalGameDirectory', 'oldSmokeGameDirectory', 'currentSmokeGameDirectory', 'originalSaveDirectory', 'oldSmokeSaveDirectory', 'currentSmokeSaveDirectory') {
        Assert-ExistingDirectoryRootIsNotLink ([string]$State.$field) "Game-switch state $field"
    }
}

function Assert-LegacyClosedState {
    param([Parameter(Mandatory)]$State)

    Assert-ExactStateFieldSet $State $script:LegacyStateFields
    if ($State.schemaVersion -isnot [long] -or $State.schemaVersion -ne 1) {
        throw "Unsupported legacy game-switch state schema: $($State.schemaVersion)"
    }
    Assert-StringStateFieldSet $State @('appManifestSha256')
    if ($State.appManifestSha256 -cnotmatch '^[0-9a-f]{64}$') {
        throw 'Legacy game-switch state appManifestSha256 is not lowercase 64-hex.'
    }
    Assert-StateCommon $State
}

function Assert-ClosedState {
    param([Parameter(Mandatory)]$State)

    Assert-ExactStateFieldSet $State $script:StateFields
    if ($State.schemaVersion -isnot [long] -or $State.schemaVersion -ne 2) {
        throw "Unsupported game-switch state schema: $($State.schemaVersion)"
    }
    Assert-StringStateFieldSet $State @('appManifestAppId', 'appManifestNormalizedSha256')
    Assert-StringMap $State.currentDepotManifests 'Game-switch state currentDepotManifests'
    if ($State.appManifestAppId -cnotmatch '^[0-9]+$') {
        throw 'Game-switch state appManifestAppId is invalid.'
    }
    if ($State.appManifestNormalizedSha256 -cnotmatch '^[0-9a-f]{64}$') {
        throw 'Game-switch state appManifestNormalizedSha256 is not lowercase 64-hex.'
    }
    $pathAppId = Get-AppManifestPathAppId ([string]$State.appManifestPath)
    if (-not [string]::Equals($pathAppId, [string]$State.appManifestAppId, [StringComparison]::Ordinal)) {
        throw 'Game-switch state appManifestAppId does not match appManifestPath.'
    }
    $depotEntries = @(Get-MapEntries $State.currentDepotManifests)
    if ($depotEntries.Count -eq 0) {
        throw 'Game-switch state currentDepotManifests is empty.'
    }
    foreach ($entry in $depotEntries) {
        if ($entry.Key -cnotmatch '^[0-9]+$' -or $entry.Value -cnotmatch '^[0-9]+$') {
            throw 'Game-switch state currentDepotManifests is invalid.'
        }
    }
    $matchingDepots = @($depotEntries | Where-Object {
        [string]::Equals($_.Value, [string]$State.currentManifestId, [StringComparison]::Ordinal)
    })
    if ($matchingDepots.Count -ne 1) {
        throw 'Game-switch state currentManifestId must occur exactly once in currentDepotManifests.'
    }
    Assert-StateCommon $State
}

function ConvertFrom-LegacyState {
    param([Parameter(Mandatory)]$State)

    Assert-LegacyClosedState $State
    $evidence = Get-AppManifestEvidence ([string]$State.appManifestPath)
    if (-not [string]::Equals([string]$evidence.RawSha256, [string]$State.appManifestSha256, [StringComparison]::Ordinal)) {
        throw 'Legacy appmanifest hash mismatch; schema 1 cannot prove that only LastPlayed changed, so this state cannot be migrated safely.'
    }
    $appId = Get-AppManifestPathAppId ([string]$State.appManifestPath)
    Assert-AppManifestCoreIdentity $evidence ([string]$State.appManifestPath) $appId ([string]$State.currentBuildId) ([string]$State.currentManifestId)

    $migrated = [ordered]@{
        schemaVersion = [long]2
        attemptId = [string]$State.attemptId
        phase = [string]$State.phase
        canonical = [string]$State.canonical
        saveDirectory = [string]$State.saveDirectory
        appManifestPath = [string]$State.appManifestPath
        downloadedOldDepot = [string]$State.downloadedOldDepot
        originalGameDirectory = [string]$State.originalGameDirectory
        oldSmokeGameDirectory = [string]$State.oldSmokeGameDirectory
        currentSmokeGameDirectory = [string]$State.currentSmokeGameDirectory
        originalSaveDirectory = [string]$State.originalSaveDirectory
        oldSmokeSaveDirectory = [string]$State.oldSmokeSaveDirectory
        currentSmokeSaveDirectory = [string]$State.currentSmokeSaveDirectory
        saveWasPresent = [bool]$State.saveWasPresent
        originalSaveFingerprint = $State.originalSaveFingerprint
        appManifestAppId = $appId
        appManifestNormalizedSha256 = [string]$evidence.NormalizedSha256
        currentDepotManifests = $evidence.DepotManifests
        oldBuildId = [string]$State.oldBuildId
        currentBuildId = [string]$State.currentBuildId
        oldManifestId = [string]$State.oldManifestId
        currentManifestId = [string]$State.currentManifestId
        oldHashes = $State.oldHashes
        currentHashes = $State.currentHashes
        oldFingerprint = [string]$State.oldFingerprint
        currentFingerprint = [string]$State.currentFingerprint
    }
    $current = [pscustomobject]$migrated
    Assert-ClosedState $current
    return $current
}

function Get-StateTemporaryPath {
    param($State)
    return "$StatePath.$($State.attemptId).preparing"
}

function Test-AllowedTransition {
    param([string]$From, [string]$To)

    if ([string]::Equals($From, $To, [StringComparison]::Ordinal)) {
        return $true
    }
    $allowed = @{
        'prepared' = @('original-game-move-pending', 'restore-original-game-restored')
        'original-game-move-pending' = @('original-game-preserved')
        'original-game-preserved' = @('original-save-move-pending', 'originals-preserved', 'restore-no-active-game')
        'original-save-move-pending' = @('originals-preserved')
        'originals-preserved' = @('old-copy-preparing', 'restore-no-active-game')
        'old-copy-preparing' = @('old-copy-prepared')
        'old-copy-prepared' = @('old-activate-pending', 'restore-no-active-game')
        'old-activate-pending' = @('old-active')
        'old-active' = @('old-deactivate-pending', 'restore-old-game-pending')
        'old-deactivate-pending' = @('old-game-preserved')
        'old-game-preserved' = @('old-save-move-pending', 'restore-old-game-preserved')
        'old-save-move-pending' = @('old-save-preserved')
        'old-save-preserved' = @('current-copy-preparing', 'restore-save-preserved')
        'current-copy-preparing' = @('current-copy-prepared')
        'current-copy-prepared' = @('current-activate-pending', 'restore-save-preserved')
        'current-activate-pending' = @('current-smoke-active')
        'current-smoke-active' = @('restore-current-game-pending')
        'restore-old-game-pending' = @('restore-old-game-preserved')
        'restore-current-game-pending' = @('restore-current-game-preserved')
        'restore-no-active-game' = @('restore-save-preserved')
        'restore-old-game-preserved' = @('restore-old-save-pending')
        'restore-current-game-preserved' = @('restore-current-save-pending')
        'restore-old-save-pending' = @('restore-save-preserved')
        'restore-current-save-pending' = @('restore-save-preserved')
        'restore-save-preserved' = @('restore-original-game-pending', 'restore-original-game-restored')
        'restore-original-game-pending' = @('restore-original-game-restored')
        'restore-original-game-restored' = @('restore-original-save-pending', 'restored')
        'restore-original-save-pending' = @('restored')
        'restored' = @()
    }
    return $allowed.ContainsKey($From) -and ($allowed[$From] -ccontains $To)
}

function Assert-StateImmutableMatch {
    param($Current, $Candidate)

    foreach ($field in @($script:StateFields | Where-Object { $_ -cne 'phase' })) {
        $left = ConvertTo-Json -InputObject $Current.$field -Depth 10 -Compress
        $right = ConvertTo-Json -InputObject $Candidate.$field -Depth 10 -Compress
        if (-not [string]::Equals($left, $right, [StringComparison]::Ordinal)) {
            throw "Ambiguous game-switch journal temporary changes immutable field: $field"
        }
    }
}

function Assert-NoDuplicateJsonProperty {
    param(
        [Parameter(Mandatory)][Text.Json.JsonElement]$Element,
        [Parameter(Mandatory)][string]$JsonPath
    )

    if ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Object) {
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($property in $Element.EnumerateObject()) {
            if (-not $names.Add($property.Name)) {
                throw "Game-switch state JSON contains duplicate property '$($property.Name)' at $JsonPath."
            }
            Assert-NoDuplicateJsonProperty $property.Value "$JsonPath.$($property.Name)"
        }
        return
    }
    if ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Array) {
        $index = 0
        foreach ($item in $Element.EnumerateArray()) {
            Assert-NoDuplicateJsonProperty $item "$JsonPath[$index]"
            $index++
        }
    }
}

function Read-StateDocument {
    param([Parameter(Mandatory)][string]$Path)

    $stateItem = Get-Item -LiteralPath $Path -Force
    if ($stateItem.LinkType -or (($stateItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw "Game-switch state must not be a symlink or reparse point: $Path"
    }
    $json = [IO.File]::ReadAllText($Path)
    try {
        $document = [Text.Json.JsonDocument]::Parse($json)
    } catch {
        throw "Game-switch state is not valid JSON: $Path. $($_.Exception.Message)"
    }
    try {
        if ($document.RootElement.ValueKind -ne [Text.Json.JsonValueKind]::Object) {
            throw "Game-switch state JSON root must be an object: $Path"
        }
        Assert-NoDuplicateJsonProperty $document.RootElement '$'
    } finally {
        $document.Dispose()
    }
    try {
        $state = $json | ConvertFrom-Json
    } catch {
        throw "Game-switch state cannot be materialized from JSON: $Path. $($_.Exception.Message)"
    }
    $schemaProperties = @($state.PSObject.Properties | Where-Object { $_.Name -ceq 'schemaVersion' })
    if ($schemaProperties.Count -ne 1 -or $schemaProperties[0].Value -isnot [long]) {
        throw 'Game-switch state schemaVersion is missing or is not an integer.'
    }
    switch ([long]$schemaProperties[0].Value) {
        1 {
            return [pscustomobject]@{
                State = ConvertFrom-LegacyState $state
                WasLegacy = $true
            }
        }
        2 {
            Assert-ClosedState $state
            return [pscustomobject]@{
                State = $state
                WasLegacy = $false
            }
        }
        default { throw "Unsupported game-switch state schema: $($schemaProperties[0].Value)" }
    }
}

function Get-JournalTemporaryFiles {
    $parent = Split-Path -Parent $StatePath
    $leaf = Split-Path -Leaf $StatePath
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        return @()
    }
    return @(Get-ChildItem -LiteralPath $parent -File -Force | Where-Object {
        $_.Name.StartsWith("$leaf.", [StringComparison]::Ordinal) -and $_.Name.EndsWith('.preparing', [StringComparison]::Ordinal)
    })
}

function Complete-LegacyStateMigration {
    param([Parameter(Mandatory)]$Document)

    $state = $Document.State
    if ([bool]$Document.WasLegacy -and [string]$state.phase -cne 'restored') {
        Write-StatePhase $state ([string]$state.phase)
    }
    return $state
}

function Read-StateWithRecovery {
    $temporaryFiles = @(Get-JournalTemporaryFiles)
    if ($temporaryFiles.Count -gt 1) {
        throw "Ambiguous game-switch journal: multiple preparing files exist for $StatePath."
    }
    $stateExists = Test-Path -LiteralPath $StatePath -PathType Leaf
    if (-not $stateExists -and $temporaryFiles.Count -eq 0) {
        return $null
    }
    if (-not $stateExists) {
        $candidateDocument = Read-StateDocument $temporaryFiles[0].FullName
        if ([string]$candidateDocument.State.phase -cne 'prepared') {
            throw 'Ambiguous initial game-switch journal temporary is not in prepared phase.'
        }
        [IO.File]::Move($temporaryFiles[0].FullName, $StatePath, $false)
        return Complete-LegacyStateMigration $candidateDocument
    }

    $stateDocument = Read-StateDocument $StatePath
    if ($temporaryFiles.Count -eq 1) {
        $candidateDocument = Read-StateDocument $temporaryFiles[0].FullName
        Assert-StateImmutableMatch $stateDocument.State $candidateDocument.State
        if (-not (Test-AllowedTransition ([string]$stateDocument.State.phase) ([string]$candidateDocument.State.phase))) {
            throw "Ambiguous game-switch journal transition: $($stateDocument.State.phase) -> $($candidateDocument.State.phase)."
        }
        [IO.File]::Move($temporaryFiles[0].FullName, $StatePath, $true)
        $stateDocument = $candidateDocument
    }
    return Complete-LegacyStateMigration $stateDocument
}

function Write-StatePhase {
    param(
        [Parameter(Mandatory)]$State,
        [Parameter(Mandatory)][string]$Phase
    )

    if (-not ($script:KnownPhases -ccontains $Phase)) {
        throw "Cannot persist unknown game-switch phase: $Phase"
    }
    if (-not (Test-AllowedTransition ([string]$State.phase) $Phase)) {
        throw "Invalid game-switch journal transition: $($State.phase) -> $Phase"
    }
    $State.phase = $Phase
    Assert-ClosedState $State
    $temporary = Get-StateTemporaryPath $State
    if (Test-Path -LiteralPath $temporary) {
        throw "Ambiguous game-switch journal temporary already exists: $temporary"
    }
    $json = (ConvertTo-Json -InputObject $State -Depth 10 -Compress) + "`n"
    [IO.File]::WriteAllText($temporary, $json, [Text.UTF8Encoding]::new($false))
    Invoke-OperationPoint "before-journal-$Phase"
    [IO.File]::Move($temporary, $StatePath, (Test-Path -LiteralPath $StatePath))
    Invoke-OperationPoint "after-journal-$Phase"
}

function Write-InitialState {
    param([Parameter(Mandatory)]$State)

    Assert-ClosedState $State
    $parent = Split-Path -Parent $StatePath
    [IO.Directory]::CreateDirectory($parent) | Out-Null
    $temporary = Get-StateTemporaryPath $State
    if (Test-Path -LiteralPath $temporary) {
        throw "Ambiguous game-switch journal temporary already exists: $temporary"
    }
    $json = (ConvertTo-Json -InputObject $State -Depth 10 -Compress) + "`n"
    [IO.File]::WriteAllText($temporary, $json, [Text.UTF8Encoding]::new($false))
    Invoke-OperationPoint 'before-journal-prepared'
    [IO.File]::Move($temporary, $StatePath, $false)
    Invoke-OperationPoint 'after-journal-prepared'
}

function Assert-PathAbsent {
    param([string]$Path, [string]$Label)
    if (Test-Path -LiteralPath $Path) {
        throw "Ambiguous topology: $Label must be absent at $Path."
    }
}

function Assert-DirectoryPresent {
    param([string]$Path, [string]$Label)
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "Ambiguous topology: $Label directory is missing at $Path."
    }
}

function Complete-GameRename {
    param(
        [Parameter(Mandatory)]$State,
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)]$ExpectedHashes,
        [Parameter(Mandatory)][string]$ExpectedFingerprint,
        [Parameter(Mandatory)][string]$OperationName,
        [Parameter(Mandatory)][string]$NextPhase,
        [Parameter(Mandatory)][string]$Label
    )

    $sourceExists = Test-Path -LiteralPath $Source -PathType Container
    $destinationExists = Test-Path -LiteralPath $Destination -PathType Container
    if ($sourceExists -and -not $destinationExists) {
        Assert-NativeIdentity $Source $ExpectedHashes $ExpectedFingerprint $Label
        [IO.Directory]::Move($Source, $Destination)
        Invoke-OperationPoint "after-rename-$OperationName"
    } elseif (-not $sourceExists -and $destinationExists) {
        Assert-NativeIdentity $Destination $ExpectedHashes $ExpectedFingerprint $Label
    } else {
        throw "Ambiguous topology for ${OperationName}: source=$sourceExists destination=$destinationExists."
    }
    Write-StatePhase $State $NextPhase
}

function Complete-SaveRename {
    param(
        [Parameter(Mandatory)]$State,
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string]$OperationName,
        [Parameter(Mandatory)][string]$NextPhase,
        [AllowNull()][string]$ExpectedFingerprint,
        [switch]$AllowBothAbsent
    )

    $sourceExists = Test-Path -LiteralPath $Source -PathType Container
    $destinationExists = Test-Path -LiteralPath $Destination -PathType Container
    if ($sourceExists -and -not $destinationExists) {
        if (-not [string]::IsNullOrEmpty($ExpectedFingerprint)) {
            Assert-SaveIdentity $Source $ExpectedFingerprint 'Original save'
        }
        [IO.Directory]::Move($Source, $Destination)
        Invoke-OperationPoint "after-rename-$OperationName"
    } elseif (-not $sourceExists -and $destinationExists) {
        if (-not [string]::IsNullOrEmpty($ExpectedFingerprint)) {
            Assert-SaveIdentity $Destination $ExpectedFingerprint 'Original save'
        }
    } elseif (-not $sourceExists -and -not $destinationExists -and $AllowBothAbsent) {
        # A clean launch is allowed to create no save tree.
    } else {
        throw "Ambiguous topology for ${OperationName}: source=$sourceExists destination=$destinationExists."
    }
    Write-StatePhase $State $NextPhase
}

function Copy-TreePortable {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination,
        [switch]$ExcludeBepInEx
    )

    Assert-DirectoryTreeHasNoLinks $Source 'Smoke copy source'
    Assert-DirectoryTreeHasNoLinks $Destination 'Smoke copy destination'
    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    $items = @(Get-ChildItem -LiteralPath $Source -Force -Recurse)
    foreach ($item in $items) {
        if ($item.LinkType) {
            throw "Smoke copies do not follow links: $($item.FullName)"
        }
        $relative = [IO.Path]::GetRelativePath($Source, $item.FullName).Replace('\', '/')
        if ($ExcludeBepInEx -and ($relative -ceq 'BepInEx' -or $relative.StartsWith('BepInEx/', [StringComparison]::Ordinal))) {
            continue
        }
        $target = Join-Path $Destination $relative
        Assert-NoExistingAncestorLink -Path $target -Label 'Smoke copy destination'
        if ($item.PSIsContainer) {
            [IO.Directory]::CreateDirectory($target) | Out-Null
        } else {
            [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
            [IO.File]::Copy($item.FullName, $target, $true)
        }
    }
}

function Invoke-RobocopyChecked {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination,
        [string[]]$AdditionalArguments = @()
    )

    $previousPreference = $PSNativeCommandUseErrorActionPreference
    try {
        $PSNativeCommandUseErrorActionPreference = $false
        $null = & robocopy.exe $Source $Destination /E /COPY:DAT /DCOPY:DAT /R:2 /W:1 /XJ @AdditionalArguments
        $exitCode = $LASTEXITCODE
    } finally {
        $PSNativeCommandUseErrorActionPreference = $previousPreference
    }
    if ($exitCode -lt 0 -or $exitCode -gt 7) {
        throw "robocopy failed with exit code $exitCode while copying $Source to $Destination."
    }
}

function Copy-BaseTree {
    param([string]$Source, [string]$Destination, [string]$OperationName)

    Assert-DirectoryTreeHasNoLinks $Source "$OperationName base source"
    Assert-DirectoryTreeHasNoLinks $Destination "$OperationName base destination"
    if ($IsWindows) {
        Invoke-RobocopyChecked $Source $Destination @('/XD', (Join-Path $Source 'BepInEx'))
    } else {
        Copy-TreePortable $Source $Destination -ExcludeBepInEx
    }
    Invoke-OperationPoint "after-copy-$OperationName-base"
}

function Copy-LoaderAllowlist {
    param([string]$Source, [string]$Destination, [string]$OperationName)

    foreach ($entry in $script:LoaderAllowlist) {
        $sourcePath = Join-Path $Source $entry.Path
        Assert-NoExistingAncestorLink -Path $sourcePath -Label "Loader allowlist source $($entry.Path)"
        if (-not (Test-Path -LiteralPath $sourcePath)) {
            if ($entry.Required) {
                throw "Required loader allowlist entry is missing: $sourcePath"
            }
            continue
        }
        $destinationPath = Join-Path $Destination $entry.Path
        Assert-NoExistingAncestorLink -Path $destinationPath -Label "Loader allowlist destination $($entry.Path)"
        if (Test-Path -LiteralPath $sourcePath -PathType Container) {
            Assert-DirectoryTreeHasNoLinks $sourcePath "Loader allowlist source $($entry.Path)"
            Assert-DirectoryTreeHasNoLinks $destinationPath "Loader allowlist destination $($entry.Path)"
            if ($IsWindows) {
                Invoke-RobocopyChecked $sourcePath $destinationPath
            } else {
                Copy-TreePortable $sourcePath $destinationPath
            }
            Assert-DirectoryTreeHasNoLinks $destinationPath "Loader allowlist destination $($entry.Path)"
        } else {
            [IO.Directory]::CreateDirectory((Split-Path -Parent $destinationPath)) | Out-Null
            [IO.File]::Copy($sourcePath, $destinationPath, $true)
        }
        $pointName = $entry.Path.Replace('/', '-')
        Invoke-OperationPoint "after-copy-$OperationName-loader-$pointName"
    }
}

function Assert-CleanSmokeLoader {
    param([string]$Path)

    $plugins = Join-Path $Path 'BepInEx/plugins'
    if (Test-Path -LiteralPath $plugins) {
        $pluginFiles = @(Get-ChildItem -LiteralPath $plugins -File -Force -Recurse)
        if ($pluginFiles.Count -ne 0) {
            throw "Smoke tree contains inherited plugins: $($pluginFiles.FullName -join ', ')"
        }
    }
    $config = Join-Path $Path 'BepInEx/config'
    if (Test-Path -LiteralPath $config) {
        $unexpected = @(Get-ChildItem -LiteralPath $config -File -Force -Recurse | Where-Object { $_.FullName -cne (Join-Path $config 'BepInEx.cfg') })
        if ($unexpected.Count -ne 0) {
            throw "Smoke tree contains inherited config: $($unexpected.FullName -join ', ')"
        }
    }
}

function Copy-SmokeTree {
    param($State, [ValidateSet('old', 'current')][string]$Build)

    if ($Build -ceq 'old') {
        $source = [string]$State.downloadedOldDepot
        $destination = [string]$State.oldSmokeGameDirectory
        $hashes = $State.oldHashes
        $fingerprint = [string]$State.oldFingerprint
    } else {
        $source = [string]$State.originalGameDirectory
        $destination = [string]$State.currentSmokeGameDirectory
        $hashes = $State.currentHashes
        $fingerprint = [string]$State.currentFingerprint
    }
    Assert-DirectoryTreeHasNoLinks $destination "$Build smoke game"
    Copy-BaseTree $source $destination $Build
    Copy-LoaderAllowlist ([string]$State.originalGameDirectory) $destination $Build
    Assert-DirectoryTreeHasNoLinks $destination "$Build smoke game"
    Assert-RequiredLoaderFileSet $destination "$Build smoke game"
    Assert-CleanSmokeLoader $destination
    Assert-NativeIdentity $destination $hashes $fingerprint "$Build smoke game"
}

function Assert-StaticEvidence {
    param($State)

    $manifestEvidence = Get-AppManifestEvidence ([string]$State.appManifestPath)
    Assert-AppManifestCoreIdentity $manifestEvidence ([string]$State.appManifestPath) ([string]$State.appManifestAppId) ([string]$State.currentBuildId) ([string]$State.currentManifestId)
    if (-not (Test-HashMapsEqual $manifestEvidence.DepotManifests $State.currentDepotManifests)) {
        throw 'Appmanifest InstalledDepots identity mismatch.'
    }
    if (-not [string]::Equals([string]$manifestEvidence.NormalizedSha256, [string]$State.appManifestNormalizedSha256, [StringComparison]::Ordinal)) {
        throw 'Appmanifest content changed outside the permitted decimal LastPlayed value.'
    }
    Assert-NativeIdentity ([string]$State.downloadedOldDepot) $State.oldHashes ([string]$State.oldFingerprint) 'Downloaded old depot'

    if (Test-Path -LiteralPath $State.originalGameDirectory -PathType Container) {
        Assert-NativeIdentity ([string]$State.originalGameDirectory) $State.currentHashes ([string]$State.currentFingerprint) 'Original game'
    } elseif ($State.phase -in @('prepared', 'original-game-move-pending', 'restore-original-game-pending', 'restore-original-game-restored', 'restore-original-save-pending', 'restored')) {
        Assert-NativeIdentity ([string]$State.canonical) $State.currentHashes ([string]$State.currentFingerprint) 'Original game'
    } else {
        throw 'Ambiguous topology: the untouched original game tree is missing.'
    }

    if (Test-Path -LiteralPath $State.originalSaveDirectory -PathType Container) {
        Assert-SaveIdentity ([string]$State.originalSaveDirectory) ([string]$State.originalSaveFingerprint) 'Original save'
    }
}

function Assert-ActivateOldInputs {
    param($State)

    foreach ($item in @(
        @{ Name = 'CanonicalGameDirectory'; Value = $CanonicalGameDirectory },
        @{ Name = 'SaveDirectory'; Value = $SaveDirectory },
        @{ Name = 'AppManifestPath'; Value = $AppManifestPath },
        @{ Name = 'DownloadedOldDepot'; Value = $DownloadedOldDepot },
        @{ Name = 'OldBuildId'; Value = $OldBuildId },
        @{ Name = 'OldManifestId'; Value = $OldManifestId },
        @{ Name = 'CurrentBuildId'; Value = $CurrentBuildId },
        @{ Name = 'CurrentManifestId'; Value = $CurrentManifestId }
    )) {
        if ([string]::IsNullOrWhiteSpace([string]$item.Value)) {
            throw "ActivateOld requires -$($item.Name)."
        }
    }
    if ($null -eq $ExpectedOldNativeHash -or $ExpectedOldNativeHash.Count -eq 0) {
        throw 'ActivateOld requires -ExpectedOldNativeHash.'
    }
    if ($null -eq $ExpectedCurrentNativeHash -or $ExpectedCurrentNativeHash.Count -eq 0) {
        throw 'ActivateOld requires -ExpectedCurrentNativeHash.'
    }

    $expectedOld = ConvertTo-ExpectedHashMap $ExpectedOldNativeHash 'ExpectedOldNativeHash'
    $expectedCurrent = ConvertTo-ExpectedHashMap $ExpectedCurrentNativeHash 'ExpectedCurrentNativeHash'
    if ((@(Get-OrdinalMapKeys $expectedOld) -join "`n") -cne (@(Get-OrdinalMapKeys $expectedCurrent) -join "`n")) {
        throw 'ExpectedOldNativeHash and ExpectedCurrentNativeHash must contain the same closed paths.'
    }
    if ($null -ne $State) {
        $comparisons = @(
            @{ Name = 'CanonicalGameDirectory'; Actual = Get-NormalizedAbsolutePath $CanonicalGameDirectory; Expected = [string]$State.canonical },
            @{ Name = 'SaveDirectory'; Actual = Get-NormalizedAbsolutePath $SaveDirectory; Expected = [string]$State.saveDirectory },
            @{ Name = 'AppManifestPath'; Actual = Get-NormalizedAbsolutePath $AppManifestPath; Expected = [string]$State.appManifestPath },
            @{ Name = 'DownloadedOldDepot'; Actual = Get-NormalizedAbsolutePath $DownloadedOldDepot; Expected = [string]$State.downloadedOldDepot },
            @{ Name = 'OldBuildId'; Actual = $OldBuildId; Expected = [string]$State.oldBuildId },
            @{ Name = 'OldManifestId'; Actual = $OldManifestId; Expected = [string]$State.oldManifestId },
            @{ Name = 'CurrentBuildId'; Actual = $CurrentBuildId; Expected = [string]$State.currentBuildId },
            @{ Name = 'CurrentManifestId'; Actual = $CurrentManifestId; Expected = [string]$State.currentManifestId }
        )
        foreach ($comparison in $comparisons) {
            if (-not [string]::Equals([string]$comparison.Actual, [string]$comparison.Expected, [StringComparison]::Ordinal)) {
                throw "ActivateOld cannot resume with changed $($comparison.Name)."
            }
        }
        if (-not (Test-HashMapsEqual $expectedOld $State.oldHashes)) {
            throw 'ActivateOld cannot resume with changed ExpectedOldNativeHash.'
        }
        if (-not (Test-HashMapsEqual $expectedCurrent $State.currentHashes)) {
            throw 'ActivateOld cannot resume with changed ExpectedCurrentNativeHash.'
        }
    }
    return [pscustomobject]@{
        Old = $expectedOld
        Current = $expectedCurrent
    }
}

function Assert-ActivateOldPathSafety {
    foreach ($item in @(
        @{ Name = 'CanonicalGameDirectory'; Value = $CanonicalGameDirectory },
        @{ Name = 'SaveDirectory'; Value = $SaveDirectory },
        @{ Name = 'AppManifestPath'; Value = $AppManifestPath },
        @{ Name = 'DownloadedOldDepot'; Value = $DownloadedOldDepot }
    )) {
        if ([string]::IsNullOrWhiteSpace([string]$item.Value)) {
            throw "ActivateOld requires -$($item.Name)."
        }
    }
    $canonical = Get-NormalizedAbsolutePath $CanonicalGameDirectory
    $save = Get-NormalizedAbsolutePath $SaveDirectory
    $manifest = Get-NormalizedAbsolutePath $AppManifestPath
    $depot = Get-NormalizedAbsolutePath $DownloadedOldDepot
    Assert-InputPathTopology $canonical $save $depot $manifest
    Assert-NoExistingAncestorLink $canonical 'CanonicalGameDirectory'
    Assert-NoExistingAncestorLink $save 'SaveDirectory'
    Assert-NoExistingAncestorLink $depot 'DownloadedOldDepot'
    Assert-NoExistingAncestorLink $manifest 'AppManifestPath'
}

function New-SwitchState {
    param(
        [Parameter(Mandatory)]$ExpectedOldMap,
        [Parameter(Mandatory)]$ExpectedCurrentMap
    )

    $canonical = Get-NormalizedAbsolutePath $CanonicalGameDirectory
    $save = Get-NormalizedAbsolutePath $SaveDirectory
    $manifest = Get-NormalizedAbsolutePath $AppManifestPath
    $depot = Get-NormalizedAbsolutePath $DownloadedOldDepot
    Assert-InputPathTopology $canonical $save $depot $manifest
    if (-not (Test-Path -LiteralPath $canonical -PathType Container)) {
        throw "Canonical game directory is missing: $canonical"
    }
    if (-not (Test-Path -LiteralPath $depot -PathType Container)) {
        throw "Downloaded old depot is missing: $depot"
    }
    if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) {
        throw "Appmanifest is missing: $manifest"
    }
    Assert-ExistingDirectoryRootIsNotLink $canonical 'Canonical game'
    Assert-ExistingDirectoryRootIsNotLink $save 'Save'
    Assert-ExistingDirectoryRootIsNotLink $depot 'Downloaded depot'
    Assert-NoExistingAncestorLink $manifest 'Appmanifest'
    foreach ($identifier in @($OldBuildId, $OldManifestId, $CurrentBuildId, $CurrentManifestId)) {
        if ($identifier -cnotmatch '^[0-9]+$') {
            throw "Build and manifest identifiers must contain decimal digits only: $identifier"
        }
    }
    foreach ($loader in $script:LoaderAllowlist) {
        $loaderPath = Join-Path $canonical $loader.Path
        Assert-NoExistingAncestorLink -Path $loaderPath -Label "Loader allowlist source $($loader.Path)"
        if (-not (Test-Path -LiteralPath $loaderPath)) {
            if ($loader.Required) {
                throw "Required loader allowlist entry is missing: $loaderPath"
            }
            continue
        }
        if (Test-Path -LiteralPath $loaderPath -PathType Container) {
            Assert-DirectoryTreeHasNoLinks $loaderPath "Loader allowlist source $($loader.Path)"
        }
    }
    Assert-RequiredLoaderFileSet $canonical 'Canonical game'

    $manifestAppId = Get-AppManifestPathAppId $manifest
    $manifestEvidence = Get-AppManifestEvidence $manifest
    Assert-AppManifestCoreIdentity $manifestEvidence $manifest $manifestAppId ([string]$CurrentBuildId) ([string]$CurrentManifestId)

    $currentHashes = Get-NativeHashMap $canonical $ExpectedCurrentMap
    if (-not (Test-HashMapsEqual $currentHashes $ExpectedCurrentMap)) {
        throw 'Canonical game hash mismatch against ExpectedCurrentNativeHash.'
    }
    $oldHashes = Get-NativeHashMap $depot $ExpectedOldMap
    if (-not (Test-HashMapsEqual $oldHashes $ExpectedOldMap)) {
        throw 'Downloaded old depot hash mismatch against ExpectedOldNativeHash.'
    }
    $attempt = [guid]::NewGuid().ToString('N')
    $saveWasPresent = Test-Path -LiteralPath $save -PathType Container
    if ((Test-Path -LiteralPath $save) -and -not $saveWasPresent) {
        throw "Save path exists but is not a directory: $save"
    }
    $saveFingerprint = if ($saveWasPresent) { Get-DirectoryFingerprint $save } else { $null }

    $state = [ordered]@{
        schemaVersion = [long]2
        attemptId = $attempt
        phase = 'prepared'
        canonical = $canonical
        saveDirectory = $save
        appManifestPath = $manifest
        downloadedOldDepot = $depot
        originalGameDirectory = "$canonical.modkit-$attempt-original"
        oldSmokeGameDirectory = "$canonical.modkit-$attempt-old-smoke"
        currentSmokeGameDirectory = "$canonical.modkit-$attempt-current-smoke"
        originalSaveDirectory = "$save.modkit-$attempt-original"
        oldSmokeSaveDirectory = "$save.modkit-$attempt-old-smoke"
        currentSmokeSaveDirectory = "$save.modkit-$attempt-current-smoke"
        saveWasPresent = $saveWasPresent
        originalSaveFingerprint = $saveFingerprint
        appManifestAppId = $manifestAppId
        appManifestNormalizedSha256 = [string]$manifestEvidence.NormalizedSha256
        currentDepotManifests = $manifestEvidence.DepotManifests
        oldBuildId = [string]$OldBuildId
        currentBuildId = [string]$CurrentBuildId
        oldManifestId = [string]$OldManifestId
        currentManifestId = [string]$CurrentManifestId
        oldHashes = $oldHashes
        currentHashes = $currentHashes
        oldFingerprint = Get-HashMapFingerprint $oldHashes
        currentFingerprint = Get-HashMapFingerprint $currentHashes
    }
    foreach ($reserved in @($state.originalGameDirectory, $state.oldSmokeGameDirectory, $state.currentSmokeGameDirectory, $state.originalSaveDirectory, $state.oldSmokeSaveDirectory, $state.currentSmokeSaveDirectory)) {
        Assert-PathAbsent $reserved 'attempt-qualified reserved path'
    }
    Write-InitialState $state
    return $state
}

function Complete-PendingPhase {
    param($State)

    switch ([string]$State.phase) {
        'original-game-move-pending' {
            Complete-GameRename $State $State.canonical $State.originalGameDirectory $State.currentHashes $State.currentFingerprint 'original-game-to-backup' 'original-game-preserved' 'Original game'
            return $true
        }
        'original-save-move-pending' {
            Complete-SaveRename $State $State.saveDirectory $State.originalSaveDirectory 'original-save-to-backup' 'originals-preserved' $State.originalSaveFingerprint
            return $true
        }
        'old-copy-preparing' {
            Copy-SmokeTree $State old
            Write-StatePhase $State 'old-copy-prepared'
            return $true
        }
        'old-activate-pending' {
            Complete-GameRename $State $State.oldSmokeGameDirectory $State.canonical $State.oldHashes $State.oldFingerprint 'old-smoke-to-canonical' 'old-active' 'Old smoke game'
            return $true
        }
        'old-deactivate-pending' {
            Complete-GameRename $State $State.canonical $State.oldSmokeGameDirectory $State.oldHashes $State.oldFingerprint 'canonical-old-to-old-smoke' 'old-game-preserved' 'Old smoke game'
            return $true
        }
        'old-save-move-pending' {
            Complete-SaveRename $State $State.saveDirectory $State.oldSmokeSaveDirectory 'old-save-to-old-smoke-save' 'old-save-preserved' $null -AllowBothAbsent
            return $true
        }
        'current-copy-preparing' {
            Copy-SmokeTree $State current
            Write-StatePhase $State 'current-copy-prepared'
            return $true
        }
        'current-activate-pending' {
            Complete-GameRename $State $State.currentSmokeGameDirectory $State.canonical $State.currentHashes $State.currentFingerprint 'current-smoke-to-canonical' 'current-smoke-active' 'Current smoke game'
            return $true
        }
        'restore-old-game-pending' {
            Complete-GameRename $State $State.canonical $State.oldSmokeGameDirectory $State.oldHashes $State.oldFingerprint 'canonical-old-to-old-smoke' 'restore-old-game-preserved' 'Old smoke game'
            return $true
        }
        'restore-current-game-pending' {
            Complete-GameRename $State $State.canonical $State.currentSmokeGameDirectory $State.currentHashes $State.currentFingerprint 'canonical-current-to-current-smoke' 'restore-current-game-preserved' 'Current smoke game'
            return $true
        }
        'restore-old-save-pending' {
            Complete-SaveRename $State $State.saveDirectory $State.oldSmokeSaveDirectory 'old-save-to-old-smoke-save' 'restore-save-preserved' $null -AllowBothAbsent
            return $true
        }
        'restore-current-save-pending' {
            Complete-SaveRename $State $State.saveDirectory $State.currentSmokeSaveDirectory 'current-save-to-current-smoke-save' 'restore-save-preserved' $null -AllowBothAbsent
            return $true
        }
        'restore-original-game-pending' {
            Complete-GameRename $State $State.originalGameDirectory $State.canonical $State.currentHashes $State.currentFingerprint 'original-game-to-canonical' 'restore-original-game-restored' 'Original game'
            return $true
        }
        'restore-original-save-pending' {
            if ($State.saveWasPresent) {
                Complete-SaveRename $State $State.originalSaveDirectory $State.saveDirectory 'original-save-to-canonical' 'restored' $State.originalSaveFingerprint
            } else {
                Assert-PathAbsent $State.originalSaveDirectory 'original save backup'
                Assert-PathAbsent $State.saveDirectory 'originally absent canonical save'
                Write-StatePhase $State 'restored'
            }
            return $true
        }
        default { return $false }
    }
}

function Assert-OldActiveTopology {
    param($State)
    Assert-NativeIdentity $State.canonical $State.oldHashes $State.oldFingerprint 'Active old game'
    Assert-PathAbsent $State.oldSmokeGameDirectory 'old smoke reserved path while active'
    Assert-NativeIdentity $State.originalGameDirectory $State.currentHashes $State.currentFingerprint 'Original game'
}

function Assert-CurrentActiveTopology {
    param($State)
    Assert-NativeIdentity $State.canonical $State.currentHashes $State.currentFingerprint 'Active current game'
    Assert-PathAbsent $State.currentSmokeGameDirectory 'current smoke reserved path while active'
    Assert-NativeIdentity $State.oldSmokeGameDirectory $State.oldHashes $State.oldFingerprint 'Preserved old smoke game'
    Assert-NativeIdentity $State.originalGameDirectory $State.currentHashes $State.currentFingerprint 'Original game'
}

function Invoke-ActivateOld {
    Assert-ActivateOldPathSafety
    $state = Read-StateWithRecovery
    $expectedMaps = Assert-ActivateOldInputs $state
    if ($null -eq $state) {
        $state = New-SwitchState $expectedMaps.Old $expectedMaps.Current
    }
    Assert-StaticEvidence $state
    if ([string]$state.phase -ceq 'restored') {
        throw 'ActivateOld refuses a terminal restored state; archive it before another attempt.'
    }

    while ($true) {
        if (Complete-PendingPhase $state) {
            continue
        }
        switch ([string]$state.phase) {
            'prepared' { Write-StatePhase $state 'original-game-move-pending' }
            'original-game-preserved' {
                if ($state.saveWasPresent) {
                    Write-StatePhase $state 'original-save-move-pending'
                } else {
                    Assert-PathAbsent $state.saveDirectory 'originally absent save'
                    Write-StatePhase $state 'originals-preserved'
                }
            }
            'originals-preserved' { Write-StatePhase $state 'old-copy-preparing' }
            'old-copy-prepared' { Write-StatePhase $state 'old-activate-pending' }
            'old-active' {
                Assert-OldActiveTopology $state
                return
            }
            default { throw "ActivateOld cannot resume phase $($state.phase); use the matching action or Restore." }
        }
    }
}

function Invoke-ActivateCurrent {
    $state = Read-StateWithRecovery
    if ($null -eq $state) {
        throw 'ActivateCurrent requires an existing nonterminal game-switch state.'
    }
    Assert-StaticEvidence $state
    if ([string]$state.phase -ceq 'restored') {
        throw 'ActivateCurrent refuses a terminal restored state.'
    }

    while ($true) {
        if (Complete-PendingPhase $state) {
            continue
        }
        switch ([string]$state.phase) {
            'old-active' {
                Assert-OldActiveTopology $state
                Write-StatePhase $state 'old-deactivate-pending'
            }
            'old-game-preserved' { Write-StatePhase $state 'old-save-move-pending' }
            'old-save-preserved' { Write-StatePhase $state 'current-copy-preparing' }
            'current-copy-prepared' { Write-StatePhase $state 'current-activate-pending' }
            'current-smoke-active' {
                Assert-CurrentActiveTopology $state
                return
            }
            default { throw "ActivateCurrent requires old-active or its exact continuation, not $($state.phase)." }
        }
    }
}

function Assert-RestoredTopology {
    param($State)

    Assert-NativeIdentity $State.canonical $State.currentHashes $State.currentFingerprint 'Restored original game'
    Assert-PathAbsent $State.originalGameDirectory 'original game backup after restore'
    if ($State.saveWasPresent) {
        Assert-SaveIdentity $State.saveDirectory $State.originalSaveFingerprint 'Restored original save'
        Assert-PathAbsent $State.originalSaveDirectory 'original save backup after restore'
    } else {
        Assert-PathAbsent $State.saveDirectory 'originally absent canonical save'
        Assert-PathAbsent $State.originalSaveDirectory 'originally absent save backup'
    }
}

function Invoke-Restore {
    $state = Read-StateWithRecovery
    if ($null -eq $state) {
        throw 'Restore requires an existing game-switch state.'
    }
    Assert-StaticEvidence $state

    while ($true) {
        if (Complete-PendingPhase $state) {
            continue
        }
        switch ([string]$state.phase) {
            'restored' {
                Assert-RestoredTopology $state
                return
            }
            'old-active' {
                Assert-OldActiveTopology $state
                Write-StatePhase $state 'restore-old-game-pending'
            }
            'current-smoke-active' {
                Assert-CurrentActiveTopology $state
                Write-StatePhase $state 'restore-current-game-pending'
            }
            'old-game-preserved' {
                Assert-PathAbsent $state.canonical 'canonical game after old preservation'
                Assert-NativeIdentity $state.oldSmokeGameDirectory $state.oldHashes $state.oldFingerprint 'Preserved old smoke game'
                Write-StatePhase $state 'restore-old-game-preserved'
            }
            'restore-old-game-preserved' { Write-StatePhase $state 'restore-old-save-pending' }
            'restore-current-game-preserved' { Write-StatePhase $state 'restore-current-save-pending' }
            'prepared' {
                Assert-NativeIdentity $state.canonical $state.currentHashes $state.currentFingerprint 'Original game'
                Assert-PathAbsent $state.originalGameDirectory 'original game backup before preservation'
                Write-StatePhase $state 'restore-original-game-restored'
            }
            { $_ -in @('original-game-preserved', 'originals-preserved', 'old-copy-prepared') } {
                Assert-PathAbsent $state.canonical 'canonical game during preparation'
                Assert-NativeIdentity $state.originalGameDirectory $state.currentHashes $state.currentFingerprint 'Original game'
                Write-StatePhase $state 'restore-no-active-game'
            }
            'restore-no-active-game' {
                if ($state.saveWasPresent -and -not (Test-Path -LiteralPath $state.originalSaveDirectory)) {
                    Assert-SaveIdentity $state.saveDirectory $state.originalSaveFingerprint 'Original save'
                } else {
                    Assert-PathAbsent $state.saveDirectory 'canonical smoke save before restore'
                }
                Write-StatePhase $state 'restore-save-preserved'
            }
            { $_ -in @('old-save-preserved', 'current-copy-prepared') } {
                Assert-PathAbsent $state.canonical 'canonical game before original restore'
                Assert-PathAbsent $state.saveDirectory 'canonical save before original restore'
                Write-StatePhase $state 'restore-save-preserved'
            }
            'restore-save-preserved' {
                Assert-PathAbsent $state.canonical 'canonical game before original restore'
                Write-StatePhase $state 'restore-original-game-pending'
            }
            'restore-original-game-restored' {
                Assert-NativeIdentity $state.canonical $state.currentHashes $state.currentFingerprint 'Restored original game'
                Assert-PathAbsent $state.originalGameDirectory 'original game backup after restore'
                if ($state.saveWasPresent) {
                    Write-StatePhase $state 'restore-original-save-pending'
                } else {
                    Assert-PathAbsent $state.saveDirectory 'originally absent canonical save'
                    Write-StatePhase $state 'restored'
                }
            }
            default { throw "Restore cannot reconcile unknown stable phase: $($state.phase)." }
        }
    }
}

$StatePath = Get-NormalizedAbsolutePath $StatePath
Assert-NoExistingAncestorLink $StatePath 'StatePath'
switch ($Action) {
    'ActivateOld' { Invoke-ActivateOld }
    'ActivateCurrent' { Invoke-ActivateCurrent }
    'Restore' { Invoke-Restore }
}
