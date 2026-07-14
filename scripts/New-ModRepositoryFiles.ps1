#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$RepositoryRoot,
    [switch]$IncludeCallerWorkflows
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:PathComparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
$script:Utf8 = [Text.UTF8Encoding]::new($false)

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

function Assert-DeclaredPath([string]$Path, [string]$Extension, [string]$Label) {
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path -cne $Path.Trim() -or @($Path.ToCharArray() | Where-Object { [char]::IsControl($_) }).Count -ne 0 -or
        $Path.Contains('\') -or $Path.Contains(':') -or
        $Path.StartsWith('/') -or @($Path.Split('/') | Where-Object { $_ -in @('','.', '..') }).Count -ne 0 -or
        -not $Path.EndsWith($Extension, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label contains an unsafe or noncanonical relative path: $Path"
    }
}

function Read-GeneratorInput([string]$Root) {
    $modPath = Join-Path $Root 'mod.json'
    $lockPath = Join-Path $Root 'modkit.lock.json'
    Assert-RegularFile $modPath 'mod.json'
    Assert-RegularFile $lockPath 'modkit.lock.json'

    $modDocument = [Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($modPath))
    try {
        $modFields = @('schemaVersion','id','displayName','assemblyName','version','project','testProjects','guardScripts','installReadme','supportedSteamBuild')
        $mod = Get-ClosedPropertyMap $modDocument.RootElement $modFields 'mod.json'
        $schema = 0
        if ($mod['schemaVersion'].ValueKind -ne [Text.Json.JsonValueKind]::Number -or -not $mod['schemaVersion'].TryGetInt32([ref]$schema) -or
            $schema -ne 1 -or $mod['schemaVersion'].GetRawText() -cne '1') { throw 'mod.json schemaVersion must be the JSON integer 1.' }
        foreach ($field in 'id','displayName','assemblyName','version','project','installReadme','supportedSteamBuild') { $null = Read-String $mod $field 'mod.json' }
        $project = Read-String $mod 'project' 'mod.json'
        $installReadme = Read-String $mod 'installReadme' 'mod.json'
        Assert-DeclaredPath $project '.csproj' 'mod.json project'
        Assert-DeclaredPath $installReadme '.txt' 'mod.json installReadme'
        foreach ($field in 'testProjects','guardScripts') {
            if ($mod[$field].ValueKind -ne [Text.Json.JsonValueKind]::Array) { throw "mod.json field $field must be an array." }
            $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            $extension = if ($field -ceq 'testProjects') { '.csproj' } else { '.ps1' }
            foreach ($item in $mod[$field].EnumerateArray()) {
                if ($item.ValueKind -ne [Text.Json.JsonValueKind]::String) { throw "mod.json field $field must contain strings." }
                $declaredPath = $item.GetString()
                Assert-DeclaredPath $declaredPath $extension "mod.json $field"
                if (-not $seen.Add($declaredPath)) { throw "mod.json field $field contains a duplicate path." }
            }
        }
        $id = Read-String $mod 'id' 'mod.json'
        $assemblyName = Read-String $mod 'assemblyName' 'mod.json'
        $version = Read-String $mod 'version' 'mod.json'
        $displayName = Read-String $mod 'displayName' 'mod.json'
        $supportedSteamBuild = Read-String $mod 'supportedSteamBuild' 'mod.json'
        if ($id -cnotmatch '^[a-z0-9]+(?:[.-][a-z0-9]+)+$' -or [string]::IsNullOrWhiteSpace($displayName) -or $displayName -cne $displayName.Trim() -or
            $assemblyName -cnotmatch '^[A-Za-z_][A-Za-z0-9_.]*$' -or
            $version -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$' -or
            $supportedSteamBuild -cnotmatch '^[1-9][0-9]*$') { throw 'mod.json identity is noncanonical.' }
    } finally { $modDocument.Dispose() }

    $lockDocument = [Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($lockPath))
    try {
        $lockFields = @('schemaVersion','repository','workflowCommit','packageId','packageVersion','releaseTag','assetName','sha256')
        $lockProperties = Get-ClosedPropertyMap $lockDocument.RootElement $lockFields 'modkit.lock.json'
        $schema = 0
        if ($lockProperties['schemaVersion'].ValueKind -ne [Text.Json.JsonValueKind]::Number -or
            -not $lockProperties['schemaVersion'].TryGetInt32([ref]$schema) -or $schema -ne 1 -or
            $lockProperties['schemaVersion'].GetRawText() -cne '1') { throw 'modkit.lock.json schemaVersion must be the JSON integer 1.' }
        $lock = [ordered]@{ schemaVersion = 1 }
        foreach ($field in $lockFields | Where-Object { $_ -cne 'schemaVersion' }) { $lock[$field] = Read-String $lockProperties $field 'modkit.lock.json' }
        $value = [pscustomobject]$lock
    } finally { $lockDocument.Dispose() }
    $repositorySegments = @([string]$value.repository -split '/')
    if ($value.repository -cnotmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or $repositorySegments -contains '.' -or $repositorySegments -contains '..' -or
        $value.workflowCommit -cnotmatch '^[0-9a-f]{40}$' -or
        $value.packageId -cne 'FarmTogether2.GameApi.Ref' -or $value.packageVersion -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$' -or
        $value.releaseTag -cne "v$($value.packageVersion)" -or $value.assetName -cne "$($value.packageId).$($value.packageVersion).nupkg" -or
        $value.sha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'modkit.lock.json identity is noncanonical.' }
    return $value
}

function ConvertTo-Utf8([string]$Text) {
    return $script:Utf8.GetBytes($Text.Replace("`r`n", "`n"))
}

function Test-BytesEqual([byte[]]$Left, [byte[]]$Right) {
    if ($Left.Length -ne $Right.Length) { return $false }
    for ($index = 0; $index -lt $Left.Length; $index++) {
        if ($Left[$index] -ne $Right[$index]) { return $false }
    }
    return $true
}

function Invoke-OwnedFileWrite([string]$Path, [byte[]]$Bytes) {
    Assert-NoReparseAncestor $Path 'Generated file'
    if (Test-Path -LiteralPath $Path -PathType Container) { throw "Generated file path is a directory: $Path" }
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        $item = Get-Item -LiteralPath $Path -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Generated file is a symlink or reparse point: $Path" }
        if (Test-BytesEqual ([IO.File]::ReadAllBytes($Path)) $Bytes) { return }
    }
    $parent = [IO.Path]::GetDirectoryName($Path)
    Assert-NoReparseAncestor $parent 'Generated file parent'
    if (-not (Test-Path -LiteralPath $parent)) { [IO.Directory]::CreateDirectory($parent) | Out-Null }
    $temporary = Join-Path $parent ".$([IO.Path]::GetFileName($Path)).$([guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllBytes($temporary, $Bytes)
        if (-not (Test-BytesEqual ([IO.File]::ReadAllBytes($temporary)) $Bytes)) { throw "Generated file staging verification failed: $Path" }
        [IO.File]::Move($temporary, $Path, $true)
    } finally { if (Test-Path -LiteralPath $temporary) { [IO.File]::Delete($temporary) } }
}

