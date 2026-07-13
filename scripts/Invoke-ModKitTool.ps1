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

function Assert-RegularFile([string]$Path, [string]$Label) {
    Assert-NoReparseAncestor $Path $Label
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "$Label does not exist: $Path" }
    $item = Get-Item -LiteralPath $Path -Force
    if (-not ($item -is [IO.FileInfo]) -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "$Label must be a regular file: $Path" }
}

function Invoke-Git([string[]]$Arguments, [string]$Label) {
    $output = @(& git --no-replace-objects -C $script:ToolingRoot @Arguments)
    if ($LASTEXITCODE -ne 0) { throw "$Label failed with exit code $LASTEXITCODE." }
    return $output
}

function Assert-GitObjectId([string]$Value, [string]$Label) {
    if ($Value -cnotmatch '^(?:[0-9a-f]{40}|[0-9a-f]{64})$') { throw "$Label is not a canonical Git object ID." }
}

function Get-HeadBlobIdentity([string]$RelativePath, [string]$Label) {
    $expression = "$($script:HeadCommit):$RelativePath"
    $resolved = @(Invoke-Git @('rev-parse','--verify',$expression) "$Label HEAD blob resolution")
    if ($resolved.Count -ne 1) { throw "$Label HEAD blob resolution is ambiguous." }
    $blob = $resolved[0].Trim()
    Assert-GitObjectId $blob "$Label HEAD blob"
    $type = @(Invoke-Git @('cat-file','-t',$blob) "$Label HEAD object type")
    if ($type.Count -ne 1 -or $type[0].Trim() -cne 'blob') { throw "$Label HEAD object is not a blob." }
    $tree = @(Invoke-Git @('-c','core.quotepath=false','ls-tree','--full-tree',$script:HeadCommit,'--',$RelativePath) "$Label HEAD tree entry")
    if ($tree.Count -ne 1) { throw "$Label must have exactly one HEAD tree entry." }
    $match = [regex]::Match($tree[0], '^(100644|100755) blob ([0-9a-f]{40}|[0-9a-f]{64})\t(.+)$')
    if (-not $match.Success -or $match.Groups[2].Value -cne $blob -or $match.Groups[3].Value -cne $RelativePath) {
        throw "$Label HEAD tree entry is not the expected regular blob."
    }
    return $blob
}

function Get-GitFileIdentity([string]$Path, [string]$Label) {
    $output = @(Invoke-Git @('hash-object','--no-filters','--',$Path) "$Label hash-object")
    if ($output.Count -ne 1) { throw "$Label hash-object output is ambiguous." }
    $identity = $output[0].Trim()
    Assert-GitObjectId $identity "$Label hash-object"
    return $identity
}

function Copy-HeadVerifiedRegularFile([string]$Source, [string]$Destination, [string]$RelativePath, [string]$Label) {
    $expected = Get-HeadBlobIdentity $RelativePath $Label
    Assert-RegularFile $Source $Label
    if ((Get-GitFileIdentity $Source "$Label before copy") -cne $expected) { throw "$Label differs from its immutable HEAD blob before copy." }
    Assert-NoReparseAncestor $Destination "$Label staged destination"
    if (Test-Path -LiteralPath $Destination) { throw "$Label staged destination already exists: $Destination" }
    [IO.File]::Copy($Source, $Destination, $false)
    Assert-RegularFile $Source $Label
    if ((Get-GitFileIdentity $Source "$Label after copy") -cne $expected) { throw "$Label differs from its immutable HEAD blob after copy." }
    Assert-RegularFile $Destination "$Label staged destination"
    if ((Get-GitFileIdentity $Destination "$Label staged destination") -cne $expected) { throw "$Label staged destination differs from its immutable HEAD blob." }
}

function Assert-ClosedToolSourceSet([string]$Directory, [string[]]$ExpectedNames) {
    Assert-NoReparseAncestor $Directory 'ModKit tool source directory'
    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) { throw "ModKit tool source directory does not exist: $Directory" }
    $actual = @(Get-ChildItem -LiteralPath $Directory -File -Force | ForEach-Object Name)
    if ($actual.Count -ne $ExpectedNames.Count) { throw 'ModKit tool source directory does not match its closed file allowlist.' }
    foreach ($name in $ExpectedNames) {
        if ($actual -cnotcontains $name) { throw "ModKit tool source directory is missing closed input $name." }
    }
    $unexpectedDirectories = @(Get-ChildItem -LiteralPath $Directory -Directory -Force | Where-Object {
        $_.Name -cne 'bin' -and $_.Name -cne 'obj'
    })
    if ($unexpectedDirectories.Count -ne 0) { throw 'ModKit tool source directory contains an extra source directory outside its closed allowlist.' }
}

$toolProject = Join-Path (Split-Path -Parent $PSScriptRoot) 'tools/FarmTogether2.ModKit.Tool/FarmTogether2.ModKit.Tool.csproj'
$sourceToolDirectory = Split-Path -Parent $toolProject
$script:ToolingRoot = Split-Path -Parent (Split-Path -Parent $sourceToolDirectory)
Assert-NoReparseAncestor $script:ToolingRoot 'ModKit tooling root'
if (-not (Test-Path -LiteralPath $script:ToolingRoot -PathType Container)) { throw "ModKit tooling root does not exist: $script:ToolingRoot" }
$headOutput = @(Invoke-Git @('rev-parse','--verify','HEAD^{commit}') 'ModKit tooling HEAD resolution')
if ($headOutput.Count -ne 1) { throw 'ModKit tooling HEAD resolution is ambiguous.' }
$script:HeadCommit = $headOutput[0].Trim()
Assert-GitObjectId $script:HeadCommit 'ModKit tooling HEAD commit'
$headType = @(Invoke-Git @('cat-file','-t',$script:HeadCommit) 'ModKit tooling HEAD type')
if ($headType.Count -ne 1 -or $headType[0].Trim() -cne 'commit') { throw 'ModKit tooling HEAD is not a commit object.' }

