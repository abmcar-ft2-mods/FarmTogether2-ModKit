[CmdletBinding(DefaultParameterSetName = 'Export')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Export')]
    [ValidateCount(4, 4)]
    [string[]] $PluginAssembly,

    [Parameter(Mandatory, ParameterSetName = 'Export')]
    [ValidateCount(4, 4)]
    [string[]] $RuntimeSource,

    [Parameter(Mandatory, ParameterSetName = 'Export')]
    [string] $InteropMapPath,

    [Parameter(Mandatory, ParameterSetName = 'Export')]
    [string] $Output,

    [Parameter(ParameterSetName = 'Export')]
    [string] $SupportedBuildsPath,

    [Parameter(Mandatory, ParameterSetName = 'Snapshot')]
    [string] $SnapshotInteropDirectory,

    [Parameter(Mandatory, ParameterSetName = 'Snapshot')]
    [ValidatePattern('^\d+$')]
    [string] $SteamBuildId,

    [Parameter(Mandatory, ParameterSetName = 'Snapshot')]
    [string] $SnapshotOutput,

    [Parameter(Mandatory, ParameterSetName = 'SupportedBuilds')]
    [ValidateCount(2, 2)]
    [string[]] $BuildSnapshot,

    [Parameter(Mandatory, ParameterSetName = 'SupportedBuilds')]
    [ValidateCount(4, 4)]
    [string[]] $ModBuild,

    [Parameter(Mandatory, ParameterSetName = 'SupportedBuilds')]
    [string] $SupportedBuildsOutput
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:ApprovedAssemblies = [ordered]@{
    'Assembly-CSharp'                 = '0.0.0.0'
    'Il2Cppmscorlib'                  = '4.0.0.0'
    'MilkstoneUnityExtensions'        = '1.0.0.0'
    'UnityEngine.CoreModule'          = '0.0.0.0'
    'UnityEngine.IMGUIModule'         = '0.0.0.0'
    'UnityEngine.InputLegacyModule'   = '0.0.0.0'
    'UnityEngine.TextRenderingModule' = '0.0.0.0'
}

$script:ApprovedPlugins = [ordered]@{
    'FarmTogether2.AutoSellMod'       = [ordered]@{ modId = 'com.abmcar.farmtogether2.autosellmod'; sourceDirectory = 'AutoSellMod' }
    'FarmTogether2.QoLMod'            = [ordered]@{ modId = 'com.abmcar.farmtogether2.qolmod'; sourceDirectory = 'QoLMod' }
    'FarmTogether2.AutoModRangeMod'   = [ordered]@{ modId = 'com.abmcar.farmtogether2.automodrangemod'; sourceDirectory = 'AutoModRangeMod' }
    'FarmTogether2.FarmhandSpeedMod'  = [ordered]@{ modId = 'com.abmcar.farmtogether2.farmhandspeedmod'; sourceDirectory = 'FarmhandSpeedMod' }
}

function Get-OrdinalSortedStrings {
    param([AllowEmptyCollection()][string[]] $Values)

    [string[]] $copy = @($Values)
    [Array]::Sort($copy, [StringComparer]::Ordinal)
    return $copy
}

function Assert-ExactProperties {
    param(
        [Parameter(Mandatory)] $Value,
        [Parameter(Mandatory)][string[]] $Names,
        [Parameter(Mandatory)][string] $Context
    )

    if ($null -eq $Value) {
        throw "$Context is null."
    }

    [string[]] $actual = @($Value.PSObject.Properties.Name)
    [string[]] $expected = @($Names)
    [Array]::Sort($actual, [StringComparer]::Ordinal)
    [Array]::Sort($expected, [StringComparer]::Ordinal)
    if ([string]::Join("`n", $actual) -cne [string]::Join("`n", $expected)) {
        throw "$Context has an unexpected property set. Expected [$([string]::Join(', ', $expected))], got [$([string]::Join(', ', $actual))]."
    }
}

function Read-ClosedJson {
    param([Parameter(Mandatory)][string] $Path, [Parameter(Mandatory)][string] $Context)

    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not [IO.File]::Exists($resolved)) {
        throw "$Context does not exist: $resolved"
    }

    $raw = [IO.File]::ReadAllText($resolved, [Text.Encoding]::UTF8)
    if ($raw -match '(?i)64hex|placeholder') {
        throw "$Context contains a placeholder."
    }

    try {
        return $raw | ConvertFrom-Json -Depth 100
    }
    catch {
        throw "$Context is not valid JSON: $($_.Exception.Message)"
    }
}

function Write-JsonAtomically {
    param([Parameter(Mandatory)] $Value, [Parameter(Mandatory)][string] $Path)

    $target = [IO.Path]::GetFullPath($Path)
    $parent = [IO.Path]::GetDirectoryName($target)
    if (-not [IO.Directory]::Exists($parent)) {
        throw "Output directory does not exist: $parent"
    }
    if ([IO.Directory]::Exists($target)) {
        throw "Output path is a directory: $target"
    }

    $temporary = Join-Path $parent ('.' + [IO.Path]::GetFileName($target) + '.tmp.' + [Guid]::NewGuid().ToString('N'))
    try {
        $json = $Value | ConvertTo-Json -Depth 100 -Compress
        [IO.File]::WriteAllText($temporary, $json, [Text.UTF8Encoding]::new($false))
        [IO.File]::Move($temporary, $target, $true)
    }
    finally {
        if ([IO.File]::Exists($temporary)) {
            [IO.File]::Delete($temporary)
        }
    }
}

function Get-LowerSha256 {
    param([Parameter(Mandatory)][string] $Text)

    $bytes = [Text.Encoding]::UTF8.GetBytes($Text)
    $hash = [Security.Cryptography.SHA256]::HashData($bytes)
    return [Convert]::ToHexString($hash).ToLowerInvariant()
}

function Assert-Fingerprint {
    param([Parameter(Mandatory)][string] $Value, [Parameter(Mandatory)][string] $Context)

    if ($Value -cnotmatch '^[0-9a-f]{64}$' -or $Value -eq ('0' * 64)) {
        throw "$Context is not a canonical lowercase SHA-256 fingerprint."
    }
}

function Import-MonoCecil {
    if ('Mono.Cecil.ModuleDefinition' -as [type]) {
        return
    }

    $roots = [Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
        $roots.Add($env:NUGET_PACKAGES)
    }
    $roots.Add((Join-Path $HOME '.nuget/packages'))

    foreach ($root in $roots) {
        $candidate = Join-Path $root 'mono.cecil/0.11.6/lib/netstandard2.0/Mono.Cecil.dll'
        if ([IO.File]::Exists($candidate)) {
            Add-Type -Path $candidate
            return
        }
    }

    throw 'Mono.Cecil 0.11.6 is missing. Restore FarmTogether2-ModKit.sln before running this script.'
}

function ConvertTo-LowerToken {
    param($Bytes)

    if ($null -eq $Bytes -or $Bytes.Count -eq 0) {
        return ''
    }
    return [BitConverter]::ToString([byte[]]$Bytes).Replace('-', '').ToLowerInvariant()
}