$root = [IO.Path]::GetFullPath($RepositoryRoot)
Assert-NoReparseAncestor $root 'Repository root'
if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw "Repository root does not exist: $root" }
$lock = Read-GeneratorInput $root
$commit = [string]$lock.workflowCommit

$files = [ordered]@{}
$files['.gitattributes'] = ConvertTo-Utf8 @'
* text=auto eol=lf
*.ps1 text eol=lf
*.cs text eol=lf
*.csproj text eol=lf
*.json text eol=lf
*.yml text eol=lf
*.dll binary
*.pdb binary
*.zip binary
*.nupkg binary
'@
$files['.gitignore'] = ConvertTo-Utf8 @'
**/bin/
**/obj/
.artifacts/
artifacts/
.audit/
.modkit/
.vs/
TestResults/
*.dll
*.pdb
*.zip
*.nupkg
*.dmp
*.dump
*.log
*.user
*.suo
'@
$files['global.json'] = ConvertTo-Utf8 @'
{
  "sdk": {
    "version": "8.0.421",
    "rollForward": "disable",
    "allowPrerelease": false
  }
}
'@
$files['NuGet.config'] = ConvertTo-Utf8 @'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="FarmTogether2-ModKit" value=".modkit/packages" /> <!-- gitleaks:allow -->
    <add key="BepInEx" value="https://nuget.bepinex.dev/v3/index.json" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="FarmTogether2-ModKit"> <!-- gitleaks:allow -->
      <package pattern="FarmTogether2.GameApi.Ref" />
    </packageSource>
    <packageSource key="BepInEx">
      <package pattern="BepInEx.*" />
      <package pattern="Il2CppInterop.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="AssetRipper.*" />
      <package pattern="AsmResolver*" />
      <package pattern="Disarm" />
      <package pattern="HarmonyX" />
      <package pattern="Iced" />
      <package pattern="IndexRange" />
      <package pattern="Microsoft.*" />
      <package pattern="Mono.Cecil" />
      <package pattern="MonoMod.*" />
      <package pattern="NETStandard.Library" />
      <package pattern="Newtonsoft.Json" />
      <package pattern="NuGet.*" />
      <package pattern="runtime.*" />
      <package pattern="Samboy063.*" />
      <package pattern="SemanticVersioning" />
      <package pattern="StableNameDotNet" />
      <package pattern="System.*" />
      <package pattern="xunit*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