$rootInputs = @('Directory.Build.props','Directory.Packages.props','NuGet.config','global.json')
$toolInputs = @(
    'CandidateVerifier.cs',
    'CanonicalZipWriter.cs',
    'DeterministicNupkgWriter.cs',
    'FarmTogether2.ModKit.Tool.csproj',
    'LockFileResolver.cs',
    'ModPackager.cs',
    'packages.lock.json',
    'Program.cs',
    'ToolModels.cs'
)
Assert-ClosedToolSourceSet $sourceToolDirectory $toolInputs
Assert-RegularFile $toolProject 'ModKit tool project'

$temporaryParent = [IO.Path]::GetTempPath()
Assert-NoReparseAncestor $temporaryParent 'Tool build temporary parent'
$attempt = [guid]::NewGuid().ToString('N')
$buildRoot = Join-Path $temporaryParent "farmtogether2-modkit-tool-$attempt"
$sourceRoot = Join-Path $buildRoot 'source'
$stagedToolDirectory = Join-Path $sourceRoot 'tools/FarmTogether2.ModKit.Tool'
$stagedToolProject = Join-Path $stagedToolDirectory 'FarmTogether2.ModKit.Tool.csproj'
$stagedBuildProps = Join-Path $sourceRoot 'Directory.Build.props'
$output = Join-Path $buildRoot 'bin'
if (Test-Path -LiteralPath $buildRoot) { throw "Tool build attempt path already exists: $buildRoot" }
[IO.Directory]::CreateDirectory($stagedToolDirectory) | Out-Null
[IO.Directory]::CreateDirectory($output) | Out-Null
try {
    foreach ($name in $rootInputs) {
        Copy-HeadVerifiedRegularFile (Join-Path $script:ToolingRoot $name) (Join-Path $sourceRoot $name) $name "Tool build input $name"
    }
    foreach ($name in $toolInputs) {
        $relative = "tools/FarmTogether2.ModKit.Tool/$name"
        Copy-HeadVerifiedRegularFile (Join-Path $sourceToolDirectory $name) (Join-Path $stagedToolDirectory $name) $relative "Tool source $name"
    }
    Push-Location $sourceRoot
    try {
        $isolatedBuildProperties = @(
            "-p:DirectoryBuildPropsPath=$stagedBuildProps",
            '-p:ImportDirectoryBuildTargets=false',
            '-p:CustomBeforeDirectoryBuildProps=',
            '-p:CustomAfterDirectoryBuildProps=',
            '-p:CustomBeforeDirectoryBuildTargets=',
            '-p:CustomAfterDirectoryBuildTargets=',
            '-p:CustomBeforeMicrosoftCommonProps=',
            '-p:CustomAfterMicrosoftCommonProps=',
            '-p:CustomBeforeMicrosoftCommonTargets=',
            '-p:CustomAfterMicrosoftCommonTargets=',
            '-p:CustomBeforeMicrosoftCommonCrossTargetingTargets=',
            '-p:CustomAfterMicrosoftCommonCrossTargetingTargets=',
            '-p:CustomBeforeMicrosoftCSharpTargets=',
            '-p:CustomAfterMicrosoftCSharpTargets=',
            '-p:ImportByWildcardBeforeMicrosoftCommonProps=false',
            '-p:ImportByWildcardAfterMicrosoftCommonProps=false',
            '-p:ImportUserLocationsByWildcardBeforeMicrosoftCommonProps=false',
            '-p:ImportUserLocationsByWildcardAfterMicrosoftCommonProps=false',
            '-p:ImportByWildcardBeforeMicrosoftCommonTargets=false',
            '-p:ImportByWildcardAfterMicrosoftCommonTargets=false',
            '-p:ImportUserLocationsByWildcardBeforeMicrosoftCommonTargets=false',
            '-p:ImportUserLocationsByWildcardAfterMicrosoftCommonTargets=false',
            '-p:ImportByWildcardBeforeMicrosoftCommonCrossTargetingTargets=false',
            '-p:ImportByWildcardAfterMicrosoftCommonCrossTargetingTargets=false',
            '-p:ImportByWildcardBeforeMicrosoftCSharpTargets=false',
            '-p:ImportByWildcardAfterMicrosoftCSharpTargets=false',
            '-p:ImportUserLocationsByWildcardBeforeMicrosoftCSharpTargets=false',
            '-p:ImportUserLocationsByWildcardAfterMicrosoftCSharpTargets=false'
        )
        & dotnet restore $stagedToolProject --locked-mode -noAutoResponse @isolatedBuildProperties
        if ($LASTEXITCODE -ne 0) { throw "Locked ModKit tool restore failed with exit code $LASTEXITCODE." }
        & dotnet build $stagedToolProject -c Release --no-restore --output $output -noAutoResponse @isolatedBuildProperties
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