function ConvertTo-ContractTypeName {
    param([Parameter(Mandatory)] $Type)

    if ($Type -is [Mono.Cecil.GenericInstanceType]) {
        $element = ([string]$Type.ElementType.FullName).Replace('/', '.').Replace('+', '.') -replace '`\d+', ''
        $arguments = @($Type.GenericArguments | ForEach-Object { ConvertTo-ContractTypeName $_ })
        return "$element<$([string]::Join(',', $arguments))>"
    }
    if ($Type -is [Mono.Cecil.ByReferenceType]) { return "$(ConvertTo-ContractTypeName $Type.ElementType)&" }
    if ($Type -is [Mono.Cecil.PointerType]) { return "$(ConvertTo-ContractTypeName $Type.ElementType)*" }
    if ($Type -is [Mono.Cecil.ArrayType]) {
        $commas = if ($Type.Rank -le 1) { '' } else { ',' * ($Type.Rank - 1) }
        return "$(ConvertTo-ContractTypeName $Type.ElementType)[$commas]"
    }
    return ([string]$Type.FullName).Replace('/', '.').Replace('+', '.')
}

function Get-AllTypeDefinitions {
    param([Parameter(Mandatory)] $Module)

    $result = [Collections.Generic.List[object]]::new()
    $pending = [Collections.Generic.Stack[object]]::new()
    foreach ($type in $Module.Types) {
        if ($type.Name -ne '<Module>') {
            $pending.Push($type)
        }
    }
    while ($pending.Count -ne 0) {
        $type = $pending.Pop()
        $result.Add($type)
        foreach ($nested in $type.NestedTypes) {
            $pending.Push($nested)
        }
    }
    return $result.ToArray()
}

function Get-AssemblyIdentityObject {
    param([Parameter(Mandatory)] $Assembly)

    $culture = if ([string]::IsNullOrEmpty([string]$Assembly.Name.Culture)) { '' } else { [string]$Assembly.Name.Culture }
    return [ordered]@{
        name = [string]$Assembly.Name.Name
        version = [string]$Assembly.Name.Version
        culture = $culture
        publicKeyToken = ConvertTo-LowerToken $Assembly.Name.PublicKeyToken
    }
}

function Assert-ApprovedAssemblyIdentity {
    param([Parameter(Mandatory)] $Assembly, [Parameter(Mandatory)][string] $ExpectedName, [Parameter(Mandatory)][string] $Context)

    $identity = Get-AssemblyIdentityObject $Assembly
    if ($identity.name -cne $ExpectedName -or
        $identity.version -cne $script:ApprovedAssemblies[$ExpectedName] -or
        $identity.culture -cne '' -or
        $identity.publicKeyToken -cne '') {
        throw "$Context has identity '$($identity.name), Version=$($identity.version), Culture=$($identity.culture), PublicKeyToken=$($identity.publicKeyToken)' instead of the approved identity."
    }
}

function Get-PublicMetadataFingerprint {
    param([Parameter(Mandatory)] $Assembly)

    $lines = [Collections.Generic.List[string]]::new()
    $identity = Get-AssemblyIdentityObject $Assembly
    $lines.Add("assembly`t$($identity.name)`t$($identity.version)`t$($identity.culture)`t$($identity.publicKeyToken)")

    foreach ($type in Get-AllTypeDefinitions $Assembly.MainModule) {
        if (-not ($type.IsPublic -or $type.IsNestedPublic)) {
            continue
        }

        $kind = if ($type.IsEnum) { 'enum' } elseif ($type.IsInterface) { 'interface' } elseif ($type.IsValueType) { 'struct' } else { 'class' }
        $baseType = if ($null -eq $type.BaseType) { '' } else { ConvertTo-ContractTypeName $type.BaseType }
        $interfaces = @(Get-OrdinalSortedStrings @($type.Interfaces | ForEach-Object { ConvertTo-ContractTypeName $_.InterfaceType }))
        $lines.Add("type`t$kind`t$(ConvertTo-ContractTypeName $type)`t$baseType`t$([string]::Join(',', $interfaces))")

        foreach ($field in $type.Fields) {
            if (-not $field.IsPublic) { continue }
            $constant = if ($field.HasConstant) { [Convert]::ToString($field.Constant, [Globalization.CultureInfo]::InvariantCulture) } else { '' }
            $lines.Add("field`t$(ConvertTo-ContractTypeName $type)`t$($field.IsStatic)`t$(ConvertTo-ContractTypeName $field.FieldType)`t$($field.Name)`t$constant")
        }
        foreach ($method in $type.Methods) {
            if (-not $method.IsPublic) { continue }
            $parameters = @($method.Parameters | ForEach-Object { ConvertTo-ContractTypeName $_.ParameterType })
            $lines.Add("method`t$(ConvertTo-ContractTypeName $type)`t$($method.IsStatic)`t$(ConvertTo-ContractTypeName $method.ReturnType)`t$($method.Name)`t$($method.GenericParameters.Count)`t$([string]::Join(',', $parameters))")
        }
        foreach ($property in $type.Properties) {
            $accessors = @(@($property.GetMethod, $property.SetMethod) | Where-Object { $null -ne $_ -and $_.IsPublic })
            if ($accessors.Count -eq 0) { continue }
            $parameters = @($property.Parameters | ForEach-Object { ConvertTo-ContractTypeName $_.ParameterType })
            $isStatic = @($accessors | Where-Object IsStatic).Count -ne 0
            $lines.Add("property`t$(ConvertTo-ContractTypeName $type)`t$isStatic`t$(ConvertTo-ContractTypeName $property.PropertyType)`t$($property.Name)`t$([string]::Join(',', $parameters))")
        }
        foreach ($event in $type.Events) {
            $accessors = @(@($event.AddMethod, $event.RemoveMethod) | Where-Object { $null -ne $_ -and $_.IsPublic })
            if ($accessors.Count -eq 0) { continue }
            $isStatic = @($accessors | Where-Object IsStatic).Count -ne 0
            $lines.Add("event`t$(ConvertTo-ContractTypeName $type)`t$isStatic`t$(ConvertTo-ContractTypeName $event.EventType)`t$($event.Name)")
        }
    }

    [string[]] $canonical = @(Get-OrdinalSortedStrings $lines.ToArray())
    return Get-LowerSha256 ([string]::Join("`n", $canonical))
}