'@
$files['Directory.Build.props'] = ConvertTo-Utf8 @'
<Project>
  <PropertyGroup>
    <GameApiMode Condition="'$(GameApiMode)' == ''">Hosted</GameApiMode>
    <DeployToGame Condition="'$(DeployToGame)' == ''">false</DeployToGame>
    <IsFarmTogether2Plugin Condition="'$(IsFarmTogether2Plugin)' == ''">false</IsFarmTogether2Plugin>
    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
    <ContinuousIntegrationBuild Condition="'$(CI)' == 'true'">true</ContinuousIntegrationBuild>
    <Deterministic>true</Deterministic>
  </PropertyGroup>
  <Import Project="$(MSBuildThisFileDirectory).modkit\generated\ModKit.lock.props"
          Condition="'$(GameApiMode)' == 'Hosted' and Exists('$(MSBuildThisFileDirectory).modkit\generated\ModKit.lock.props')" />
  <PropertyGroup Condition="'$(GameApiMode)' == 'LocalInterop' and '$(InteropDir)' != '' and '$(GameDir)' == ''">
    <BepInExInteropDir>$([System.IO.Path]::GetFullPath('$(InteropDir)'))</BepInExInteropDir>
  </PropertyGroup>
  <PropertyGroup Condition="'$(GameApiMode)' == 'LocalInterop' and '$(GameDir)' != '' and '$(InteropDir)' == ''">
    <BepInExInteropDir>$([System.IO.Path]::Combine($([System.IO.Path]::GetFullPath('$(GameDir)')), 'BepInEx', 'interop'))</BepInExInteropDir>
  </PropertyGroup>
  <Target Name="ValidateFarmTogether2BuildMode" BeforeTargets="PrepareForBuild;CollectPackageReferences">
    <Error Condition="'$(GameApiMode)' != 'Hosted' and '$(GameApiMode)' != 'LocalInterop'" Text="GameApiMode must be Hosted or LocalInterop." />
    <Error Condition="'$(GameApiMode)' == 'Hosted' and ('$(InteropDir)' != '' or '$(GameDir)' != '')" Text="Hosted mode rejects InteropDir and GameDir." />
    <Error Condition="'$(GameApiMode)' == 'Hosted' and '$(DeployToGame)' == 'true'" Text="Hosted mode cannot deploy to a game directory." />
    <Error Condition="'$(GameApiMode)' == 'Hosted' and '$(FarmTogether2GameApiRefVersion)' == ''" Text="Hosted mode requires the root-owned ModKit lock props." />
    <Error Condition="'$(GameApiMode)' == 'LocalInterop' and (('$(InteropDir)' == '' and '$(GameDir)' == '') or ('$(InteropDir)' != '' and '$(GameDir)' != ''))" Text="LocalInterop requires exactly one of InteropDir or GameDir." />
    <Error Condition="'$(GameApiMode)' == 'LocalInterop' and !Exists('$(BepInExInteropDir)')" Text="The selected BepInEx interop directory does not exist." />
    <Error Condition="'$(DeployToGame)' == 'true' and ('$(GameApiMode)' != 'LocalInterop' or '$(GameDir)' == '')" Text="DeployToGame requires LocalInterop and an explicit GameDir." />
  </Target>
</Project>
'@
$files['Directory.Build.targets'] = ConvertTo-Utf8 @'
<Project>
  <Target Name="RejectDirectFarmTogether2Deployment"
          BeforeTargets="PrepareForBuild"
          Condition="'$(DeployToGame)' == 'true'">
    <Error Text="DeployToGame is available only through the locked scripts/build.ps1 wrapper, which performs verified atomic deployment after all tests and guards pass." />
  </Target>
</Project>
'@
$files['LICENSE'] = ConvertTo-Utf8 @'
MIT License

