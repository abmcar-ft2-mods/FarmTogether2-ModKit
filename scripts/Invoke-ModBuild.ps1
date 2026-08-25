#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$RepositoryRoot,
    [Parameter(Mandatory)][ValidateSet('Hosted', 'LocalInterop')][string]$GameApiMode,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$InteropDir,
    [string]$GameDir,
    [switch]$DeployToGame
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$script:PathComparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }

function Get-FullPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'A required path is empty.' }
    return [IO.Path]::GetFullPath($Path)
}

function Assert-NoReparseAncestor([string]$Path, [string]$Label) {
    $current = Get-FullPath $Path
    while (-not [string]::IsNullOrEmpty($current)) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "$Label contains a symlink or reparse-point ancestor: $current"
            }
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
    if (-not ($item -is [IO.FileInfo]) -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Label must be a regular file: $Path"
    }
}

function Assert-Directory([string]$Path, [string]$Label) {
    Assert-NoReparseAncestor $Path $Label
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { throw "$Label does not exist: $Path" }
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "$Label must not be a symlink: $Path" }
}

function Assert-DirectoryTree([string]$Path, [string]$Label) {
    Assert-Directory $Path $Label
    foreach ($item in Get-ChildItem -LiteralPath $Path -Force -Recurse) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "$Label contains a symlink or reparse point: $($item.FullName)" }
    }
}

function Get-ClosedPropertyMap([Text.Json.JsonElement]$Element, [string[]]$Fields, [string]$Label) {
    if ($Element.ValueKind -ne [Text.Json.JsonValueKind]::Object) { throw "$Label must be a JSON object." }
    $properties = [Collections.Generic.Dictionary[string, Text.Json.JsonElement]]::new([StringComparer]::Ordinal)
    foreach ($property in $Element.EnumerateObject()) {
        if (-not $properties.TryAdd($property.Name, $property.Value.Clone())) { throw "$Label contains duplicate field $($property.Name)." }
    }
    if ($properties.Count -ne $Fields.Count) { throw "$Label contains missing or extra fields." }
    foreach ($field in $Fields) { if (-not $properties.ContainsKey($field)) { throw "$Label is missing field $field." } }
    return ,$properties
}

function Read-String([Collections.Generic.Dictionary[string, Text.Json.JsonElement]]$Properties, [string]$Field, [string]$Label) {
    if ($Properties[$Field].ValueKind -ne [Text.Json.JsonValueKind]::String) { throw "$Label field $Field must be a JSON string." }
    return $Properties[$Field].GetString()
}

function Read-ClosedLock([string]$Path) {
    Assert-RegularFile $Path 'ModKit lock'
    $document = [Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($Path))
    try {
        $fields = @('schemaVersion','repository','workflowCommit','packageId','packageVersion','releaseTag','assetName','sha256')
        $properties = Get-ClosedPropertyMap $document.RootElement $fields 'ModKit lock'
        $schema = 0
        if ($properties['schemaVersion'].ValueKind -ne [Text.Json.JsonValueKind]::Number -or
            -not $properties['schemaVersion'].TryGetInt32([ref]$schema) -or $schema -ne 1 -or
            $properties['schemaVersion'].GetRawText() -cne '1') { throw 'ModKit lock schemaVersion must be the JSON integer 1.' }
        $result = [ordered]@{ schemaVersion = 1 }
        foreach ($field in $fields | Where-Object { $_ -cne 'schemaVersion' }) { $result[$field] = Read-String $properties $field 'ModKit lock' }
        $lock = [pscustomobject]$result
    } finally { $document.Dispose() }
    $repositorySegments = @([string]$lock.repository -split '/')
    if ($lock.repository -cnotmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or $repositorySegments -contains '.' -or $repositorySegments -contains '..' -or
        $lock.workflowCommit -cnotmatch '^[0-9a-f]{40}$' -or $lock.packageId -cne 'FarmTogether2.GameApi.Ref' -or
        $lock.packageVersion -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$' -or
        $lock.releaseTag -cne "v$($lock.packageVersion)" -or $lock.assetName -cne "$($lock.packageId).$($lock.packageVersion).nupkg" -or
        $lock.sha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'ModKit lock identity is noncanonical.' }
    return $lock
}