function New-InteropSnapshotObject {
    param([Parameter(Mandatory)][string] $Directory, [Parameter(Mandatory)][string] $BuildId)

    Import-MonoCecil
    $root = [IO.Path]::GetFullPath($Directory)
    if (-not [IO.Directory]::Exists($root)) {
        throw "Interop directory does not exist: $root"
    }

    $hashes = [ordered]@{}
    $identities = [Collections.Generic.List[object]]::new()
    foreach ($name in $script:ApprovedAssemblies.Keys) {
        $path = Join-Path $root "$name.dll"
        if (-not [IO.File]::Exists($path)) {
            throw "Interop assembly is missing: $path"
        }

        $assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($path, [Mono.Cecil.ReaderParameters]@{ ReadingMode = [Mono.Cecil.ReadingMode]::Deferred; ReadSymbols = $false })
        try {
            Assert-ApprovedAssemblyIdentity $assembly $name $path
            if (@($assembly.MainModule.AssemblyReferences.Name) -cnotcontains 'Il2CppInterop.Runtime') {
                throw "$path is not a LocalInterop assembly: Il2CppInterop.Runtime is not referenced."
            }
            $hashes[$name] = Get-PublicMetadataFingerprint $assembly
            $identities.Add((Get-AssemblyIdentityObject $assembly))
        }
        finally {
            $assembly.Dispose()
        }
    }

    [string[]] $aggregateLines = @($script:ApprovedAssemblies.Keys | ForEach-Object { "$_`t$($hashes[$_])" })
    return [ordered]@{
        schemaVersion = 1
        steamBuildId = $BuildId
        aggregateSha256 = Get-LowerSha256 ([string]::Join("`n", $aggregateLines))
        assemblyMetadataSha256 = $hashes
        assemblies = $identities.ToArray()
    }
}

function Read-ValidatedSnapshot {
    param([Parameter(Mandatory)][string] $Path, [Parameter(Mandatory)][string] $Context)

    $snapshot = Read-ClosedJson $Path $Context
    Assert-ExactProperties $snapshot @('schemaVersion', 'steamBuildId', 'aggregateSha256', 'assemblyMetadataSha256', 'assemblies') $Context
    if ($snapshot.schemaVersion -ne 1) { throw "$Context has unsupported schemaVersion '$($snapshot.schemaVersion)'." }
    if ([string]$snapshot.steamBuildId -cnotmatch '^\d+$') { throw "$Context has an invalid steamBuildId." }
    Assert-Fingerprint ([string]$snapshot.aggregateSha256) "$Context aggregateSha256"
    Assert-ExactProperties $snapshot.assemblyMetadataSha256 @($script:ApprovedAssemblies.Keys) "$Context assemblyMetadataSha256"

    $hashes = [ordered]@{}
    foreach ($name in $script:ApprovedAssemblies.Keys) {
        $value = [string]$snapshot.assemblyMetadataSha256.PSObject.Properties[$name].Value
        Assert-Fingerprint $value "$Context assemblyMetadataSha256.$name"
        $hashes[$name] = $value
    }
    $expectedAggregate = Get-LowerSha256 ([string]::Join("`n", @($script:ApprovedAssemblies.Keys | ForEach-Object { "$_`t$($hashes[$_])" })))
    if ([string]$snapshot.aggregateSha256 -cne $expectedAggregate) {
        throw "$Context aggregateSha256 does not match its assembly fingerprints."
    }

    if (@($snapshot.assemblies).Count -ne $script:ApprovedAssemblies.Count) {
        throw "$Context must contain exactly seven assembly identities."
    }
    $identityByName = @{}
    foreach ($identity in @($snapshot.assemblies)) {
        Assert-ExactProperties $identity @('name', 'version', 'culture', 'publicKeyToken') "$Context assembly identity"
        $name = [string]$identity.name
        if (-not $script:ApprovedAssemblies.Contains($name) -or $identityByName.ContainsKey($name)) {
            throw "$Context contains a duplicate or unknown assembly identity '$name'."
        }
        if ([string]$identity.version -cne $script:ApprovedAssemblies[$name] -or
            [string]$identity.culture -cne '' -or [string]$identity.publicKeyToken -cne '') {
            throw "$Context contains an unapproved identity for '$name'."
        }
        $identityByName[$name] = $identity
    }
    if ($identityByName.Count -ne $script:ApprovedAssemblies.Count) {
        throw "$Context is missing an approved assembly identity."
    }

    return $snapshot
}

function Assert-SnapshotMatchesInterop {
    param([Parameter(Mandatory)] $Snapshot, [Parameter(Mandatory)][string] $InteropDirectory, [Parameter(Mandatory)][string] $Context)

    $actual = New-InteropSnapshotObject $InteropDirectory ([string]$Snapshot.steamBuildId)
    if ([string]$actual.aggregateSha256 -cne [string]$Snapshot.aggregateSha256) {
        throw "$Context aggregate fingerprint does not match the interop DLLs."
    }
    foreach ($name in $script:ApprovedAssemblies.Keys) {
        if ([string]$actual.assemblyMetadataSha256[$name] -cne [string]$Snapshot.assemblyMetadataSha256.PSObject.Properties[$name].Value) {
            throw "$Context fingerprint for '$name' does not match the interop DLL."
        }
    }
}

function New-SupportedBuildsObject {
    param([Parameter(Mandatory)][string[]] $SnapshotPaths, [Parameter(Mandatory)][string[]] $Mappings)

    if ([IO.Path]::GetFullPath($SnapshotPaths[0]) -ceq [IO.Path]::GetFullPath($SnapshotPaths[1])) {
        throw 'BuildSnapshot values must name two different files.'
    }

    $snapshots = @($SnapshotPaths | ForEach-Object { Read-ValidatedSnapshot $_ "Build snapshot '$_'" })
    if ([string]$snapshots[0].steamBuildId -ceq [string]$snapshots[1].steamBuildId) {
        throw 'BuildSnapshot values must contain different Steam build IDs.'
    }

    $modToBuild = @{}
    foreach ($mapping in $Mappings) {
        if ($mapping -cnotmatch '^(?<mod>[^=]+)=(?<build>\d+)$') {
            throw "Invalid ModBuild '$mapping'; expected mod-id=build-id."
        }
        $modId = $Matches.mod
        $buildId = $Matches.build
        $known = @($script:ApprovedPlugins.Values | ForEach-Object { $_.modId }) -ccontains $modId
        if (-not $known) { throw "Unknown mod ID in ModBuild: $modId" }
        if ($modToBuild.ContainsKey($modId)) { throw "Duplicate ModBuild for '$modId'." }
        $modToBuild[$modId] = $buildId
    }
    if ($modToBuild.Count -ne $script:ApprovedPlugins.Count) { throw 'ModBuild must map every approved mod exactly once.' }

    $snapshotsByBuild = @{}
    foreach ($snapshot in $snapshots) { $snapshotsByBuild[[string]$snapshot.steamBuildId] = $snapshot }
    foreach ($buildId in $modToBuild.Values) {
        if (-not $snapshotsByBuild.ContainsKey($buildId)) { throw "ModBuild refers to missing build '$buildId'." }
    }
    foreach ($buildId in $snapshotsByBuild.Keys) {
        if ($modToBuild.Values -cnotcontains $buildId) { throw "Build '$buildId' is not selected by any mod." }
    }

    $identity0 = @($snapshots[0].assemblies | ConvertTo-Json -Depth 10 -Compress)
    $identity1 = @($snapshots[1].assemblies | ConvertTo-Json -Depth 10 -Compress)
    if ([string]$identity0 -cne [string]$identity1) {
        throw 'Build snapshots have different assembly identities.'
    }

    $mods = [ordered]@{}
    foreach ($modId in Get-OrdinalSortedStrings @($modToBuild.Keys)) {
        $mods[$modId] = [ordered]@{ steamBuildId = $modToBuild[$modId] }
    }
    $builds = [ordered]@{}
    foreach ($buildId in Get-OrdinalSortedStrings @($snapshotsByBuild.Keys)) {
        $snapshot = $snapshotsByBuild[$buildId]
        $hashes = [ordered]@{}
        foreach ($name in $script:ApprovedAssemblies.Keys) {
            $hashes[$name] = [string]$snapshot.assemblyMetadataSha256.PSObject.Properties[$name].Value
        }
        $builds[$buildId] = [ordered]@{ aggregateSha256 = [string]$snapshot.aggregateSha256; assemblyMetadataSha256 = $hashes }
    }

    return [ordered]@{
        schemaVersion = 1
        mods = $mods
        builds = $builds
        assemblies = @($snapshots[0].assemblies)
    }
}