Copyright (c) 2026 abmcar

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
'@
$resolverSource = Join-Path $PSScriptRoot 'Resolve-ModKit.ps1'
Assert-RegularFile $resolverSource 'ModKit resolver source'
$files['scripts/Resolve-ModKit.ps1'] = [IO.File]::ReadAllBytes($resolverSource)
$files['scripts/build.ps1'] = ConvertTo-Utf8 @'
#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Hosted', 'LocalInterop')][string]$GameApiMode = 'Hosted',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$InteropDir,
    [string]$GameDir,
    [switch]$DeployToGame
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'Resolve-ModKit.ps1') -LockFile (Join-Path $root 'modkit.lock.json') -Destination (Join-Path $root '.modkit/packages') -PropsOutput (Join-Path $root '.modkit/generated/ModKit.lock.props')
if ($LASTEXITCODE -ne 0) { throw "ModKit resolution failed with exit code $LASTEXITCODE." }
$parameters = @{ RepositoryRoot=$root; GameApiMode=$GameApiMode; Configuration=$Configuration; DeployToGame=$DeployToGame }
if (-not [string]::IsNullOrWhiteSpace($InteropDir)) { $parameters.InteropDir = $InteropDir }
if (-not [string]::IsNullOrWhiteSpace($GameDir)) { $parameters.GameDir = $GameDir }
& (Join-Path $root '.modkit/tooling/scripts/Invoke-ModBuild.ps1') @parameters
if ($LASTEXITCODE -ne 0) { throw "Mod build failed with exit code $LASTEXITCODE." }
'@
$files['scripts/pack.ps1'] = ConvertTo-Utf8 @'
#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$OutputDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $root 'artifacts' }
& (Join-Path $PSScriptRoot 'Resolve-ModKit.ps1') -LockFile (Join-Path $root 'modkit.lock.json') -Destination (Join-Path $root '.modkit/packages') -PropsOutput (Join-Path $root '.modkit/generated/ModKit.lock.props')
if ($LASTEXITCODE -ne 0) { throw "ModKit resolution failed with exit code $LASTEXITCODE." }
& (Join-Path $root '.modkit/tooling/scripts/Pack-Mod.ps1') -RepositoryRoot $root -Configuration $Configuration -OutputDirectory $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw "Mod packaging failed with exit code $LASTEXITCODE." }
'@

if ($IncludeCallerWorkflows) {
    $files['.github/workflows/ci.yml'] = ConvertTo-Utf8 ((@'
name: CI

on:
  push:
    branches: [main]
  pull_request:
  workflow_dispatch:

permissions: {}

jobs:
  build:
    permissions:
      contents: read
    uses: abmcar/FarmTogether2-ModKit/.github/workflows/reusable-mod-build.yml@__COMMIT__
    with:
      modkit-commit: __COMMIT__
      game-api-mode: Hosted
      configuration: Release
      candidate-kind: ${{ github.event_name == 'push' && github.ref == 'refs/heads/main' && 'candidate' || 'preview' }}
      retention-days: 7
    secrets:
      modkit_read_token: ${{ secrets.MODKIT_READ_TOKEN }}
'@).Replace('__COMMIT__', $commit))
    $files['.github/workflows/release.yml'] = ConvertTo-Utf8 ((@'
name: Release

on:
  push:
    tags: ['v*.*.*']
  workflow_dispatch:
    inputs:
      tag:
        description: Existing stable tag to publish
        required: true
        type: string

permissions: {}

jobs:
  publish:
    permissions:
      actions: read
      attestations: read
      contents: write
    uses: abmcar/FarmTogether2-ModKit/.github/workflows/reusable-mod-publish.yml@__COMMIT__
    with:
      tag: ${{ github.event_name == 'workflow_dispatch' && inputs.tag || github.ref_name }}
      modkit-commit: __COMMIT__
    secrets:
      modkit_read_token: ${{ secrets.MODKIT_READ_TOKEN }}
'@).Replace('__COMMIT__', $commit))
    $files['.github/dependabot.yml'] = ConvertTo-Utf8 @'
version: 2
updates:
  - package-ecosystem: github-actions
    directory: /
    schedule:
      interval: weekly
'@
}

foreach ($relative in $files.Keys) {
    $destination = [IO.Path]::GetFullPath((Join-Path $root $relative))
    $relativeBack = [IO.Path]::GetRelativePath($root, $destination)
    if ($relativeBack -eq '..' -or $relativeBack.StartsWith("..$([IO.Path]::DirectorySeparatorChar)")) { throw "Generated path escapes the repository: $relative" }
    Assert-NoReparseAncestor $destination "Generated path $relative"
    if (Test-Path -LiteralPath $destination -PathType Container) { throw "Generated file path is a directory: $relative" }
}
foreach ($entry in $files.GetEnumerator()) { Invoke-OwnedFileWrite (Join-Path $root $entry.Key) $entry.Value }
