#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$RepositoryRoot,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
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

function Assert-SafeDirectoryTree([string]$Path, [string]$Label) {
    Assert-NoReparseAncestor $Path $Label
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { throw "$Label does not exist: $Path" }
    foreach ($item in Get-ChildItem -LiteralPath $Path -Force -Recurse) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "$Label contains a symlink or reparse point: $($item.FullName)" }
    }
}

function Invoke-OwnedDirectoryCleanup([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    Assert-SafeDirectoryTree $Path 'Packager-owned directory'
    [IO.Directory]::Delete($Path, $true)
}

function Get-PathLockName([string]$Path) {
    $identity = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Path))
    if ($IsWindows) { $identity = $identity.ToUpperInvariant() }
    $bytes = [Text.Encoding]::UTF8.GetBytes($identity)
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
    return "FarmTogether2.ModKit.Pack.$hash"
}

function Get-PackageFingerprint([string]$Path) {
    Assert-SafeDirectoryTree $Path 'Package fingerprint directory'
    $lines = @(Get-ChildItem -LiteralPath $Path -Force -File | Sort-Object Name | ForEach-Object {
        "$($_.Name):$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())"
    })
    $bytes = [Text.Encoding]::UTF8.GetBytes(($lines -join "`n") + "`n")
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Write-PackJournal([string]$Path, [Collections.IDictionary]$Value) {
    Assert-NoReparseAncestor $Path 'Pack transaction journal'
    if (Test-Path -LiteralPath $Path) { throw "Pack transaction journal already exists: $Path" }
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Value | ConvertTo-Json -Compress) + "`n")
    $temporary = "$Path.$([guid]::NewGuid().ToString('N')).preparing"
    try {
        $stream = [IO.FileStream]::new($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None, 4096, [IO.FileOptions]::WriteThrough)
        try {
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush($true)
        } finally { $stream.Dispose() }
        [IO.File]::Move($temporary, $Path, $false)
    } finally {
        if (Test-Path -LiteralPath $temporary) { [IO.File]::Delete($temporary) }
    }
}