if ($PSCmdlet.ParameterSetName -eq 'Snapshot') {
    $snapshot = New-InteropSnapshotObject $SnapshotInteropDirectory $SteamBuildId
    Write-JsonAtomically $snapshot $SnapshotOutput
    return
}

if ($PSCmdlet.ParameterSetName -eq 'SupportedBuilds') {
    $supported = New-SupportedBuildsObject $BuildSnapshot $ModBuild
    Write-JsonAtomically $supported $SupportedBuildsOutput
    return
}

function Read-ValidatedSupportedBuilds {
    param([Parameter(Mandatory)][string] $Path)

    $supported = Read-ClosedJson $Path 'Supported builds'
    Assert-ExactProperties $supported @('schemaVersion', 'mods', 'builds', 'assemblies') 'Supported builds'
    if ($supported.schemaVersion -ne 1) { throw 'Supported builds has an unsupported schemaVersion.' }

    [string[]] $modIds = @($script:ApprovedPlugins.Values | ForEach-Object { $_.modId })
    Assert-ExactProperties $supported.mods $modIds 'Supported builds mods'
    foreach ($modId in $modIds) {
        $entry = $supported.mods.PSObject.Properties[$modId].Value
        Assert-ExactProperties $entry @('steamBuildId') "Supported builds mod '$modId'"
        if ([string]$entry.steamBuildId -cnotmatch '^\d+$') { throw "Supported builds mod '$modId' has an invalid Steam build ID." }
    }

    $buildNames = @($supported.builds.PSObject.Properties.Name)
    if ($buildNames.Count -ne 2 -or @($buildNames | Select-Object -Unique).Count -ne 2) {
        throw 'Supported builds must contain exactly two builds.'
    }
    foreach ($build in $buildNames) {
        if ($build -cnotmatch '^\d+$') { throw "Supported builds contains invalid build ID '$build'." }
        $entry = $supported.builds.PSObject.Properties[$build].Value
        Assert-ExactProperties $entry @('aggregateSha256', 'assemblyMetadataSha256') "Supported build '$build'"
        Assert-Fingerprint ([string]$entry.aggregateSha256) "Supported build '$build' aggregateSha256"
        Assert-ExactProperties $entry.assemblyMetadataSha256 @($script:ApprovedAssemblies.Keys) "Supported build '$build' hashes"
        $hashes = [ordered]@{}
        foreach ($name in $script:ApprovedAssemblies.Keys) {
            $hash = [string]$entry.assemblyMetadataSha256.PSObject.Properties[$name].Value
            Assert-Fingerprint $hash "Supported build '$build' hash '$name'"
            $hashes[$name] = $hash
        }
        $aggregate = Get-LowerSha256 ([string]::Join("`n", @($script:ApprovedAssemblies.Keys | ForEach-Object { "$_`t$($hashes[$_])" })))
        if ($aggregate -cne [string]$entry.aggregateSha256) { throw "Supported build '$build' has an inconsistent aggregate fingerprint." }
    }
    foreach ($modId in $modIds) {
        $build = [string]$supported.mods.PSObject.Properties[$modId].Value.steamBuildId
        if ($buildNames -cnotcontains $build) { throw "Supported mod '$modId' refers to missing build '$build'." }
    }

    if (@($supported.assemblies).Count -ne $script:ApprovedAssemblies.Count) { throw 'Supported builds must list seven assembly identities.' }
    $seen = @{}
    foreach ($identity in @($supported.assemblies)) {
        Assert-ExactProperties $identity @('name', 'version', 'culture', 'publicKeyToken') 'Supported assembly identity'
        $name = [string]$identity.name
        if (-not $script:ApprovedAssemblies.Contains($name) -or $seen.ContainsKey($name)) { throw "Supported builds contains duplicate or unknown assembly '$name'." }
        if ([string]$identity.version -cne $script:ApprovedAssemblies[$name] -or [string]$identity.culture -cne '' -or [string]$identity.publicKeyToken -cne '') {
            throw "Supported builds contains an unapproved identity for '$name'."
        }
        $seen[$name] = $true
    }
    return $supported
}

function Get-AssemblyScopeName {
    param([Parameter(Mandatory)] $Type)

    $current = $Type
    while ($current -is [Mono.Cecil.TypeSpecification]) { $current = $current.ElementType }
    $scope = $current.Scope
    while ($scope -is [Mono.Cecil.TypeReference]) {
        $scopeType = $scope
        while ($scopeType -is [Mono.Cecil.TypeSpecification]) { $scopeType = $scopeType.ElementType }
        $scope = $scopeType.Scope
    }
    if ($scope -is [Mono.Cecil.AssemblyNameReference]) { return [string]$scope.Name }
    return $null
}

function ConvertTo-OpenDeclaringTypeName {
    param([Parameter(Mandatory)] $Type)

    $current = $Type
    while ($current -is [Mono.Cecil.TypeSpecification]) { $current = $current.ElementType }
    return ConvertTo-ContractTypeName $current
}

function New-InteropContext {
    param([Parameter(Mandatory)][string] $Directory)

    $assemblies = [ordered]@{}
    $typesByAssembly = @{}
    $allTypes = [Collections.Generic.List[object]]::new()
    try {
        foreach ($name in $script:ApprovedAssemblies.Keys) {
            $path = Join-Path $Directory "$name.dll"
            $assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($path, [Mono.Cecil.ReaderParameters]@{ ReadingMode = [Mono.Cecil.ReadingMode]::Deferred; ReadSymbols = $false })
            Assert-ApprovedAssemblyIdentity $assembly $name $path
            $assemblies[$name] = $assembly
            $index = @{}
            foreach ($type in Get-AllTypeDefinitions $assembly.MainModule) {
                $index[(ConvertTo-ContractTypeName $type)] = $type
                $allTypes.Add($type)
            }
            $typesByAssembly[$name] = $index
        }
        return [pscustomobject]@{ Assemblies = $assemblies; TypesByAssembly = $typesByAssembly; AllTypes = $allTypes.ToArray() }
    }
    catch {
        foreach ($assembly in $assemblies.Values) { $assembly.Dispose() }
        throw
    }
}

