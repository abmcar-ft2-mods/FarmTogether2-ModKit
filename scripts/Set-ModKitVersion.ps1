#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string]$Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:PathComparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
$script:Utf8 = [Text.UTF8Encoding]::new($false)

function Get-LowerSha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

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

function Write-VerifiedTextFile([string]$Path, [string]$Current, [string]$Desired, [string]$Label) {
    if ($Current -ceq $Desired) {
        return $false
    }

    Assert-RegularFile $Path $Label
    $currentHash = Get-LowerSha256 $Path
    $parent = [IO.Path]::GetDirectoryName($Path)
    $temporary = Join-Path $parent ".$([IO.Path]::GetFileName($Path)).$([guid]::NewGuid().ToString('N')).preparing"
    try {
        $stream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $bytes = $script:Utf8.GetBytes($Desired)
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush($true)
        } finally {
            $stream.Dispose()
        }

        Assert-RegularFile $temporary "$Label temporary"
        $desiredHash = Get-LowerSha256 $temporary
        if ($script:Utf8.GetString([IO.File]::ReadAllBytes($temporary)) -cne $Desired) {
            throw "$Label temporary content differs from the requested content."
        }

        Assert-RegularFile $Path $Label
        if ((Get-LowerSha256 $Path) -cne $currentHash -or [IO.File]::ReadAllText($Path) -cne $Current) {
            throw "$Label changed before promotion."
        }
        Assert-NoReparseAncestor $Path $Label
        [IO.File]::Move($temporary, $Path, $true)
        Assert-RegularFile $Path $Label
        if ((Get-LowerSha256 $Path) -cne $desiredHash -or [IO.File]::ReadAllText($Path) -cne $Desired) {
            throw "$Label differs after promotion."
        }
        return $true
    } finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) {
            [IO.File]::Delete($temporary)
        }
    }
}

$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$project = Join-Path $root 'src/FarmTogether2.GameApi.Ref/FarmTogether2.GameApi.Ref.csproj'
$changelog = Join-Path $root 'CHANGELOG.md'
Assert-RegularFile $project 'Reference project'
Assert-RegularFile $changelog 'CHANGELOG'

$projectText = [IO.File]::ReadAllText($project)
$versionMatches = [regex]::Matches($projectText, '<Version>([^<]*)</Version>', [Text.RegularExpressions.RegexOptions]::CultureInvariant)
if ($versionMatches.Count -ne 1) {
    throw 'The reference project must contain exactly one simple Version element.'
}
$currentVersion = $versionMatches[0].Groups[1].Value
if ($currentVersion -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw "The current reference package version is not canonical stable SemVer: $currentVersion"
}
$desiredProject = if ($currentVersion -ceq $Version) {
    $projectText
} else {
    $projectText.Remove($versionMatches[0].Groups[1].Index, $versionMatches[0].Groups[1].Length).Insert(
        $versionMatches[0].Groups[1].Index,
        $Version)
}

$changelogRaw = [IO.File]::ReadAllText($changelog)
if ($changelogRaw.Replace("`r`n", '').Contains("`r", [StringComparison]::Ordinal)) {
    throw 'CHANGELOG.md contains a noncanonical carriage return.'
}
$changelogUsesCrLf = $changelogRaw.Contains("`r`n", [StringComparison]::Ordinal)
$changelogText = $changelogRaw.Replace("`r`n", "`n")
if (-not $changelogText.StartsWith("# Changelog`n`n", [StringComparison]::Ordinal)) {
    throw 'CHANGELOG.md must begin with the canonical Changelog heading.'
}
$escapedVersion = [regex]::Escape($Version)
$entryMatches = [regex]::Matches(
    $changelogText,
    "(?m)^## $escapedVersion - (\d{4}-\d{2}-\d{2})$",
    [Text.RegularExpressions.RegexOptions]::CultureInvariant)
if ($entryMatches.Count -gt 1) {
    throw "CHANGELOG.md contains duplicate entries for version $Version."
}
if ($entryMatches.Count -eq 1) {
    $entryDate = [DateTime]::MinValue
    if (-not [DateTime]::TryParseExact(
        $entryMatches[0].Groups[1].Value,
        'yyyy-MM-dd',
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::None,
        [ref]$entryDate)) {
        throw "CHANGELOG.md contains an invalid date for version $Version."
    }
    if ($entryMatches[0].Index -ne "# Changelog`n`n".Length) {
        throw "CHANGELOG.md already contains a non-leading entry for version $Version."
    }
    $desiredChangelog = $changelogText
} else {
    $date = [DateTime]::UtcNow.ToString('yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
    $entry = "## $Version - $date`n`n- Prepare ``FarmTogether2.GameApi.Ref`` version $Version.`n`n"
    $desiredChangelog = "# Changelog`n`n$entry" + $changelogText.Substring("# Changelog`n`n".Length)
}
$desiredChangelogRaw = if ($changelogUsesCrLf) { $desiredChangelog.Replace("`n", "`r`n") } else { $desiredChangelog }

$projectChanged = Write-VerifiedTextFile $project $projectText $desiredProject 'Reference project'
if ($projectChanged -and $env:FARMT2_MODKIT_VERSION_FAIL_AT -ceq 'after-project-promotion') {
    [Environment]::Exit(197)
}
$null = Write-VerifiedTextFile $changelog $changelogRaw $desiredChangelogRaw 'CHANGELOG'

Assert-RegularFile $project 'Reference project'
Assert-RegularFile $changelog 'CHANGELOG'
if ([IO.File]::ReadAllText($project) -cne $desiredProject -or
    [IO.File]::ReadAllText($changelog) -cne $desiredChangelogRaw) {
    throw 'ModKit version files did not converge to the requested state.'
}