function Read-StringArray([Collections.Generic.Dictionary[string, Text.Json.JsonElement]]$Properties, [string]$Field) {
    if ($Properties[$Field].ValueKind -ne [Text.Json.JsonValueKind]::Array) { throw "mod.json field $Field must be a JSON array." }
    $values = [Collections.Generic.List[string]]::new()
    foreach ($item in $Properties[$Field].EnumerateArray()) {
        if ($item.ValueKind -ne [Text.Json.JsonValueKind]::String) { throw "mod.json field $Field must contain only strings." }
        $values.Add($item.GetString())
    }
    if (@($values | Sort-Object -Unique).Count -ne $values.Count) { throw "mod.json field $Field contains duplicate paths." }
    return $values.ToArray()
}

function Read-VerifiedModConfig([string]$Path) {
    Assert-RegularFile $Path 'mod.json'
    $document = [Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($Path))
    try {
        $fields = @('schemaVersion','id','displayName','assemblyName','version','project','testProjects','guardScripts','installReadme','supportedSteamBuild')
        $properties = Get-ClosedPropertyMap $document.RootElement $fields 'mod.json'
        $schema = 0
        if ($properties['schemaVersion'].ValueKind -ne [Text.Json.JsonValueKind]::Number -or
            -not $properties['schemaVersion'].TryGetInt32([ref]$schema) -or $schema -ne 1 -or
            $properties['schemaVersion'].GetRawText() -cne '1') { throw 'mod.json schemaVersion must be the JSON integer 1.' }
        $result = [ordered]@{ schemaVersion = 1 }
        foreach ($field in 'id','displayName','assemblyName','version','project') { $result[$field] = Read-String $properties $field 'mod.json' }
        $result['testProjects'] = @(Read-StringArray $properties 'testProjects')
        $result['guardScripts'] = @(Read-StringArray $properties 'guardScripts')
        foreach ($field in 'installReadme','supportedSteamBuild') { $result[$field] = Read-String $properties $field 'mod.json' }
        $config = [pscustomobject]$result
    } finally { $document.Dispose() }
    return $config
}

function Resolve-DeclaredFile([string]$Root, [string]$Relative, [string]$Extension, [string]$Label) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or @($Relative.ToCharArray() | Where-Object { [char]::IsControl($_) }).Count -ne 0 -or
        $Relative.Contains('\') -or $Relative.Contains(':') -or
        $Relative.StartsWith('/') -or @($Relative.Split('/') | Where-Object { $_ -in @('','.', '..') }).Count -ne 0 -or
        -not $Relative.EndsWith($Extension, [StringComparison]::OrdinalIgnoreCase)) { throw "$Label is an unsafe relative path: $Relative" }
    $full = [IO.Path]::GetFullPath((Join-Path $Root $Relative))
    $relativeBack = [IO.Path]::GetRelativePath($Root, $full)
    if ($relativeBack -eq '..' -or $relativeBack.StartsWith("..$([IO.Path]::DirectorySeparatorChar)")) { throw "$Label escapes the repository root." }
    Assert-RegularFile $full $Label
    return $full
}

function Invoke-DotNet([string[]]$Arguments, [string]$Label) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Label failed with exit code $LASTEXITCODE." }
}