function Close-InteropContext {
    param($Context)
    if ($null -eq $Context) { return }
    foreach ($assembly in $Context.Assemblies.Values) { $assembly.Dispose() }
}

function Resolve-ContextType {
    param(
        [Parameter(Mandatory)] $Context,
        [string] $AssemblyName,
        [Parameter(Mandatory)][string] $TypeName,
        [Parameter(Mandatory)][string] $Description
    )

    $normalized = $TypeName.Replace('/', '.').Replace('+', '.').Trim()
    $candidates = if (-not [string]::IsNullOrEmpty($AssemblyName)) {
        if (-not $Context.TypesByAssembly.ContainsKey($AssemblyName)) { @() }
        elseif ($Context.TypesByAssembly[$AssemblyName].ContainsKey($normalized)) { @($Context.TypesByAssembly[$AssemblyName][$normalized]) }
        else { @($Context.TypesByAssembly[$AssemblyName].Values | Where-Object { [string]$_.Name -ceq $normalized }) }
    }
    else {
        @($Context.AllTypes | Where-Object {
            (ConvertTo-ContractTypeName $_) -ceq $normalized -or [string]$_.Name -ceq $normalized
        })
    }
    $candidates = @($candidates)
    if ($candidates.Count -ne 1) { throw "$Description resolved to $($candidates.Count) interop types instead of exactly one." }
    return $candidates[0]
}

function Get-TypeKind {
    param([Parameter(Mandatory)] $Type)
    if ($Type.IsEnum) { return 'enum' }
    if ($Type.IsInterface) { return 'interface' }
    if ($Type.IsValueType) { return 'struct' }
    if ($null -ne $Type.BaseType -and (ConvertTo-ContractTypeName $Type.BaseType) -ceq 'System.MulticastDelegate') { return 'delegate' }
    return 'class'
}

function Find-PropertyForAccessor {
    param([Parameter(Mandatory)] $Type, [Parameter(Mandatory)] $Method)

    if ($Method.Name -notmatch '^(get|set)_(?<name>.+)$') { return $null }
    $name = $Matches.name
    $indexCount = if ($Method.Name.StartsWith('set_', [StringComparison]::Ordinal)) { $Method.Parameters.Count - 1 } else { $Method.Parameters.Count }
    $matches = @($Type.Properties | Where-Object { $_.Name -ceq $name -and $_.Parameters.Count -eq $indexCount })
    if ($matches.Count -gt 1) { throw "Accessor '$($Type.FullName)::$($Method.Name)' maps to multiple properties." }
    if ($matches.Count -eq 1) { return $matches[0] }
    return $null
}

function ConvertTo-SemanticMember {
    param([Parameter(Mandatory)] $Member, [Parameter(Mandatory)] $Context, [Parameter(Mandatory)][string] $AssemblyName)

    $declaringName = ConvertTo-OpenDeclaringTypeName $Member.DeclaringType
    $declaring = Resolve-ContextType $Context $AssemblyName $declaringName "Member declaring type '$declaringName'"
    if ($Member -is [Mono.Cecil.MethodReference]) {
        $property = Find-PropertyForAccessor $declaring $Member
        if ($null -ne $property) {
            $propertyType = if ($Member.Name.StartsWith('get_', [StringComparison]::Ordinal)) { $Member.ReturnType } else { $Member.Parameters[$Member.Parameters.Count - 1].ParameterType }
            $parameterCount = if ($Member.Name.StartsWith('set_', [StringComparison]::Ordinal)) { $Member.Parameters.Count - 1 } else { $Member.Parameters.Count }
            $parameters = for ($i = 0; $i -lt $parameterCount; $i++) { ConvertTo-ContractTypeName $Member.Parameters[$i].ParameterType }
            $suffix = if ($parameterCount -eq 0) { '' } else { '(' + [string]::Join(',', @($parameters)) + ')' }
            return [ordered]@{
                assembly = $AssemblyName
                declaringType = $declaringName
                kind = 'property'
                signature = "$(ConvertTo-ContractTypeName $propertyType) $($property.Name)$suffix"
                isStatic = -not $Member.HasThis
            }
        }

        $parameters = @($Member.Parameters | ForEach-Object { ConvertTo-ContractTypeName $_.ParameterType })
        return [ordered]@{
            assembly = $AssemblyName
            declaringType = $declaringName
            kind = 'method'
            signature = "$(ConvertTo-ContractTypeName $Member.ReturnType) $($Member.Name)($([string]::Join(',', $parameters)))"
            isStatic = -not $Member.HasThis
        }
    }
    if ($Member -is [Mono.Cecil.FieldReference]) {
        $fields = @($declaring.Fields | Where-Object Name -CEQ $Member.Name)
        if ($fields.Count -ne 1) { throw "Field '$declaringName::$($Member.Name)' resolved to $($fields.Count) definitions." }
        return [ordered]@{
            assembly = $AssemblyName
            declaringType = $declaringName
            kind = 'field'
            signature = "$(ConvertTo-ContractTypeName $Member.FieldType) $($Member.Name)"
            isStatic = [bool]$fields[0].IsStatic
        }
    }
    throw "Unsupported Cecil member reference '$($Member.GetType().FullName)'."
}