function Invoke-PackJournalCleanup([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    Assert-NoReparseAncestor $Path 'Pack transaction journal'
    $item = Get-Item -LiteralPath $Path -Force
    if (-not ($item -is [IO.FileInfo]) -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Pack transaction journal is not a regular file.' }
    [IO.File]::Delete($Path)
}

function Read-PackJournal([string]$Path) {
    Assert-NoReparseAncestor $Path 'Pack transaction journal'
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw 'Pack transaction journal is missing.' }
    $document = [Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($Path))
    try {
        $fields = @('schemaVersion','attempt','output','staging','backup','hadOutput','stagingFingerprint')
        $properties = [Collections.Generic.Dictionary[string, Text.Json.JsonElement]]::new([StringComparer]::Ordinal)
        foreach ($property in $document.RootElement.EnumerateObject()) {
            if (-not $properties.TryAdd($property.Name, $property.Value.Clone())) { throw 'Pack transaction journal contains duplicate fields.' }
        }
        if ($properties.Count -ne $fields.Count -or @($fields | Where-Object { -not $properties.ContainsKey($_) }).Count -ne 0) { throw 'Pack transaction journal has an invalid closed schema.' }
        $schema = 0
        if (-not $properties['schemaVersion'].TryGetInt32([ref]$schema) -or $schema -ne 1 -or $properties['schemaVersion'].GetRawText() -cne '1') { throw 'Pack transaction journal schemaVersion is invalid.' }
        foreach ($field in 'attempt','output','staging','backup','stagingFingerprint') {
            if ($properties[$field].ValueKind -ne [Text.Json.JsonValueKind]::String) { throw "Pack transaction journal field $field is invalid." }
        }
        if ($properties['hadOutput'].ValueKind -notin @([Text.Json.JsonValueKind]::True, [Text.Json.JsonValueKind]::False)) { throw 'Pack transaction journal hadOutput is invalid.' }
        return [pscustomobject]@{
            Attempt = $properties['attempt'].GetString()
            Output = $properties['output'].GetString()
            Staging = $properties['staging'].GetString()
            Backup = $properties['backup'].GetString()
            HadOutput = $properties['hadOutput'].GetBoolean()
            StagingFingerprint = $properties['stagingFingerprint'].GetString()
        }
    } finally { $document.Dispose() }
}

function Invoke-PackOperationPoint([string]$Name) {
    if ([string]::Equals($env:FARMT2_PACK_HARD_CRASH_AT, $Name, [StringComparison]::Ordinal)) { [Environment]::Exit(86) }
    if ([string]::Equals($env:FARMT2_PACK_FAIL_AT, $Name, [StringComparison]::Ordinal)) { throw "Injected pack failure at $Name." }
}

function Invoke-Tool([string[]]$Arguments) {
    $toolRunner = Join-Path $PSScriptRoot 'Invoke-ModKitTool.ps1'
    & $toolRunner @Arguments
    if ($LASTEXITCODE -ne 0) { throw "ModKit tool failed with exit code $LASTEXITCODE." }
}

function Restore-PackTransaction([string]$Journal, [string]$Output, [string]$Parent, [string]$Leaf, [string]$ModConfig) {
    if (-not (Test-Path -LiteralPath $Journal)) { return }
    $state = Read-PackJournal $Journal
    if ($state.Attempt -cnotmatch '^[0-9a-f]{32}$') { throw 'Pack transaction journal attempt is invalid.' }
    $expectedStaging = Join-Path $Parent ".$Leaf.modkit-pack-$($state.Attempt).preparing"
    $expectedBackup = Join-Path $Parent ".$Leaf.modkit-pack-$($state.Attempt).backup"
    if (-not [string]::Equals([IO.Path]::GetFullPath($state.Output), $Output, $script:PathComparison) -or
        -not [string]::Equals([IO.Path]::GetFullPath($state.Staging), $expectedStaging, $script:PathComparison) -or
        -not [string]::Equals([IO.Path]::GetFullPath($state.Backup), $expectedBackup, $script:PathComparison) -or
        $state.StagingFingerprint -cnotmatch '^[0-9a-f]{64}$') { throw 'Pack transaction journal path or fingerprint identity is invalid.' }
    foreach ($path in @($Output, $expectedStaging, $expectedBackup)) { Assert-NoReparseAncestor $path 'Pack recovery path' }
    $outputExists = Test-Path -LiteralPath $Output -PathType Container
    $stagingExists = Test-Path -LiteralPath $expectedStaging -PathType Container
    $backupExists = Test-Path -LiteralPath $expectedBackup -PathType Container

    if (-not $outputExists -and $backupExists) {
        [IO.Directory]::Move($expectedBackup, $Output)
        if (@(Get-ChildItem -LiteralPath $Output -Force).Count -ne 0) {
            & (Join-Path $PSScriptRoot 'Test-PlayerPackage.ps1') -ModConfig $ModConfig -Artifacts $Output
        }
        if ($stagingExists) { Invoke-OwnedDirectoryCleanup $expectedStaging }
        Invoke-PackJournalCleanup $Journal
        return
    }
    if ($outputExists -and $backupExists) {
        & (Join-Path $PSScriptRoot 'Test-PlayerPackage.ps1') -ModConfig $ModConfig -Artifacts $Output
        if ((Get-PackageFingerprint $Output) -cne $state.StagingFingerprint) { throw 'Recovered output does not match the interrupted pack attempt; refusing to mutate either directory.' }
        Invoke-OwnedDirectoryCleanup $expectedBackup
        if ($stagingExists) { Invoke-OwnedDirectoryCleanup $expectedStaging }
        Invoke-PackJournalCleanup $Journal
        return
    }
    if ($outputExists -and -not $backupExists -and -not $stagingExists) {
        & (Join-Path $PSScriptRoot 'Test-PlayerPackage.ps1') -ModConfig $ModConfig -Artifacts $Output
        if ((Get-PackageFingerprint $Output) -cne $state.StagingFingerprint) { throw 'Recovered output does not match the interrupted pack attempt.' }
        Invoke-PackJournalCleanup $Journal
        return
    }
    if ($outputExists -and $stagingExists -and -not $backupExists -and $state.HadOutput) {
        Invoke-OwnedDirectoryCleanup $expectedStaging
        Invoke-PackJournalCleanup $Journal
        return
    }
    if (-not $outputExists -and $stagingExists -and -not $backupExists -and -not $state.HadOutput) {
        Invoke-OwnedDirectoryCleanup $expectedStaging
        Invoke-PackJournalCleanup $Journal
        return
    }
    throw 'Interrupted pack transaction cannot be recovered without risking an unrelated directory.'
}

$root = [IO.Path]::GetFullPath($RepositoryRoot)
Assert-SafeDirectoryTree $root 'Repository root'
$modConfig = Join-Path $root 'mod.json'
if (-not (Test-Path -LiteralPath $modConfig -PathType Leaf)) { throw 'Repository-root mod.json does not exist.' }
Invoke-Tool @('mod-config','verify','--file',$modConfig)
$document = [Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($modConfig))
try {
    if ($document.RootElement.ValueKind -ne [Text.Json.JsonValueKind]::Object) { throw 'mod.json must be an object.' }
    $projects = @($document.RootElement.EnumerateObject() | Where-Object Name -ceq 'project')
    if ($projects.Count -ne 1 -or $projects[0].Value.ValueKind -ne [Text.Json.JsonValueKind]::String) { throw 'mod.json project is invalid.' }
    $projectRelative = $projects[0].Value.GetString()
} finally { $document.Dispose() }
if ([string]::IsNullOrWhiteSpace($projectRelative) -or @($projectRelative.ToCharArray() | Where-Object { [char]::IsControl($_) }).Count -ne 0 -or
    $projectRelative.Contains('\') -or $projectRelative.Contains(':') -or $projectRelative.StartsWith('/') -or
    @($projectRelative.Split('/') | Where-Object { $_ -in @('','.', '..') }).Count -ne 0) { throw 'mod.json project is an unsafe relative path.' }
$project = [IO.Path]::GetFullPath((Join-Path $root $projectRelative))
if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { throw "Plugin project does not exist: $project" }

$propertyOutput = @(& dotnet msbuild $project -nologo "-property:Configuration=$Configuration" '-property:DeployToGame=false' '-getProperty:TargetPath' '-getProperty:Version')
if ($LASTEXITCODE -ne 0) { throw "MSBuild property query failed with exit code $LASTEXITCODE." }
try { $properties = ($propertyOutput -join [Environment]::NewLine) | ConvertFrom-Json -Depth 10 } catch { throw "MSBuild property query returned invalid JSON: $($_.Exception.Message)" }
$targetPath = [string]$properties.Properties.TargetPath
$version = [string]$properties.Properties.Version
if ([string]::IsNullOrWhiteSpace($targetPath) -or [string]::IsNullOrWhiteSpace($version)) { throw 'MSBuild did not return TargetPath and Version.' }
$pluginDll = [IO.Path]::GetFullPath($targetPath)
$pluginPdb = [IO.Path]::ChangeExtension($pluginDll, '.pdb')
foreach ($entry in @([pscustomobject]@{Path=$pluginDll;Label='Built plugin DLL'},[pscustomobject]@{Path=$pluginPdb;Label='Built plugin PDB'})) {
    Assert-NoReparseAncestor $entry.Path $entry.Label
    if (-not (Test-Path -LiteralPath $entry.Path -PathType Leaf)) { throw "$($entry.Label) does not exist: $($entry.Path)" }
}

$output = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($OutputDirectory))
$parent = [IO.Path]::GetDirectoryName($output)
if ([string]::IsNullOrEmpty($parent)) { throw 'OutputDirectory must have a parent directory.' }
$leaf = [IO.Path]::GetFileName($output)
if ([string]::IsNullOrWhiteSpace($leaf) -or $leaf -in @('.', '..')) { throw 'OutputDirectory has an unsafe leaf name.' }
$mutex = [Threading.Mutex]::new($false, (Get-PathLockName $output))
$mutexAcquired = $false
try {
    try { $mutexAcquired = $mutex.WaitOne() } catch [Threading.AbandonedMutexException] { $mutexAcquired = $true }
    if (-not $mutexAcquired) { throw 'Could not acquire the package output lock.' }
    Assert-SafeDirectoryTree $parent 'Output parent directory'
    $journal = Join-Path $parent ".$leaf.modkit-pack.transaction.json"
    Restore-PackTransaction $journal $output $parent $leaf $modConfig
    if (Test-Path -LiteralPath $output) {
        Assert-SafeDirectoryTree $output 'Existing output directory'
        if (@(Get-ChildItem -LiteralPath $output -Force).Count -ne 0) {
            & (Join-Path $PSScriptRoot 'Test-PlayerPackage.ps1') -ModConfig $modConfig -Artifacts $output
            if ($LASTEXITCODE -ne 0) { throw 'Existing output directory is not a ModKit-owned package set.' }
        }
    }

    $attempt = [guid]::NewGuid().ToString('N')
    $staging = Join-Path $parent ".$leaf.modkit-pack-$attempt.preparing"
    $backup = Join-Path $parent ".$leaf.modkit-pack-$attempt.backup"
    if ((Test-Path -LiteralPath $staging) -or (Test-Path -LiteralPath $backup)) { throw 'Packager attempt paths already exist.' }
    [IO.Directory]::CreateDirectory($staging) | Out-Null
    $backedUp = $false
    $promoted = $false
    $complete = $false
    $journalWritten = $false
    $stagingFingerprint = $null
    try {
        Invoke-Tool @(
            'mod-package','write',
            '--mod-config',$modConfig,
            '--repository-root',$root,
            '--plugin-dll',$pluginDll,
            '--plugin-pdb',$pluginPdb,
            '--msbuild-version',$version,
            '--output',$staging
        )
        & (Join-Path $PSScriptRoot 'Test-PlayerPackage.ps1') -ModConfig $modConfig -Artifacts $staging
        if ($LASTEXITCODE -ne 0) { throw 'Prepared player package verification failed.' }
        $stagingFingerprint = Get-PackageFingerprint $staging
        $hadOutput = Test-Path -LiteralPath $output
        Write-PackJournal $journal ([ordered]@{
            schemaVersion = 1
            attempt = $attempt
            output = $output
            staging = $staging
            backup = $backup
            hadOutput = [bool]$hadOutput
            stagingFingerprint = $stagingFingerprint
        })
        $journalWritten = $true
        if ($hadOutput) {
            [IO.Directory]::Move($output, $backup)
            $backedUp = $true
        }
        Invoke-PackOperationPoint 'after-backup'
        [IO.Directory]::Move($staging, $output)
        $promoted = $true
        Invoke-PackOperationPoint 'after-promote'
        & (Join-Path $PSScriptRoot 'Test-PlayerPackage.ps1') -ModConfig $modConfig -Artifacts $output
        if ($LASTEXITCODE -ne 0 -or (Get-PackageFingerprint $output) -cne $stagingFingerprint) { throw 'Promoted player package verification failed.' }
        $complete = $true
        if ($backedUp) { Invoke-OwnedDirectoryCleanup $backup; $backedUp = $false }
        Invoke-PackJournalCleanup $journal
        $journalWritten = $false
    } finally {
        if (-not $complete) {
            if ($promoted -and (Test-Path -LiteralPath $output)) {
                if ((Get-PackageFingerprint $output) -cne $stagingFingerprint) { throw 'Package rollback refuses to remove an output that no longer matches this attempt.' }
                Invoke-OwnedDirectoryCleanup $output
                $promoted = $false
            }
            if ($backedUp -and -not (Test-Path -LiteralPath $output) -and (Test-Path -LiteralPath $backup)) {
                [IO.Directory]::Move($backup, $output)
                $backedUp = $false
            }
            if ($journalWritten -and -not $backedUp -and (Test-Path -LiteralPath $output)) {
                Invoke-PackJournalCleanup $journal
                $journalWritten = $false
            }
        }
        if (Test-Path -LiteralPath $staging) { Invoke-OwnedDirectoryCleanup $staging }
        if (-not $complete -and $backedUp -and (Test-Path -LiteralPath $backup)) { throw "Package rollback left a backup directory: $backup" }
    }
} finally {
    if ($mutexAcquired) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