$root = Get-FullPath $RepositoryRoot
Assert-Directory $root 'Repository root'
$interopProvided = -not [string]::IsNullOrWhiteSpace($InteropDir)
$gameProvided = -not [string]::IsNullOrWhiteSpace($GameDir)
if ($GameApiMode -ceq 'Hosted') {
    if ($interopProvided -or $gameProvided -or $DeployToGame) { throw 'Hosted mode rejects InteropDir, GameDir, and DeployToGame.' }
} else {
    if ([int]$interopProvided + [int]$gameProvided -ne 1) { throw 'LocalInterop mode requires exactly one of InteropDir or GameDir.' }
    if ($DeployToGame -and -not $gameProvided) { throw 'DeployToGame requires an explicit GameDir.' }
}

$resolvedGame = $null
$resolvedInterop = $null
if ($gameProvided) {
    $resolvedGame = Get-FullPath $GameDir
    Assert-Directory $resolvedGame 'Game directory'
    $resolvedInterop = Join-Path $resolvedGame 'BepInEx/interop'
    Assert-DirectoryTree $resolvedInterop 'Game interop directory'
} elseif ($interopProvided) {
    $resolvedInterop = Get-FullPath $InteropDir
    Assert-DirectoryTree $resolvedInterop 'Interop directory'
}