function Get-CallBodies {
    param([Parameter(Mandatory)][string] $Text, [Parameter(Mandatory)][string] $Name)

    $result = [Collections.Generic.List[string]]::new()
    $matches = [regex]::Matches($Text, "(?<![A-Za-z0-9_])$([regex]::Escape($Name))\s*\(")
    foreach ($match in $matches) {
        $open = $Text.IndexOf('(', $match.Index)
        $depth = 1
        $inString = $false
        $inChar = $false
        $escaped = $false
        for ($i = $open + 1; $i -lt $Text.Length; $i++) {
            $character = $Text[$i]
            if ($escaped) { $escaped = $false; continue }
            if (($inString -or $inChar) -and $character -eq '\') { $escaped = $true; continue }
            if (-not $inChar -and $character -eq '"') { $inString = -not $inString; continue }
            if (-not $inString -and $character -eq "'") { $inChar = -not $inChar; continue }
            if ($inString -or $inChar) { continue }
            if ($character -eq '(') { $depth++; continue }
            if ($character -eq ')') {
                $depth--
                if ($depth -eq 0) {
                    $result.Add($Text.Substring($open + 1, $i - $open - 1))
                    break
                }
            }
        }
        if ($depth -ne 0) { throw "Unbalanced invocation of '$Name' in runtime source." }
    }
    return $result.ToArray()
}

function ConvertTo-MethodSignature {
    param([Parameter(Mandatory)] $Method)
    $parameters = @($Method.Parameters | ForEach-Object { ConvertTo-ContractTypeName $_.ParameterType })
    return "$(ConvertTo-ContractTypeName $Method.ReturnType) $($Method.Name)($([string]::Join(',', $parameters)))"
}

function Test-SourceTypeMatchesCecilType {
    param([Parameter(Mandatory)][string] $SourceType, [Parameter(Mandatory)] $CecilType)

    $source = ($SourceType -replace '\s+', '')
    $aliases = @{
        'bool' = 'System.Boolean'; 'byte' = 'System.Byte'; 'short' = 'System.Int16'; 'int' = 'System.Int32'
        'long' = 'System.Int64'; 'uint' = 'System.UInt32'; 'float' = 'System.Single'; 'double' = 'System.Double'
        'string' = 'System.String'; 'object' = 'System.Object'
    }
    if ($aliases.ContainsKey($source)) { $source = $aliases[$source] }

    if ($source -match '^(?<outer>[^<]+)<(?<inner>.+)>$' -and $CecilType -is [Mono.Cecil.GenericInstanceType]) {
        $outer = $Matches.outer
        $inner = $Matches.inner
        $actualOuter = ConvertTo-ContractTypeName $CecilType.ElementType
        $actualOuterWithoutArity = $actualOuter -replace '`\d+$', ''
        if ($actualOuterWithoutArity -cne $outer -and -not $actualOuterWithoutArity.EndsWith(".$outer", [StringComparison]::Ordinal)) { return $false }
        if ($CecilType.GenericArguments.Count -ne 1) { return $false }
        return Test-SourceTypeMatchesCecilType $inner $CecilType.GenericArguments[0]
    }

    $actual = ConvertTo-ContractTypeName $CecilType
    return $actual -ceq $source -or $actual.EndsWith(".$source", [StringComparison]::Ordinal)
}

function Add-RuntimeTarget {
    param(
        [Parameter(Mandatory)] $Targets,
        [Parameter(Mandatory)][string] $Assembly,
        [Parameter(Mandatory)][string] $Type,
        [Parameter(Mandatory)][string] $Kind,
        [Parameter(Mandatory)][string] $Signature,
        [Parameter(Mandatory)][bool] $Required,
        [Parameter(Mandatory)][string] $ModId
    )

    $key = "$Assembly`u{1f}$Type`u{1f}$Kind`u{1f}$Signature"
    if (-not $Targets.ContainsKey($key)) {
        $Targets[$key] = [pscustomobject]@{
            Assembly = $Assembly; Type = $Type; Kind = $Kind; Signature = $Signature
            Required = $Required; ModIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        }
    }
    $target = $Targets[$key]
    $target.Required = [bool]($target.Required -or $Required)
    [void]$target.ModIds.Add($ModId)
}

function Resolve-RuntimeTargets {
    param(
        [Parameter(Mandatory)][string] $Text,
        [Parameter(Mandatory)][string] $PluginName,
        [Parameter(Mandatory)][string] $ModId,
        [Parameter(Mandatory)] $Context,
        [Parameter(Mandatory)] $Targets
    )

    foreach ($helper in @('PatchPrefix', 'PatchPostfix', 'PatchBoth', 'PatchPrefixByTypeName')) {
        foreach ($body in Get-CallBodies $Text $helper) {
            if ($helper -eq 'PatchPrefixByTypeName') {
                if ($body -cnotmatch '^\s*"(?<type>[^"]+)"\s*,\s*"(?<member>[^"]+)"') { continue }
            }
            else {
                if ($body -cnotmatch '^\s*typeof\s*\(\s*(?<type>[^\)]+)\s*\)\s*,\s*"(?<member>[^"]+)"') { continue }
            }
            $sourceType = $Matches.type.Trim()
            $memberName = $Matches.member
            $type = Resolve-ContextType $Context 'Assembly-CSharp' $sourceType "Runtime target type '$sourceType' in $PluginName"
            $candidates = @($type.Methods | Where-Object Name -CEQ $memberName)

            $argumentMatch = [regex]::Match($body, 'new\s*\[\s*\]\s*\{(?<arguments>.*?)\}', [Text.RegularExpressions.RegexOptions]::Singleline)
            if ($argumentMatch.Success) {
                $argumentTypes = @([regex]::Matches($argumentMatch.Groups['arguments'].Value, 'typeof\s*\(\s*(?<type>[^\)]+)\s*\)') | ForEach-Object { $_.Groups['type'].Value.Trim() })
                $candidates = @($candidates | Where-Object {
                    if ($_.Parameters.Count -ne $argumentTypes.Count) { return $false }
                    for ($index = 0; $index -lt $argumentTypes.Count; $index++) {
                        if (-not (Test-SourceTypeMatchesCecilType $argumentTypes[$index] $_.Parameters[$index].ParameterType)) { return $false }
                    }
                    return $true
                })
            }
            if ($candidates.Count -ne 1) {
                throw "Runtime method '$sourceType.$memberName' in $PluginName resolved to $($candidates.Count) overloads instead of exactly one."
            }

            $required = if ($PluginName -eq 'FarmTogether2.AutoModRangeMod') {
                $helper -ne 'PatchPrefixByTypeName'
            }
            elseif ($body -match ',\s*(?<required>true|false)\s*$') {
                [bool]::Parse($Matches.required)
            }
            else {
                throw "Runtime target '$sourceType.$memberName' in $PluginName has no explicit required flag."
            }
            $method = $candidates[0]
            Add-RuntimeTarget $Targets ([string]$type.Module.Assembly.Name.Name) (ConvertTo-ContractTypeName $type) 'method' (ConvertTo-MethodSignature $method) $required $ModId
        }
    }

    $reflectionPattern = '(?<helper>FloatMember|NumericMember)\.Resolve\s*\(\s*typeof\s*\(\s*(?<type>[^\)]+)\s*\)\s*,\s*"(?<member>[^"]+)"\s*\)'
    foreach ($match in [regex]::Matches($Text, $reflectionPattern)) {
        $sourceType = $match.Groups['type'].Value.Trim()
        $memberName = $match.Groups['member'].Value
        $type = Resolve-ContextType $Context 'Assembly-CSharp' $sourceType "Reflection target type '$sourceType' in $PluginName"
        $properties = @($type.Properties | Where-Object Name -CEQ $memberName)
        $fields = @($type.Fields | Where-Object Name -CEQ $memberName)
        if (($properties.Count + $fields.Count) -ne 1) {
            throw "Reflection target '$sourceType.$memberName' in $PluginName resolved to $($properties.Count + $fields.Count) members instead of exactly one."
        }
        if ($properties.Count -eq 1) {
            $property = $properties[0]
            $signature = "$(ConvertTo-ContractTypeName $property.PropertyType) $($property.Name)"
            $kind = 'property'
        }
        else {
            $field = $fields[0]
            $signature = "$(ConvertTo-ContractTypeName $field.FieldType) $($field.Name)"
            $kind = 'field'
        }
        Add-RuntimeTarget $Targets ([string]$type.Module.Assembly.Name.Name) (ConvertTo-ContractTypeName $type) $kind $signature $false $ModId
    }
}

function Assert-MapSnapshotAgainstSupported {
    param([Parameter(Mandatory)] $Snapshot, [Parameter(Mandatory)] $Supported, [Parameter(Mandatory)][string] $ModId, [Parameter(Mandatory)][string] $BuildId)

    $supportedMod = $Supported.mods.PSObject.Properties[$ModId].Value
    if ([string]$supportedMod.steamBuildId -cne $BuildId) { throw "Interop map build '$BuildId' disagrees with supported-builds for '$ModId'." }
    $buildProperty = $Supported.builds.PSObject.Properties[$BuildId]
    if ($null -eq $buildProperty) { throw "Interop map refers to unsupported build '$BuildId'." }
    $supportedBuild = $buildProperty.Value
    if ([string]$supportedBuild.aggregateSha256 -cne [string]$Snapshot.aggregateSha256) { throw "Snapshot aggregate for build '$BuildId' disagrees with supported-builds." }
    foreach ($name in $script:ApprovedAssemblies.Keys) {
        if ([string]$supportedBuild.assemblyMetadataSha256.PSObject.Properties[$name].Value -cne [string]$Snapshot.assemblyMetadataSha256.PSObject.Properties[$name].Value) {
            throw "Snapshot hash for '$name' in build '$BuildId' disagrees with supported-builds."
        }
    }
}

function Export-Contract {
    Import-MonoCecil
    $supportedPath = if ([string]::IsNullOrWhiteSpace($SupportedBuildsPath)) { Join-Path $PSScriptRoot '../contracts/supported-builds.json' } else { $SupportedBuildsPath }
    $supported = Read-ValidatedSupportedBuilds $supportedPath

    $map = Read-ClosedJson $InteropMapPath 'Interop map'
    Assert-ExactProperties $map @('schemaVersion', 'plugins') 'Interop map'
    if ($map.schemaVersion -ne 1) { throw 'Interop map has an unsupported schemaVersion.' }
    Assert-ExactProperties $map.plugins @($script:ApprovedPlugins.Keys) 'Interop map plugins'

    $mapEntries = @{}
    $contexts = @{}
    try {
        foreach ($pluginName in $script:ApprovedPlugins.Keys) {
            $entry = $map.plugins.PSObject.Properties[$pluginName].Value
            Assert-ExactProperties $entry @('modId', 'steamBuildId', 'interopDirectory') "Interop map plugin '$pluginName'"
            $approved = $script:ApprovedPlugins[$pluginName]
            if ([string]$entry.modId -cne $approved.modId) { throw "Interop map has the wrong mod ID for '$pluginName'." }
            $buildId = [string]$entry.steamBuildId
            if ($buildId -cnotmatch '^\d+$') { throw "Interop map has an invalid build ID for '$pluginName'." }
            $directory = [IO.Path]::GetFullPath([string]$entry.interopDirectory)
            if (-not [IO.Directory]::Exists($directory)) { throw "Interop directory for '$pluginName' does not exist: $directory" }
            $snapshotPath = Join-Path $directory 'snapshot.json'
            $snapshot = Read-ValidatedSnapshot $snapshotPath "Snapshot for '$pluginName'"
            if ([string]$snapshot.steamBuildId -cne $buildId) { throw "Snapshot/build mismatch for '$pluginName'." }
            Assert-MapSnapshotAgainstSupported $snapshot $supported $approved.modId $buildId
            $cacheKey = "$directory`u{1f}$buildId"
            if (-not $contexts.ContainsKey($cacheKey)) {
                Assert-SnapshotMatchesInterop $snapshot $directory "Snapshot for build '$buildId'"
                $contexts[$cacheKey] = New-InteropContext $directory
            }
            $mapEntries[$pluginName] = [pscustomobject]@{ ModId = $approved.modId; BuildId = $buildId; Directory = $directory; Context = $contexts[$cacheKey] }
        }

        $assemblyPaths = @{}
        foreach ($pathValue in $PluginAssembly) {
            $path = [IO.Path]::GetFullPath($pathValue)
            if (-not [IO.File]::Exists($path)) { throw "Plugin assembly does not exist: $path" }
            $module = [Mono.Cecil.ModuleDefinition]::ReadModule($path, [Mono.Cecil.ReaderParameters]@{ ReadingMode = [Mono.Cecil.ReadingMode]::Deferred; ReadSymbols = $false })
            try { $name = [string]$module.Assembly.Name.Name } finally { $module.Dispose() }
            if (-not $script:ApprovedPlugins.Contains($name) -or $assemblyPaths.ContainsKey($name)) { throw "Plugin assembly '$path' has duplicate or unknown identity '$name'." }
            $assemblyPaths[$name] = $path
        }
        if ($assemblyPaths.Count -ne 4) { throw 'Exactly four approved plugin assemblies are required.' }

        $sourceTexts = @{}
        foreach ($sourceValue in $RuntimeSource) {
            $path = [IO.Path]::GetFullPath($sourceValue)
            if (-not [IO.File]::Exists($path) -or [IO.Path]::GetFileName($path) -cne 'Plugin.cs') { throw "Runtime source is not a Plugin.cs file: $path" }
            $directoryName = [IO.DirectoryInfo]::new([IO.Path]::GetDirectoryName($path)).Name
            $matches = @($script:ApprovedPlugins.Keys | Where-Object { $script:ApprovedPlugins[$_].sourceDirectory -ceq $directoryName })
            if ($matches.Count -ne 1 -or $sourceTexts.ContainsKey($matches[0])) { throw "Runtime source '$path' does not map uniquely to an approved plugin." }
            $sourceTexts[$matches[0]] = [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
        }
        if ($sourceTexts.Count -ne 4) { throw 'Exactly four approved Plugin.cs runtime sources are required.' }

        $directTypes = @{}
        $directMembers = @{}
        $enumDefinitions = @{}
        $runtimeTargets = @{}
        foreach ($pluginName in $script:ApprovedPlugins.Keys) {
            $entry = $mapEntries[$pluginName]
            $context = $entry.Context
            $module = [Mono.Cecil.ModuleDefinition]::ReadModule($assemblyPaths[$pluginName], [Mono.Cecil.ReaderParameters]@{ ReadingMode = [Mono.Cecil.ReadingMode]::Deferred; ReadSymbols = $false })
            try {
                foreach ($reference in $module.AssemblyReferences) {
                    if (-not $script:ApprovedAssemblies.Contains([string]$reference.Name)) { continue }
                    $token = ConvertTo-LowerToken $reference.PublicKeyToken
                    $culture = if ([string]::IsNullOrEmpty([string]$reference.Culture)) { '' } else { [string]$reference.Culture }
                    if ([string]$reference.Version -cne $script:ApprovedAssemblies[[string]$reference.Name] -or $token -cne '' -or $culture -cne '') {
                        throw "Plugin '$pluginName' references an unapproved identity for '$($reference.Name)'."
                    }
                }

                foreach ($typeReference in $module.GetTypeReferences()) {
                    $assemblyName = Get-AssemblyScopeName $typeReference
                    if ([string]::IsNullOrEmpty($assemblyName) -or -not $script:ApprovedAssemblies.Contains($assemblyName)) { continue }
                    $typeName = ConvertTo-ContractTypeName $typeReference
                    $definition = Resolve-ContextType $context $assemblyName $typeName "Direct type '${assemblyName}:$typeName'"
                    $baseType = if ($null -eq $definition.BaseType) { $null } else { ConvertTo-ContractTypeName $definition.BaseType }
                    $item = [ordered]@{ assembly = $assemblyName; type = $typeName; kind = Get-TypeKind $definition; baseType = $baseType; supportOnly = $false }
                    $key = "$assemblyName`u{1f}$typeName"
                    if ($directTypes.ContainsKey($key)) {
                        if (($directTypes[$key] | ConvertTo-Json -Compress) -cne ($item | ConvertTo-Json -Compress)) { throw "Direct type '${assemblyName}:$typeName' differs between mapped builds." }
                    }
                    else { $directTypes[$key] = $item }

                    if ($definition.IsEnum) {
                        if (-not $enumDefinitions.ContainsKey($key)) { $enumDefinitions[$key] = @{} }
                        foreach ($field in $definition.Fields | Where-Object { $_.IsStatic -and $_.HasConstant }) {
                            $value = [Convert]::ToInt64($field.Constant, [Globalization.CultureInfo]::InvariantCulture)
                            if ($enumDefinitions[$key].ContainsKey($field.Name) -and $enumDefinitions[$key][$field.Name] -ne $value) { throw "Enum value '$typeName.$($field.Name)' differs between mapped builds." }
                            $enumDefinitions[$key][$field.Name] = $value
                        }
                    }
                }

                foreach ($memberReference in $module.GetMemberReferences()) {
                    $assemblyName = Get-AssemblyScopeName $memberReference.DeclaringType
                    if ([string]::IsNullOrEmpty($assemblyName) -or -not $script:ApprovedAssemblies.Contains($assemblyName)) { continue }
                    $item = ConvertTo-SemanticMember $memberReference $context $assemblyName
                    $key = "$($item.assembly)`u{1f}$($item.declaringType)`u{1f}$($item.signature)"
                    if ($directMembers.ContainsKey($key)) {
                        if (($directMembers[$key] | ConvertTo-Json -Compress) -cne ($item | ConvertTo-Json -Compress)) { throw "Direct member '$key' differs between mapped builds." }
                    }
                    else { $directMembers[$key] = $item }
                }
            }
            finally { $module.Dispose() }

            Resolve-RuntimeTargets $sourceTexts[$pluginName] $pluginName $entry.ModId $context $runtimeTargets
        }

        if ($directTypes.Count -ne 57) { throw "Expected 57 direct types, found $($directTypes.Count)." }
        if ($directMembers.Count -ne 101) { throw "Expected 101 semantic metadata members before runtime targets, found $($directMembers.Count)." }

        foreach ($target in $runtimeTargets.Values | Where-Object Required) {
            $member = [ordered]@{ assembly = $target.Assembly; declaringType = $target.Type; kind = $target.Kind; signature = $target.Signature; isStatic = $false }
            $targetPlugin = $script:ApprovedPlugins.Keys | Where-Object { $target.ModIds.Contains($script:ApprovedPlugins[$_].modId) } | Select-Object -First 1
            $context = $mapEntries[$targetPlugin].Context
            $type = Resolve-ContextType $context $target.Assembly $target.Type "Required runtime target '$($target.Type)'"
            if ($target.Kind -eq 'method') {
                $matches = @($type.Methods | Where-Object { (ConvertTo-MethodSignature $_) -ceq $target.Signature })
                if ($matches.Count -ne 1) { throw "Required runtime target '$($target.Signature)' no longer resolves uniquely." }
                $member.isStatic = [bool]$matches[0].IsStatic
            }
            $key = "$($member.assembly)`u{1f}$($member.declaringType)`u{1f}$($member.signature)"
            if (-not $directMembers.ContainsKey($key)) { $directMembers[$key] = $member }
        }
        if ($directMembers.Count -ne 110) { throw "Expected 110 direct members after required runtime targets, found $($directMembers.Count)." }

        $associationCount = @($runtimeTargets.Values | ForEach-Object { $_.ModIds.Count } | Measure-Object -Sum).Sum
        if ($runtimeTargets.Count -ne 30 -or $associationCount -ne 32) { throw "Expected 30 runtime targets and 32 mod associations, found $($runtimeTargets.Count) and $associationCount." }

        $enumValues = @{}
        foreach ($enumKey in $enumDefinitions.Keys) {
            $typeItem = $directTypes[$enumKey]
            $fullAlias = [string]$typeItem.type
            $shortAlias = $fullAlias.Substring($fullAlias.LastIndexOf('.') + 1)
            foreach ($text in $sourceTexts.Values) {
                foreach ($alias in @($fullAlias, $shortAlias) | Select-Object -Unique) {
                    $pattern = "(?<![A-Za-z0-9_.])$([regex]::Escape($alias))\s*\.\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)"
                    foreach ($match in [regex]::Matches($text, $pattern)) {
                        $name = $match.Groups['name'].Value
                        if (-not $enumDefinitions[$enumKey].ContainsKey($name)) { throw "Source names unknown enum value '$alias.$name'." }
                        $key = "$($typeItem.assembly)`u{1f}$($typeItem.type)`u{1f}$name"
                        $enumValues[$key] = [ordered]@{ assembly = $typeItem.assembly; enumType = $typeItem.type; name = $name; value = $enumDefinitions[$enumKey][$name] }
                    }
                }
            }
        }

        $runtimeOutput = [Collections.Generic.List[object]]::new()
        foreach ($key in Get-OrdinalSortedStrings @($runtimeTargets.Keys)) {
            $target = $runtimeTargets[$key]
            [string[]] $ids = @(Get-OrdinalSortedStrings @($target.ModIds))
            if ($ids.Count -eq 0) { throw "Runtime target '$key' has no mod IDs." }
            $runtimeOutput.Add([ordered]@{ assembly = $target.Assembly; type = $target.Type; kind = $target.Kind; signature = $target.Signature; required = [bool]$target.Required; modIds = $ids })
        }
        $contract = [ordered]@{
            schemaVersion = 1
            directTypes = @(Get-OrdinalSortedStrings @($directTypes.Keys) | ForEach-Object { $directTypes[$_] })
            directMembers = @(Get-OrdinalSortedStrings @($directMembers.Keys) | ForEach-Object { $directMembers[$_] })
            enumValues = @(Get-OrdinalSortedStrings @($enumValues.Keys) | ForEach-Object { $enumValues[$_] })
            runtimeTargets = $runtimeOutput.ToArray()
        }
        Write-JsonAtomically $contract $Output
    }
    finally {
        foreach ($context in $contexts.Values) { Close-InteropContext $context }
    }
}

Export-Contract
