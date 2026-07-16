#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$RepositoryRoot
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

function Invoke-Git([string[]]$Arguments, [string]$Label) {
    $output = @(& git --no-replace-objects -C $script:Root @Arguments)
    if ($LASTEXITCODE -ne 0) { throw "$Label failed with exit code $LASTEXITCODE." }
    return $output
}

function Invoke-GitWithInput([string[]]$Arguments, [string[]]$InputLines, [string]$Label) {
    $output = @($InputLines | & git --no-replace-objects -C $script:Root @Arguments)
    if ($LASTEXITCODE -ne 0) { throw "$Label failed with exit code $LASTEXITCODE." }
    return $output
}

function Add-Offender([string]$Path) {
    $normalized = if ([string]::IsNullOrWhiteSpace($Path)) { '<unknown>' } else { $Path.Replace('\','/') }
    $null = $script:Offenders.Add($normalized)
}

function Test-ForbiddenPath([string]$Path) {
    $normalized = $Path.Replace('\','/')
    return $normalized -match '(?i)(^|/)(GameAssembly\.dll|global-metadata\.dat|FarmTogether2(?:\.exe)?|.*\.(?:dmp|dump))$' -or
        $normalized -match '(?i)\.(dll|pdb|zip|nupkg)$' -or
        $normalized -match '(?i)(^|/)BepInEx/(interop|plugins)(/|$)'
}

function ConvertFrom-YamlCommentedText([string]$Text, [string]$Label) {
    $inSingle = $false
    $inDouble = $false
    $escaped = $false
    for ($index = 0; $index -lt $Text.Length; $index++) {
        $character = $Text[$index]
        if ($inDouble) {
            if ($escaped) { $escaped = $false; continue }
            if ($character -ceq '\') { $escaped = $true; continue }
            if ($character -ceq '"') { $inDouble = $false }
            continue
        }
        if ($inSingle) {
            if ($character -ceq "'") {
                if ($index + 1 -lt $Text.Length -and $Text[$index + 1] -ceq "'") { $index++; continue }
                $inSingle = $false
            }
            continue
        }
        if ($character -ceq '"') { $inDouble = $true; continue }
        if ($character -ceq "'") { $inSingle = $true; continue }
        if ($character -ceq '#' -and ($index -eq 0 -or [char]::IsWhiteSpace($Text[$index - 1]))) {
            return $Text.Substring(0, $index).TrimEnd()
        }
    }
    if ($inSingle -or $inDouble -or $escaped) { throw "$Label contains an unterminated YAML quote." }
    return $Text.TrimEnd()
}

function Find-YamlMappingColon([string]$Text, [bool]$Flow, [string]$Label) {
    $inSingle = $false
    $inDouble = $false
    $escaped = $false
    $squareDepth = 0
    $curlyDepth = 0
    for ($index = 0; $index -lt $Text.Length; $index++) {
        $character = $Text[$index]
        if ($inDouble) {
            if ($escaped) { $escaped = $false; continue }
            if ($character -ceq '\') { $escaped = $true; continue }
            if ($character -ceq '"') { $inDouble = $false }
            continue
        }
        if ($inSingle) {
            if ($character -ceq "'") {
                if ($index + 1 -lt $Text.Length -and $Text[$index + 1] -ceq "'") { $index++; continue }
                $inSingle = $false
            }
            continue
        }
        switch ($character) {
            '"' { $inDouble = $true; continue }
            "'" { $inSingle = $true; continue }
            '[' { $squareDepth++; continue }
            ']' { $squareDepth--; if ($squareDepth -lt 0) { throw "$Label contains an unbalanced YAML flow sequence." }; continue }
            '{' { $curlyDepth++; continue }
            '}' { $curlyDepth--; if ($curlyDepth -lt 0) { throw "$Label contains an unbalanced YAML flow mapping." }; continue }
            ':' {
                if ($squareDepth -eq 0 -and $curlyDepth -eq 0 -and
                    ($Flow -or $index + 1 -eq $Text.Length -or [char]::IsWhiteSpace($Text[$index + 1]))) { return $index }
            }
        }
    }
    if ($inSingle -or $inDouble -or $escaped -or $squareDepth -ne 0 -or $curlyDepth -ne 0) {
        throw "$Label contains malformed YAML flow syntax."
    }
    return -1
}

function Split-YamlFlowItem([string]$Text, [string]$Label) {
    if ([string]::IsNullOrWhiteSpace($Text)) { return @() }
    $items = [Collections.Generic.List[string]]::new()
    $inSingle = $false
    $inDouble = $false
    $escaped = $false
    $squareDepth = 0
    $curlyDepth = 0
    $start = 0
    for ($index = 0; $index -lt $Text.Length; $index++) {
        $character = $Text[$index]
        if ($inDouble) {
            if ($escaped) { $escaped = $false; continue }
            if ($character -ceq '\') { $escaped = $true; continue }
            if ($character -ceq '"') { $inDouble = $false }
            continue
        }
        if ($inSingle) {
            if ($character -ceq "'") {
                if ($index + 1 -lt $Text.Length -and $Text[$index + 1] -ceq "'") { $index++; continue }
                $inSingle = $false
            }
            continue
        }
        switch ($character) {
            '"' { $inDouble = $true; continue }
            "'" { $inSingle = $true; continue }
            '[' { $squareDepth++; continue }
            ']' { $squareDepth--; if ($squareDepth -lt 0) { throw "$Label contains an unbalanced YAML flow sequence." }; continue }
            '{' { $curlyDepth++; continue }
            '}' { $curlyDepth--; if ($curlyDepth -lt 0) { throw "$Label contains an unbalanced YAML flow mapping." }; continue }
            ',' {
                if ($squareDepth -eq 0 -and $curlyDepth -eq 0) {
                    $item = $Text.Substring($start, $index - $start).Trim()
                    if ([string]::IsNullOrEmpty($item)) { throw "$Label contains an empty YAML flow item." }
                    $items.Add($item)
                    $start = $index + 1
                }
            }
        }
    }
    if ($inSingle -or $inDouble -or $escaped -or $squareDepth -ne 0 -or $curlyDepth -ne 0) {
        throw "$Label contains malformed YAML flow syntax."
    }
    $last = $Text.Substring($start).Trim()
    if ([string]::IsNullOrEmpty($last)) { throw "$Label contains an empty YAML flow item." }
    $items.Add($last)
    return $items.ToArray()
}

function ConvertFrom-YamlScalar([string]$Text, [string]$Label) {
    $value = $Text.Trim()
    if ([string]::IsNullOrEmpty($value)) { throw "$Label contains an empty YAML scalar." }
    if ($value.StartsWith("'", [StringComparison]::Ordinal)) {
        if (-not $value.EndsWith("'", [StringComparison]::Ordinal) -or $value.Length -lt 2) { throw "$Label contains an unterminated YAML string." }
        $inner = $value.Substring(1, $value.Length - 2)
        if ($inner.Replace("''", '', [StringComparison]::Ordinal).Contains("'", [StringComparison]::Ordinal)) {
            throw "$Label contains a malformed single-quoted YAML string."
        }
        return $inner.Replace("''", "'", [StringComparison]::Ordinal)
    }
    if ($value.StartsWith('"', [StringComparison]::Ordinal)) {
        try {
            $document = [Text.Json.JsonDocument]::Parse($value)
            try {
                if ($document.RootElement.ValueKind -ne [Text.Json.JsonValueKind]::String) { throw "$Label contains a non-string quoted YAML scalar." }
                return $document.RootElement.GetString()
            } finally { $document.Dispose() }
        } catch { throw "$Label contains an unsupported or malformed double-quoted YAML string." }
    }
    if ($value[0] -in @('*','&','!','?')) { throw "$Label uses an unsupported YAML alias, anchor, tag, or complex key." }
    return $value
}

function ConvertTo-YamlNode([string]$Kind, [object]$Value) {
    return [pscustomobject]@{ Kind = $Kind; Value = $Value }
}

function ConvertFrom-YamlInlineNode([string]$Text, [string]$Label) {
    $value = $Text.Trim()
    if ($value -match '^[|>](?:[1-9][+-]?|[+-][1-9]?)?$') { return ConvertTo-YamlNode 'BlockScalar' $value }
    if ($value.StartsWith('[', [StringComparison]::Ordinal)) {
        if (-not $value.EndsWith(']', [StringComparison]::Ordinal)) { throw "$Label contains an unterminated YAML flow sequence." }
        $items = [Collections.Generic.List[object]]::new()
        foreach ($item in @(Split-YamlFlowItem $value.Substring(1, $value.Length - 2) $Label)) {
            $items.Add((ConvertFrom-YamlInlineNode $item $Label))
        }
        return ConvertTo-YamlNode 'Sequence' $items.ToArray()
    }
    if ($value.StartsWith('{', [StringComparison]::Ordinal)) {
        if (-not $value.EndsWith('}', [StringComparison]::Ordinal)) { throw "$Label contains an unterminated YAML flow mapping." }
        $entries = [Collections.Generic.List[object]]::new()
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($item in @(Split-YamlFlowItem $value.Substring(1, $value.Length - 2) $Label)) {
            $colon = Find-YamlMappingColon $item $true $Label
            if ($colon -lt 1) { throw "$Label contains an invalid YAML flow mapping entry." }
            $key = ConvertFrom-YamlScalar $item.Substring(0, $colon) $Label
            if (-not $seen.Add($key)) { throw "$Label contains duplicate YAML key '$key'." }
            $remainder = $item.Substring($colon + 1).Trim()
            $node = if ([string]::IsNullOrEmpty($remainder)) { ConvertTo-YamlNode 'Null' $null } else { ConvertFrom-YamlInlineNode $remainder $Label }
            $entries.Add([pscustomobject]@{ Key = $key; Value = $node })
        }
        return ConvertTo-YamlNode 'Mapping' $entries.ToArray()
    }
    return ConvertTo-YamlNode 'Scalar' (ConvertFrom-YamlScalar $value $Label)
}

function Get-WorkflowYamlToken([string]$Text, [string]$Label) {
    $normalized = $Text.Replace("`r`n", "`n", [StringComparison]::Ordinal)
    if ($normalized.Contains("`r", [StringComparison]::Ordinal)) { throw "$Label contains noncanonical YAML line endings." }
    $tokens = [Collections.Generic.List[object]]::new()
    $blockParentIndent = -1
    $lineNumber = 0
    foreach ($line in $normalized.Split("`n")) {
        $lineNumber++
        $indent = 0
        while ($indent -lt $line.Length -and $line[$indent] -ceq ' ') { $indent++ }
        if ($indent -lt $line.Length -and $line[$indent] -ceq "`t") { throw "$Label uses a tab for YAML indentation at line $lineNumber." }
        if ($blockParentIndent -ge 0) {
            if ([string]::IsNullOrWhiteSpace($line) -or $indent -gt $blockParentIndent) { continue }
            $blockParentIndent = -1
        }
        $content = ConvertFrom-YamlCommentedText $line.Substring($indent) "$Label line $lineNumber"
        if ([string]::IsNullOrWhiteSpace($content)) { continue }
        if ($content -in @('---','...')) {
            if ($tokens.Count -ne 0) { throw "$Label contains multiple YAML documents." }
            continue
        }
        $tokens.Add([pscustomobject]@{ Indent = $indent; Content = $content; Line = $lineNumber })
        $candidate = $content
        if ($candidate -match '^-(?:\s+)(.*)$') { $candidate = $Matches[1].Trim() }
        $colon = Find-YamlMappingColon $candidate $false "$Label line $lineNumber"
        if ($colon -ge 0) { $candidate = $candidate.Substring($colon + 1).Trim() }
        if ($candidate -match '^[|>](?:[1-9][+-]?|[+-][1-9]?)?$') { $blockParentIndent = $indent }
    }
    return $tokens.ToArray()
}

function ConvertFrom-YamlMappingText([string]$Text, [string]$Label) {
    $colon = Find-YamlMappingColon $Text $false $Label
    if ($colon -lt 1) { throw "$Label contains an invalid YAML mapping entry." }
    $key = ConvertFrom-YamlScalar $Text.Substring(0, $colon) $Label
    $remainder = $Text.Substring($colon + 1).Trim()
    return [pscustomobject]@{ Key = $key; Remainder = $remainder }
}

function Read-YamlBlock([object[]]$Tokens, [ref]$Index, [int]$Indent, [string]$Label) {
    if ($Index.Value -ge $Tokens.Count -or $Tokens[$Index.Value].Indent -ne $Indent) { throw "$Label contains an invalid YAML indentation transition." }
    $isSequence = $Tokens[$Index.Value].Content -match '^-(?:\s|$)'
    if ($isSequence) {
        $items = [Collections.Generic.List[object]]::new()
        while ($Index.Value -lt $Tokens.Count -and $Tokens[$Index.Value].Indent -eq $Indent) {
            $token = $Tokens[$Index.Value]
            if ($token.Content -cnotmatch '^-(?<spacing>\s*)(?<remainder>.*)$') { throw "$Label mixes YAML mapping and sequence entries at line $($token.Line)." }
            $spacing = $Matches['spacing'].Length
            $remainder = $Matches['remainder'].Trim()
            $Index.Value++
            if ([string]::IsNullOrEmpty($remainder)) {
                $node = if ($Index.Value -lt $Tokens.Count -and $Tokens[$Index.Value].Indent -gt $Indent) {
                    Read-YamlBlock $Tokens $Index $Tokens[$Index.Value].Indent $Label
                } else { ConvertTo-YamlNode 'Null' $null }
                $items.Add($node)
                continue
            }
            $colon = Find-YamlMappingColon $remainder $false "$Label line $($token.Line)"
            if ($colon -lt 0) {
                $node = ConvertFrom-YamlInlineNode $remainder "$Label line $($token.Line)"
                if ($Index.Value -lt $Tokens.Count -and $Tokens[$Index.Value].Indent -gt $Indent) { throw "$Label indents content below a scalar sequence item." }
                $items.Add($node)
                continue
            }
            $entry = ConvertFrom-YamlMappingText $remainder "$Label line $($token.Line)"
            $entries = [Collections.Generic.List[object]]::new()
            $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            $null = $seen.Add($entry.Key)
            $itemMappingIndent = $Indent + 1 + [Math]::Max($spacing, 1)
            if ([string]::IsNullOrEmpty($entry.Remainder)) {
                $entryNode = if ($Index.Value -lt $Tokens.Count -and $Tokens[$Index.Value].Indent -gt $itemMappingIndent) {
                    Read-YamlBlock $Tokens $Index $Tokens[$Index.Value].Indent $Label
                } else { ConvertTo-YamlNode 'Null' $null }
            } else {
                $entryNode = ConvertFrom-YamlInlineNode $entry.Remainder "$Label line $($token.Line)"
            }
            $entries.Add([pscustomobject]@{ Key = $entry.Key; Value = $entryNode })
            if ($Index.Value -lt $Tokens.Count -and $Tokens[$Index.Value].Indent -gt $Indent) {
                if ($Tokens[$Index.Value].Indent -ne $itemMappingIndent) { throw "$Label contains invalid sequence-mapping indentation." }
                $additional = Read-YamlBlock $Tokens $Index $itemMappingIndent $Label
                if ($additional.Kind -cne 'Mapping') { throw "$Label contains a non-mapping sequence continuation." }
                foreach ($additionalEntry in $additional.Value) {
                    if (-not $seen.Add($additionalEntry.Key)) { throw "$Label contains duplicate YAML key '$($additionalEntry.Key)'." }
                    $entries.Add($additionalEntry)
                }
            }
            $items.Add((ConvertTo-YamlNode 'Mapping' $entries.ToArray()))
        }
        return ConvertTo-YamlNode 'Sequence' $items.ToArray()
    }

    $entries = [Collections.Generic.List[object]]::new()
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    while ($Index.Value -lt $Tokens.Count -and $Tokens[$Index.Value].Indent -eq $Indent) {
        $token = $Tokens[$Index.Value]
        if ($token.Content -match '^-(?:\s|$)') { throw "$Label mixes YAML mapping and sequence entries at line $($token.Line)." }
        $entry = ConvertFrom-YamlMappingText $token.Content "$Label line $($token.Line)"
        if (-not $seen.Add($entry.Key)) { throw "$Label contains duplicate YAML key '$($entry.Key)'." }
        $Index.Value++
        if ([string]::IsNullOrEmpty($entry.Remainder)) {
            $node = if ($Index.Value -lt $Tokens.Count -and $Tokens[$Index.Value].Indent -gt $Indent) {
                Read-YamlBlock $Tokens $Index $Tokens[$Index.Value].Indent $Label
            } else { ConvertTo-YamlNode 'Null' $null }
        } else {
            $node = ConvertFrom-YamlInlineNode $entry.Remainder "$Label line $($token.Line)"
            if ($Index.Value -lt $Tokens.Count -and $Tokens[$Index.Value].Indent -gt $Indent) { throw "$Label indents content below an inline mapping value." }
        }
        $entries.Add([pscustomobject]@{ Key = $entry.Key; Value = $node })
    }
    return ConvertTo-YamlNode 'Mapping' $entries.ToArray()
}

function Read-WorkflowYamlAst([string]$Text, [string]$Label) {
    $tokens = @(Get-WorkflowYamlToken $Text $Label)
    if ($tokens.Count -eq 0) { return ConvertTo-YamlNode 'Mapping' @() }
    if ($tokens[0].Indent -ne 0) { throw "$Label must start at YAML indentation zero." }
    if ($tokens.Count -eq 1 -and $tokens[0].Content.StartsWith('{', [StringComparison]::Ordinal)) {
        $root = ConvertFrom-YamlInlineNode $tokens[0].Content $Label
    } else {
        $index = 0
        $root = Read-YamlBlock $tokens ([ref]$index) 0 $Label
        if ($index -ne $tokens.Count) { throw "$Label contains an unconsumed YAML structure." }
    }
    if ($root.Kind -cne 'Mapping') { throw "$Label root must be a YAML mapping." }
    return $root
}

function Test-YamlNodeContainment([object]$Node, [string]$Expected) {
    if ($Node.Kind -ceq 'Scalar') { return $Node.Value -ceq $Expected }
    if ($Node.Kind -ceq 'Mapping') {
        foreach ($entry in $Node.Value) {
            if ($entry.Key -ceq $Expected -or (Test-YamlNodeContainment $entry.Value $Expected)) { return $true }
        }
    } elseif ($Node.Kind -ceq 'Sequence') {
        foreach ($item in $Node.Value) { if (Test-YamlNodeContainment $item $Expected) { return $true } }
    }
    return $false
}

function Test-DependabotAutoMergeWorkflow([string]$Text) {
    $normalized = [regex]::Replace(
        $Text,
        '(?m)^(?<prefix>\s*uses:\s*dependabot/fetch-metadata)@[0-9a-f]{40}\s*$',
        '${prefix}@__PIN__')
    $expected = @'
name: Dependabot auto-merge

on:
  pull_request_target:
    types: [opened, synchronize, reopened]

permissions:
  contents: write
  pull-requests: write

jobs:
  enable-auto-merge:
    if: github.event.pull_request.user.login == 'dependabot[bot]' && github.repository == 'abmcar/FarmTogether2-ModKit'
    runs-on: ubuntu-latest
    steps:
      - name: Read Dependabot metadata
        id: dependabot
        uses: dependabot/fetch-metadata@__PIN__
      - name: Enable auto-merge for patch and minor updates
        if: steps.dependabot.outputs.update-type == 'version-update:semver-patch' || steps.dependabot.outputs.update-type == 'version-update:semver-minor'
        env:
          GH_TOKEN: ${{ github.token }}
          PR_URL: ${{ github.event.pull_request.html_url }}
        run: gh pr merge --auto --squash "$PR_URL"
'@
    return [string]::Equals($normalized.TrimEnd("`n"), $expected.TrimEnd("`n"), [StringComparison]::Ordinal)
}

function Test-WorkflowYaml([string]$Text, [string]$Label) {
    try { $root = Read-WorkflowYamlAst $Text $Label } catch { return $false }
    foreach ($entry in $root.Value) {
        if ($entry.Key -ceq 'on') {
            if ($entry.Value.Kind -ceq 'BlockScalar') { return $false }
            if ((Test-YamlNodeContainment $entry.Value 'pull_request_target') -and
                ($Label -cne '.github/workflows/dependabot-auto-merge.yml' -or -not (Test-DependabotAutoMergeWorkflow $Text))) { return $false }
        }
    }
    $pending = [Collections.Generic.Stack[object]]::new()
    $pending.Push($root)
    while ($pending.Count -ne 0) {
        $node = $pending.Pop()
        if ($node.Kind -ceq 'Sequence') {
            foreach ($item in $node.Value) { $pending.Push($item) }
            continue
        }
        if ($node.Kind -cne 'Mapping') { continue }
        foreach ($entry in $node.Value) {
            if ($entry.Key -ceq 'uses' -and ($entry.Value.Kind -cne 'Scalar' -or $entry.Value.Value -cnotmatch '@[0-9a-f]{40}$')) { return $false }
            if ($entry.Key -ceq 'secrets' -and (
                $entry.Value.Kind -ceq 'BlockScalar' -or
                ($entry.Value.Kind -ceq 'Scalar' -and $entry.Value.Value -ceq 'inherit'))) { return $false }
            $pending.Push($entry.Value)
        }
    }
    return $true
}

function Test-BlobText([string]$ObjectId, [string]$Path) {
    if (-not $script:BlobTexts.ContainsKey($ObjectId)) {
        $lines = @(Invoke-Git @('cat-file','blob',$ObjectId) "History blob $ObjectId")
        $script:BlobTexts[$ObjectId] = $lines -join "`n"
    }
    $text = $script:BlobTexts[$ObjectId]
    $privateRepositoryName = 'FarmTogether2' + '-Mods'
    $localPathPattern = '(?i)([A-Z]:\\Users\\|[A-Z]:\\SteamLibrary\\|/home/[A-Za-z0-9._-]+/|/Users/[A-Za-z0-9._-]+/|steamapps[\\/](?:common|compatdata)|' + [regex]::Escape($privateRepositoryName) + ')'
    if ($text -match $localPathPattern) {
        Add-Offender $Path
    }
    if ($Path -match '(?i)^\.github/workflows/.*\.ya?ml$') {
        if (-not (Test-WorkflowYaml $text $Path)) { Add-Offender $Path }
    }
}

$script:Root = [IO.Path]::GetFullPath($RepositoryRoot)
Assert-NoReparseAncestor $script:Root 'Repository root'
if (-not (Test-Path -LiteralPath $script:Root -PathType Container)) { throw "Repository root does not exist: $script:Root" }
$inside = @(Invoke-Git @('rev-parse','--is-inside-work-tree') 'Git repository verification')
if ($inside.Count -ne 1 -or $inside[0].Trim() -cne 'true') { throw 'RepositoryRoot is not a Git worktree.' }

$script:Offenders = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$script:BlobTexts = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
& git --no-replace-objects -C $script:Root diff --quiet --
if ($LASTEXITCODE -eq 1) { Add-Offender '<dirty-tracked-worktree>' } elseif ($LASTEXITCODE -ne 0) { throw 'Tracked worktree diff failed.' }
& git --no-replace-objects -C $script:Root diff --cached --quiet --
if ($LASTEXITCODE -eq 1) { Add-Offender '<dirty-tracked-index>' } elseif ($LASTEXITCODE -ne 0) { throw 'Tracked index diff failed.' }

$tracked = @(Invoke-Git @('-c','core.quotepath=false','ls-files') 'Tracked path listing')
foreach ($path in $tracked) {
    if (Test-ForbiddenPath $path) { Add-Offender $path }
}

$identities = @(Invoke-Git @('log','--all','--format=%ae%n%ce') 'Commit identity listing')
if (@($identities | Where-Object { $_ -match '(?i)@qq\.com$' -and $_ -notmatch '(?i)users\.noreply\.github\.com$' }).Count -ne 0) {
    Add-Offender '<commit-identity>'
}

$objectLines = @(Invoke-Git @('rev-list','--objects','--all') 'Complete history object listing')
$objectIds = [Collections.Generic.List[string]]::new()
$objectPaths = [Collections.Generic.List[string]]::new()
foreach ($line in $objectLines) {
    $objectMatch = [regex]::Match($line, '^([0-9a-f]{40,64})(?:\s+(.*))?$')
    if (-not $objectMatch.Success) { throw 'Git returned a noncanonical history object line.' }
    $objectIds.Add($objectMatch.Groups[1].Value)
    $path = if ($objectMatch.Groups[2].Success -and -not [string]::IsNullOrWhiteSpace($objectMatch.Groups[2].Value)) { $objectMatch.Groups[2].Value } else { '<history-object>' }
    $objectPaths.Add($path)
}
$objectInfo = @(Invoke-GitWithInput @('cat-file','--batch-check=%(objectname) %(objecttype) %(objectsize)') $objectIds.ToArray() 'Complete history object inspection')
if ($objectInfo.Count -ne $objectIds.Count) { throw 'Git returned an incomplete history object inspection.' }

$historyObjects = $objectIds.Count
$historyBlobs = 0
$seenBlobs = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$blobSizes = [Collections.Generic.Dictionary[string, long]]::new([StringComparer]::Ordinal)
for ($index = 0; $index -lt $objectIds.Count; $index++) {
    $objectId = $objectIds[$index]
    $path = $objectPaths[$index]
    $infoMatch = [regex]::Match($objectInfo[$index], '^([0-9a-f]{40,64}) (blob|tree|commit|tag) ([0-9]+)$')
    if (-not $infoMatch.Success -or $infoMatch.Groups[1].Value -cne $objectId) {
        throw 'Git returned a noncanonical history object inspection.'
    }
    if ($infoMatch.Groups[2].Value -cne 'blob') { continue }
    if (-not $seenBlobs.Add($objectId)) { continue }
    $historyBlobs++
    if (Test-ForbiddenPath $path) { Add-Offender $path }
    $size = 0L
    if (-not [long]::TryParse($infoMatch.Groups[3].Value, [Globalization.NumberStyles]::None, [Globalization.CultureInfo]::InvariantCulture, [ref]$size)) {
        throw 'Git returned a noncanonical history blob size.'
    }
    if ($size -gt 1MB) {
        Add-Offender $path
    } else {
        Test-BlobText $objectId $path
    }
    $blobSizes[$objectId] = $size
}

$historyEntries = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$commits = @(Invoke-Git @('rev-list','--all') 'Complete history commit listing')
foreach ($commit in $commits) {
    if ($commit -cnotmatch '^[0-9a-f]{40,64}$') { throw 'Git returned a noncanonical history commit.' }
    $treeLines = @(Invoke-Git @('-c','core.quotepath=false','ls-tree','-r','--full-tree',$commit) "History tree $commit")
    foreach ($treeLine in $treeLines) {
        $treeMatch = [regex]::Match($treeLine, '^([0-7]{6})\s+(blob|commit)\s+([0-9a-f]{40,64})\t(.+)$')
        if (-not $treeMatch.Success) { throw 'Git returned a noncanonical history tree entry.' }
        $mode = $treeMatch.Groups[1].Value
        $type = $treeMatch.Groups[2].Value
        $objectId = $treeMatch.Groups[3].Value
        $path = $treeMatch.Groups[4].Value
        if ($type -cne 'blob' -or $mode -ceq '120000') { Add-Offender $path; continue }
        if (-not $historyEntries.Add("$objectId`t$path")) { continue }
        if (Test-ForbiddenPath $path) { Add-Offender $path }
        if (-not $blobSizes.ContainsKey($objectId)) {
            $sizeOutput = @(Invoke-Git @('cat-file','-s',$objectId) "History blob size $objectId")
            $size = 0L
            if ($sizeOutput.Count -ne 1 -or -not [long]::TryParse($sizeOutput[0].Trim(), [Globalization.NumberStyles]::None, [Globalization.CultureInfo]::InvariantCulture, [ref]$size)) {
                throw 'Git returned a noncanonical history blob size.'
            }
            $blobSizes[$objectId] = $size
        }
        if ($blobSizes[$objectId] -gt 1MB) { Add-Offender $path; continue }
        Test-BlobText $objectId $path
    }
}

Write-Output "trackedPaths=$($tracked.Count)"
Write-Output "historyObjects=$historyObjects"
Write-Output "historyBlobs=$historyBlobs"
if ($script:Offenders.Count -ne 0) {
    $names = @($script:Offenders | Sort-Object)
    throw "Repository audit failed for dirty, unsafe, or leaked paths: $($names -join ', ')"
}