$lock = Read-ClosedLock (Join-Path $root 'modkit.lock.json')
$nugetPackages = Join-Path $root ".modkit/nuget-packages/$($lock.sha256)"
Assert-NoReparseAncestor $nugetPackages 'Digest-keyed NuGet package cache'
$tooling = Join-Path $root '.modkit/tooling'
Assert-DirectoryTree $tooling 'Locked ModKit tooling'
$packageDirectory = Join-Path $root '.modkit/packages'
Assert-DirectoryTree $packageDirectory 'Locked ModKit package directory'
$packageEntries = @(Get-ChildItem -LiteralPath $packageDirectory -Force)
if ($packageEntries.Count -ne 1 -or -not ($packageEntries[0] -is [IO.FileInfo]) -or $packageEntries[0].Name -cne $lock.assetName) {
    throw 'Locked ModKit package directory does not contain exactly the recorded package.'
}
Assert-RegularFile $packageEntries[0].FullName 'Locked ModKit package'
if ((Get-FileHash -LiteralPath $packageEntries[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant() -cne $lock.sha256) {
    throw 'Locked ModKit package SHA-256 differs from modkit.lock.json.'
}

$head = @(& git --no-replace-objects -C $tooling rev-parse HEAD)
if ($LASTEXITCODE -ne 0 -or $head.Count -ne 1 -or $head[0].Trim() -cne $lock.workflowCommit) { throw 'Locked ModKit tooling HEAD differs from modkit.lock.json.' }
$branch = @(& git --no-replace-objects -C $tooling rev-parse --abbrev-ref HEAD)
if ($LASTEXITCODE -ne 0 -or $branch.Count -ne 1 -or $branch[0].Trim() -cne 'HEAD') { throw 'Locked ModKit tooling must be detached.' }
$status = @(& git --no-replace-objects -C $tooling status --porcelain --untracked-files=all)
if ($LASTEXITCODE -ne 0 -or $status.Count -ne 0) { throw 'Locked ModKit tooling checkout is dirty.' }

$modConfig = Join-Path $root 'mod.json'
$toolRunner = Join-Path $tooling 'scripts/Invoke-ModKitTool.ps1'
Assert-RegularFile $toolRunner 'Locked ModKit tool runner'
& $toolRunner @('mod-config','verify','--file',$modConfig)
if ($LASTEXITCODE -ne 0) { throw "Trusted mod.json verification failed with exit code $LASTEXITCODE." }
$config = Read-VerifiedModConfig $modConfig
$pluginProject = Resolve-DeclaredFile $root $config.project '.csproj' 'Plugin project'
$testProjects = @($config.testProjects | ForEach-Object { Resolve-DeclaredFile $root $_ '.csproj' 'Test project' })
$guards = @($config.guardScripts | ForEach-Object { Resolve-DeclaredFile $root $_ '.ps1' 'Guard script' })
if ($testProjects -contains $pluginProject) { throw 'mod.json project must not also appear in testProjects.' }
if ($DeployToGame) {
    Assert-NoReparseAncestor (Join-Path $resolvedGame "BepInEx/plugins/$($config.assemblyName)") 'Plugin deployment directory'
}

$commonProperties = @(
    "-p:GameApiMode=$GameApiMode",
    '-p:ContinuousIntegrationBuild=true',
    '-p:Deterministic=true'
)
if ($interopProvided) { $commonProperties += "-p:InteropDir=$resolvedInterop" }
if ($gameProvided) { $commonProperties += "-p:GameDir=$resolvedGame" }

Push-Location $root
try {
    $restoreOptions = @('--locked-mode','--packages',$nugetPackages,'--no-cache')
    Invoke-DotNet (@('restore',$pluginProject) + $restoreOptions + $commonProperties + '-p:DeployToGame=false') 'Plugin restore'
    foreach ($project in $testProjects) {
        Invoke-DotNet (@('restore',$project) + $restoreOptions + $commonProperties + '-p:DeployToGame=false') 'Test project restore'
    }
    Invoke-DotNet (@('build',$pluginProject,'-c',$Configuration,'--no-restore') + $commonProperties + '-p:DeployToGame=false') 'Plugin build'
    foreach ($project in $testProjects) {
        Invoke-DotNet (@('build',$project,'-c',$Configuration,'--no-restore') + $commonProperties + '-p:DeployToGame=false') 'Test project build'
        Invoke-DotNet (@('test','--project',$project,'-c',$Configuration,'--no-build','--no-restore') + $commonProperties + '-p:DeployToGame=false') 'Test project tests'
    }
    foreach ($guard in $guards) {
        & pwsh -NoLogo -NoProfile -File $guard
        if ($LASTEXITCODE -ne 0) { throw "Guard script failed with exit code ${LASTEXITCODE}: $guard" }
    }
    if ($DeployToGame) {
        $targetArguments = @('msbuild',$pluginProject,'-nologo',"-property:Configuration=$Configuration",'-property:DeployToGame=false') + $commonProperties + '-getProperty:TargetPath'
        $propertyOutput = @(& dotnet @targetArguments)
        if ($LASTEXITCODE -ne 0) { throw "Plugin TargetPath query failed with exit code $LASTEXITCODE." }
        try { $propertyResult = ($propertyOutput -join [Environment]::NewLine) | ConvertFrom-Json -Depth 10 } catch { throw "Plugin TargetPath query returned invalid JSON: $($_.Exception.Message)" }
        $pluginDll = [string]$propertyResult.Properties.TargetPath
        if ([string]::IsNullOrWhiteSpace($pluginDll)) { throw 'Plugin TargetPath query returned an empty path.' }
        $pluginDll = [IO.Path]::GetFullPath($pluginDll)
        $relativePluginDll = [IO.Path]::GetRelativePath($root, $pluginDll)
        if ($relativePluginDll -eq '..' -or $relativePluginDll.StartsWith("..$([IO.Path]::DirectorySeparatorChar)")) { throw 'Plugin TargetPath escapes the repository root.' }
        Assert-RegularFile $pluginDll 'Built plugin DLL'
        $pluginPdb = [IO.Path]::ChangeExtension($pluginDll, '.pdb')
        $relativePluginPdb = [IO.Path]::GetRelativePath($root, $pluginPdb)
        if ($relativePluginPdb -eq '..' -or $relativePluginPdb.StartsWith("..$([IO.Path]::DirectorySeparatorChar)")) { throw 'Plugin PDB path escapes the repository root.' }
        Assert-RegularFile $pluginPdb 'Built plugin PDB'
        & (Join-Path $PSScriptRoot 'Install-ModPlugin.ps1') `
            -SourceDll $pluginDll `
            -SourcePdb $pluginPdb `
            -GameDir $resolvedGame `
            -AssemblyName $config.assemblyName
    }
} finally { Pop-Location }
