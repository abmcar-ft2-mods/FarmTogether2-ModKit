#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$OutputDirectory,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:PathComparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/FarmTogether2.GameApi.Ref/FarmTogether2.GameApi.Ref.csproj'
$tool = Join-Path $root 'tools/FarmTogether2.ModKit.Tool/FarmTogether2.ModKit.Tool.csproj'

function Assert-NoReparseAncestor([string]$Path, [string]$Label) {
    $current = [IO.Path]::GetFullPath($Path)
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

Assert-RegularFile $project 'Reference project'
Assert-RegularFile $tool 'ModKit tool project'
$xmlSettings = [Xml.XmlReaderSettings]::new()
$xmlSettings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
$xmlSettings.XmlResolver = $null
$projectXml = [Xml.XmlDocument]::new()
$projectXml.PreserveWhitespace = $true
$reader = [Xml.XmlReader]::Create($project, $xmlSettings)
try {
    $projectXml.Load($reader)
} finally {
    $reader.Dispose()
}
$versions = @($projectXml.SelectNodes('/Project/PropertyGroup/Version'))
if ($versions.Count -ne 1 -or $versions[0].Attributes.Count -ne 0 -or $versions[0].ChildNodes.Count -ne 1) {
    throw 'The reference project must contain exactly one Version element.'
}
$version = [string]$versions[0].InnerText
if ($version -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw "The reference package version is not canonical stable SemVer: $version"
}

if (-not $NoBuild) {
    & dotnet restore $project --locked-mode
    if ($LASTEXITCODE -ne 0) {
        throw "Reference project restore failed with exit code $LASTEXITCODE."
    }
    & dotnet build $project -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "Reference project build failed with exit code $LASTEXITCODE."
    }
    & dotnet restore $tool --locked-mode
    if ($LASTEXITCODE -ne 0) {
        throw "ModKit tool restore failed with exit code $LASTEXITCODE."
    }
    & dotnet build $tool -c Release --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "ModKit tool build failed with exit code $LASTEXITCODE."
    }
}

$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$assemblies = @(
    'Assembly-CSharp',
    'Il2Cppmscorlib',
    'MilkstoneUnityExtensions',
    'UnityEngine.CoreModule',
    'UnityEngine.IMGUIModule',
    'UnityEngine.InputLegacyModule',
    'UnityEngine.TextRenderingModule'
)
$assemblyPaths = [Collections.Generic.List[string]]::new()
foreach ($assembly in $assemblies) {
    $path = Join-Path $root "src/Stubs/$assembly/bin/$Configuration/net6.0/$assembly.dll"
    Assert-RegularFile $path "Stub assembly $assembly"
    $assemblyPaths.Add($path)
}
Assert-NoReparseAncestor $outputRoot 'Package output directory'
if (Test-Path -LiteralPath $outputRoot -PathType Leaf) {
    throw "Package output path is a file: $outputRoot"
}
[IO.Directory]::CreateDirectory($outputRoot) | Out-Null
Assert-NoReparseAncestor $outputRoot 'Package output directory'
$output = Join-Path $outputRoot "FarmTogether2.GameApi.Ref.$version.nupkg"
$arguments = [Collections.Generic.List[string]]::new()
$arguments.AddRange([string[]]@(
    'run', '--project', $tool, '-c', 'Release', '--no-build', '--no-restore', '--',
    'ref-package', 'write',
    '--output', $output,
    '--package-id', 'FarmTogether2.GameApi.Ref',
    '--version', $version
))
foreach ($path in $assemblyPaths) {
    $arguments.Add('--assembly')
    $arguments.Add($path)
}

& dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Deterministic reference package writer failed with exit code $LASTEXITCODE."
}
if (-not (Test-Path -LiteralPath $output -PathType Leaf)) {
    throw "Reference package was not created: $output"
}
Write-Output $output
