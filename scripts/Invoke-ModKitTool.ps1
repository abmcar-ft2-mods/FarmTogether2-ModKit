#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments)][string[]]$ToolArguments
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$script:PathComparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }

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

function Invoke-OwnedBuildCleanup([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    Assert-NoReparseAncestor $Path 'Tool build directory'
    $root = Get-Item -LiteralPath $Path -Force
    if (-not $root.PSIsContainer -or ($root.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Tool build cleanup path is not its expected regular directory.' }
    foreach ($item in Get-ChildItem -LiteralPath $Path -Force -Recurse) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Tool build directory contains a symlink or reparse point: $($item.FullName)" }
    }
    [IO.Directory]::Delete($Path, $true)
}

function Copy-VerifiedRegularFile([string]$Source, [string]$Destination, [string]$Label) {
    Assert-NoReparseAncestor $Source $Label
    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) { throw "$Label does not exist: $Source" }
    $item = Get-Item -LiteralPath $Source -Force
    if (-not ($item -is [IO.FileInfo]) -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "$Label must be a regular file: $Source" }
    [IO.File]::Copy($Source, $Destination, $false)
    $sourceHash = (Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash
    $destinationHash = (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash
    if ($sourceHash -cne $destinationHash) { throw "$Label staging hash mismatch." }
}

$toolProject = Join-Path (Split-Path -Parent $PSScriptRoot) 'tools/FarmTogether2.ModKit.Tool/FarmTogether2.ModKit.Tool.csproj'
Assert-NoReparseAncestor $toolProject 'ModKit tool project'
if (-not (Test-Path -LiteralPath $toolProject -PathType Leaf)) { throw "ModKit tool project does not exist: $toolProject" }
$temporaryParent = [IO.Path]::GetTempPath()
Assert-NoReparseAncestor $temporaryParent 'Tool build temporary parent'
$attempt = [guid]::NewGuid().ToString('N')
$buildRoot = Join-Path $temporaryParent "farmtogether2-modkit-tool-$attempt"
$sourceRoot = Join-Path $buildRoot 'source'
$sourceToolDirectory = Split-Path -Parent $toolProject
$stagedToolDirectory = Join-Path $sourceRoot 'tools/FarmTogether2.ModKit.Tool'
$stagedToolProject = Join-Path $stagedToolDirectory 'FarmTogether2.ModKit.Tool.csproj'
$output = Join-Path $buildRoot 'bin'
if (Test-Path -LiteralPath $buildRoot) { throw "Tool build attempt path already exists: $buildRoot" }
[IO.Directory]::CreateDirectory($stagedToolDirectory) | Out-Null
[IO.Directory]::CreateDirectory($output) | Out-Null
try {
    $toolingRoot = Split-Path -Parent (Split-Path -Parent $sourceToolDirectory)
    foreach ($name in 'Directory.Build.props','Directory.Packages.props','NuGet.config','global.json') {
        Copy-VerifiedRegularFile (Join-Path $toolingRoot $name) (Join-Path $sourceRoot $name) "Tool build input $name"
    }
    $toolSources = @(Get-ChildItem -LiteralPath $sourceToolDirectory -File -Force | Where-Object {
        $_.Extension -ceq '.cs' -or $_.Name -ceq 'FarmTogether2.ModKit.Tool.csproj' -or $_.Name -ceq 'packages.lock.json'
    } | Sort-Object Name)
    if ($toolSources.Count -lt 3 -or @($toolSources | Where-Object Name -ceq 'FarmTogether2.ModKit.Tool.csproj').Count -ne 1 -or
        @($toolSources | Where-Object Name -ceq 'packages.lock.json').Count -ne 1) {
        throw 'ModKit tool source set is incomplete or ambiguous.'
    }
    foreach ($source in $toolSources) {
        Copy-VerifiedRegularFile $source.FullName (Join-Path $stagedToolDirectory $source.Name) "Tool source $($source.Name)"
    }
    Push-Location $sourceRoot
    try {
        & dotnet restore $stagedToolProject --locked-mode
        if ($LASTEXITCODE -ne 0) { throw "Locked ModKit tool restore failed with exit code $LASTEXITCODE." }
        & dotnet build $stagedToolProject -c Release --no-restore --output $output
        if ($LASTEXITCODE -ne 0) { throw "Locked ModKit tool build failed with exit code $LASTEXITCODE." }
    } finally { Pop-Location }
    $assembly = Join-Path $output 'FarmTogether2.ModKit.Tool.dll'
    Assert-NoReparseAncestor $assembly 'Built ModKit tool'
    if (-not (Test-Path -LiteralPath $assembly -PathType Leaf)) { throw 'Locked ModKit tool build did not produce its assembly.' }
    & dotnet $assembly @ToolArguments
    if ($LASTEXITCODE -ne 0) { throw "ModKit tool returned exit code $LASTEXITCODE." }
} finally {
    if (Test-Path -LiteralPath $buildRoot) { Invoke-OwnedBuildCleanup $buildRoot }
}
