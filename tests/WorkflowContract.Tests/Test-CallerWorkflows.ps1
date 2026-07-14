#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$RepositoryRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-RegularFile([string]$Path, [string]$Label) {
    $full = [IO.Path]::GetFullPath($Path)
    $current = $full
    while (-not [string]::IsNullOrEmpty($current)) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "$Label contains a symlink or reparse-point ancestor." }
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ([string]::IsNullOrEmpty($parent) -or $parent -ceq $current) { break }
        $current = $parent
    }
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { throw "$Label does not exist." }
}

function Read-Workflow([string]$Path, [string]$Label) {
    Assert-RegularFile $Path $Label
    $text = [IO.File]::ReadAllText($Path)
    if ($text.Contains("`r")) { throw "$Label must use LF line endings." }
    if ($text -match '(?im)^\s*pull_request_target\s*:' -or $text -match '(?im)^\s*secrets\s*:\s*inherit\s*(?:#.*)?$') {
        throw "$Label contains a forbidden trigger or inherited secrets."
    }
    return $text
}

function Assert-PinnedReference([string]$Text, [string]$Commit, [string]$Label) {
    $uses = @([regex]::Matches($Text, '(?im)^\s*uses\s*:\s*([^\s#]+)\s*$'))
    if ($uses.Count -eq 0) { throw "$Label contains no uses reference." }
    foreach ($match in $uses) {
        $value = $match.Groups[1].Value
        if ($value -cnotmatch '@([0-9a-f]{40})$' -or $Matches[1] -cne $Commit) {
            throw "$Label contains a uses reference that is not pinned to modkit.lock.json workflowCommit."
        }
    }
}

$root = [IO.Path]::GetFullPath($RepositoryRoot)
$lockPath = Join-Path $root 'modkit.lock.json'
Assert-RegularFile $lockPath 'modkit.lock.json'
$document = [Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($lockPath))
try {
    if ($document.RootElement.ValueKind -ne [Text.Json.JsonValueKind]::Object) { throw 'modkit.lock.json must be an object.' }
    $commitFields = @($document.RootElement.EnumerateObject() | Where-Object Name -ceq 'workflowCommit')
    if ($commitFields.Count -ne 1 -or $commitFields[0].Value.ValueKind -ne [Text.Json.JsonValueKind]::String) { throw 'modkit.lock.json workflowCommit is missing or duplicated.' }
    $commit = $commitFields[0].Value.GetString()
} finally { $document.Dispose() }
if ($commit -cnotmatch '^[0-9a-f]{40}$') { throw 'modkit.lock.json workflowCommit must be lowercase 40-hex.' }

$ci = Read-Workflow (Join-Path $root '.github/workflows/ci.yml') 'Caller CI workflow'
$release = Read-Workflow (Join-Path $root '.github/workflows/release.yml') 'Caller release workflow'
Assert-PinnedReference $ci $commit 'Caller CI workflow'
Assert-PinnedReference $release $commit 'Caller release workflow'

$escaped = [regex]::Escape($commit)
if ($ci -cnotmatch "(?m)^\s*modkit-commit\s*:\s*$escaped\s*$" -or
    $ci -cnotmatch '(?m)^\s*candidate-kind\s*:' -or
    $ci -cnotmatch '(?m)^\s*retention-days\s*:\s*7\s*$') {
    throw 'Caller CI workflow is missing the locked publish inputs.'
}
if ($release -cnotmatch "(?m)^\s*modkit-commit\s*:\s*$escaped\s*$" -or
    $release -cnotmatch '(?m)^\s*tag\s*:' -or
    $release -cnotmatch '(?m)^\s*workflow_dispatch\s*:') {
    throw 'Caller release workflow is missing tag or modkit-commit inputs.'
}
$modKitSecret = '(?m)^\s*modkit_read_token\s*:\s*\$\{\{\s*secrets\.MODKIT_READ_TOKEN\s*\}\}\s*$'
if (@([regex]::Matches($ci, $modKitSecret)).Count -ne 1 -or
    @([regex]::Matches($release, $modKitSecret)).Count -ne 1) {
    throw 'Caller workflows must explicitly pass the one private ModKit read token.'
}
$modKitSecretTail = '(?s)\n    secrets:\n      modkit_read_token:\s*\$\{\{\s*secrets\.MODKIT_READ_TOKEN\s*\}\}\s*\n?\z'
if (-not [regex]::IsMatch($ci, $modKitSecretTail) -or -not [regex]::IsMatch($release, $modKitSecretTail)) {
    throw 'Caller workflow secret maps must contain only the private ModKit read token.'
}
$releasePermissionBlock = '(?m)^    permissions:\n      actions: read\n      attestations: read\n      contents: write\n    uses:'
if (-not [regex]::IsMatch($release, $releasePermissionBlock)) {
    throw 'Caller release workflow must grant only actions read, attestations read, and contents write.'
}
if ($release -match '(?im)uses\s*:\s*actions/checkout@' -and $release -notmatch '(?im)\bref\s*:\s*\$\{\{\s*inputs\.tag\s*\}\}') {
    throw 'Caller release dispatch may not check out default-branch code.'
}

Write-Output '[caller-workflows] OK'
