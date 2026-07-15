#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$LockFile,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$Destination,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$PropsOutput
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:PathComparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
$script:LockFields = @('schemaVersion','repository','workflowCommit','packageId','packageVersion','releaseTag','assetName','sha256')

function Get-NormalizedPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw 'A required resolver path is empty.'
    }
    return [IO.Path]::GetFullPath($Path)
}

function Assert-NoReparseAncestor([string]$Path, [string]$Label) {
    $current = Get-NormalizedPath $Path
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

function Assert-DirectoryTreeHasNoReparsePoint([string]$Path, [string]$Label) {
    Assert-NoReparseAncestor $Path $Label
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "$Label does not exist: $Path"
    }
    foreach ($item in Get-ChildItem -LiteralPath $Path -Force -Recurse) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Label contains a symlink or reparse point: $($item.FullName)"
        }
    }
}

function Get-StrictPropertyMap(
    [Text.Json.JsonElement]$Element,
    [string[]]$Expected,
    [string]$Label
) {
    if ($Element.ValueKind -ne [Text.Json.JsonValueKind]::Object) {
        throw "$Label must be a JSON object."
    }
    $properties = [Collections.Generic.Dictionary[string, Text.Json.JsonElement]]::new([StringComparer]::Ordinal)
    foreach ($property in $Element.EnumerateObject()) {
        if (-not $properties.TryAdd($property.Name, $property.Value.Clone())) {
            throw "$Label contains duplicate field $($property.Name)."
        }
    }
    if ($properties.Count -ne $Expected.Count) {
        throw "$Label has missing or extra fields."
    }
    foreach ($field in $Expected) {
        if (-not $properties.ContainsKey($field)) {
            throw "$Label is missing or incorrectly cased field $field."
        }
    }
    return ,$properties
}

function Read-ClosedLock([string]$Path) {
    Assert-RegularFile $Path 'ModKit lock'
    $options = [Text.Json.JsonDocumentOptions]::new()
    $options.AllowTrailingCommas = $false
    $options.CommentHandling = [Text.Json.JsonCommentHandling]::Disallow
    try {
        $document = [Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($Path), $options)
    } catch {
        throw "ModKit lock is invalid JSON: $($_.Exception.Message)"
    }
    try {
        $properties = Get-StrictPropertyMap $document.RootElement $script:LockFields 'ModKit lock'
        $schema = 0
        if ($properties['schemaVersion'].ValueKind -ne [Text.Json.JsonValueKind]::Number -or
            -not $properties['schemaVersion'].TryGetInt32([ref]$schema) -or $schema -ne 1 -or
            $properties['schemaVersion'].GetRawText() -cne '1') {
            throw 'ModKit lock schemaVersion must be the JSON integer 1.'
        }
        $values = [ordered]@{ schemaVersion = 1 }
        foreach ($field in $script:LockFields | Where-Object { $_ -cne 'schemaVersion' }) {
            if ($properties[$field].ValueKind -ne [Text.Json.JsonValueKind]::String) {
                throw "ModKit lock field $field must be a JSON string."
            }
            $values[$field] = $properties[$field].GetString()
        }
        $lock = [pscustomobject]$values
    } finally {
        $document.Dispose()
    }

    $repositorySegments = @([string]$lock.repository -split '/')
    if ($lock.repository -cnotmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or
        $repositorySegments -contains '.' -or $repositorySegments -contains '..' -or
        $lock.workflowCommit -cnotmatch '^[0-9a-f]{40}$' -or
        $lock.packageId -cne 'FarmTogether2.GameApi.Ref' -or
        $lock.packageVersion -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$' -or
        $lock.releaseTag -cne "v$($lock.packageVersion)" -or
        $lock.assetName -cne "$($lock.packageId).$($lock.packageVersion).nupkg" -or
        $lock.sha256 -cnotmatch '^[0-9a-f]{64}$') {
        throw 'ModKit lock identity is noncanonical.'
    }
    return $lock
}

function Assert-LockValuesEqual([object]$Expected, [object]$Actual) {
    foreach ($field in $script:LockFields) {
        if ([string]$Actual.$field -cne [string]$Expected.$field) {
            throw "ModKit lock changed during resolution at $field."
        }
    }
}

function Invoke-GhJson([string]$ApiPath) {
    $output = @(& gh api --method GET `
        --header 'Accept: application/vnd.github+json' `
        --header 'X-GitHub-Api-Version: 2026-03-10' `
        $ApiPath)
    if ($LASTEXITCODE -ne 0) {
        throw "gh api failed for $ApiPath with exit code $LASTEXITCODE."
    }
    if ($output.Count -eq 0) {
        throw "gh api returned no JSON for $ApiPath."
    }
    try {
        return ($output -join [Environment]::NewLine) | ConvertFrom-Json -Depth 30
    } catch {
        throw "gh api returned invalid JSON for ${ApiPath}: $($_.Exception.Message)"
    }
}

function Get-RequiredProperty([object]$Object, [string]$Name, [string]$Label) {
    if ($null -eq $Object) {
        throw "$Label is missing."
    }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) {
        throw "$Label is missing."
    }
    return $property.Value
}

function Get-ReleaseCommit([string]$Repository, [string]$ReleaseTag) {
    $reference = Invoke-GhJson "repos/$Repository/git/ref/tags/$ReleaseTag"
    $object = Get-RequiredProperty $reference 'object' 'Tag object'
    for ($depth = 0; $depth -lt 8; $depth++) {
        $type = Get-RequiredProperty $object 'type' 'Git object type'
        $sha = Get-RequiredProperty $object 'sha' 'Git object sha'
        if (-not ($type -is [string]) -or -not ($sha -is [string]) -or $sha -cnotmatch '^[0-9a-f]{40}$') {
            throw 'Tag resolution returned a noncanonical Git object.'
        }
        if ($type -ceq 'commit') {
            return $sha
        }
        if ($type -cne 'tag') {
            throw "Tag resolves to unsupported Git object type: $type"
        }
        $tagObject = Invoke-GhJson "repos/$Repository/git/tags/$sha"
        $object = Get-RequiredProperty $tagObject 'object' 'Annotated tag object'
    }
    throw 'Annotated tag chain is too deep.'
}

function Get-ReleaseSnapshot([object]$Lock) {
    $release = Invoke-GhJson "repos/$($Lock.repository)/releases/tags/$($Lock.releaseTag)"
    $releaseId = Get-RequiredProperty $release 'id' 'Release id'
    $releaseTag = Get-RequiredProperty $release 'tag_name' 'Release tag_name'
    $draft = Get-RequiredProperty $release 'draft' 'Release draft state'
    $prerelease = Get-RequiredProperty $release 'prerelease' 'Release prerelease state'
    if (-not ($releaseId -is [long] -or $releaseId -is [int]) -or [long]$releaseId -le 0 -or
        -not ($releaseTag -is [string]) -or $releaseTag -cne [string]$Lock.releaseTag -or
        -not ($draft -is [bool]) -or $draft -or
        -not ($prerelease -is [bool]) -or $prerelease) {
        throw 'Locked Release is not the requested stable published tag.'
    }
    $assets = @(Get-RequiredProperty $release 'assets' 'Release assets')
    $assetMatches = @($assets | Where-Object {
        $name = $_.PSObject.Properties['name']
        $null -ne $name -and $name.Value -is [string] -and $name.Value -ceq [string]$Lock.assetName
    })
    if ($assetMatches.Count -ne 1) {
        throw 'Locked Release must contain exactly one matching reference package asset.'
    }
    $asset = $assetMatches[0]
    $assetId = Get-RequiredProperty $asset 'id' 'Release asset id'
    $assetState = Get-RequiredProperty $asset 'state' 'Release asset state'
    $assetSize = Get-RequiredProperty $asset 'size' 'Release asset size'
    if (-not ($assetId -is [long] -or $assetId -is [int]) -or [long]$assetId -le 0 -or
        -not ($assetState -is [string]) -or $assetState -cne 'uploaded' -or
        -not ($assetSize -is [long] -or $assetSize -is [int]) -or [long]$assetSize -le 0) {
        throw 'Locked Release asset is not a nonempty uploaded file.'
    }
    $digestProperty = $asset.PSObject.Properties['digest']
    $digest = if ($null -eq $digestProperty -or $null -eq $digestProperty.Value) { $null } else { [string]$digestProperty.Value }
    if ($null -ne $digest -and $digest -cne "sha256:$($Lock.sha256)") {
        throw 'Locked Release asset API digest differs from modkit.lock.json.'
    }
    $commit = Get-ReleaseCommit ([string]$Lock.repository) ([string]$Lock.releaseTag)
    if ($commit -cne [string]$Lock.workflowCommit) {
        throw 'Locked Release tag does not resolve to workflowCommit.'
    }
    return [pscustomobject]@{
        releaseId = [long]$releaseId
        assetId = [long]$assetId
        assetSize = [long]$assetSize
        assetDigest = $digest
        commit = $commit
    }
}

function Assert-ReleaseSnapshotEqual([object]$Expected, [object]$Actual) {
    foreach ($field in 'releaseId','assetId','assetSize','assetDigest','commit') {
        if ([string]$Actual.$field -cne [string]$Expected.$field) {
            throw "Locked Release identity changed during resolution at $field."
        }
    }
}

function Invoke-Git([string[]]$Arguments, [string]$Label) {
    $output = @(& git --no-replace-objects @Arguments)
    if ($LASTEXITCODE -ne 0) {
        throw "$Label failed with exit code $LASTEXITCODE."
    }
    return $output
}

function Invoke-GitProbe([string[]]$Arguments) {
    $previousPreference = $PSNativeCommandUseErrorActionPreference
    try {
        $PSNativeCommandUseErrorActionPreference = $false
        $output = @(& git --no-replace-objects @Arguments 2>$null)
        $exitCode = $LASTEXITCODE
    } finally {
        $PSNativeCommandUseErrorActionPreference = $previousPreference
    }
    return [pscustomobject]@{
        Succeeded = $exitCode -eq 0
        Output = [string[]]$output
    }
}

function Invoke-ResolvePoint([string]$Name) {
    if ($env:FARMT2_MODKIT_RESOLVE_FAIL_AT -ceq $Name) {
        throw "Injected ModKit resolver failure at $Name."
    }
}

function Invoke-OwnedPathCleanup([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Resolver-owned cleanup path became a reparse point: $Path"
    }
    if ($item.PSIsContainer) {
        Assert-DirectoryTreeHasNoReparsePoint $Path 'Resolver-owned cleanup tree'
        [IO.Directory]::Delete($Path, $true)
    } else {
        [IO.File]::Delete($Path)
    }
}

function Get-TreeFingerprint([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        return '<missing>'
    }
    Assert-DirectoryTreeHasNoReparsePoint $Path 'Cache fingerprint tree'
    $lines = [Collections.Generic.List[string]]::new()
    foreach ($directory in Get-ChildItem -LiteralPath $Path -Directory -Force -Recurse | Sort-Object FullName) {
        $relative = [IO.Path]::GetRelativePath($Path, $directory.FullName).Replace('\','/')
        $lines.Add("D`t$relative")
    }
    foreach ($file in Get-ChildItem -LiteralPath $Path -File -Force -Recurse | Sort-Object FullName) {
        $relative = [IO.Path]::GetRelativePath($Path, $file.FullName).Replace('\','/')
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $lines.Add("F`t$relative`t$hash")
    }
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($lines -join "`n") + "`n")
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Test-LiveCacheMatch(
    [string]$Packages,
    [string]$Tooling,
    [string]$Props,
    [object]$Lock,
    [string]$PropsContent,
    [string]$RemoteUrl
) {
    if (-not (Test-Path -LiteralPath $Packages -PathType Container) -or
        -not (Test-Path -LiteralPath $Tooling -PathType Container) -or
        -not (Test-Path -LiteralPath $Props -PathType Leaf)) {
        return $false
    }
    Assert-DirectoryTreeHasNoReparsePoint $Packages 'Live package cache'
    Assert-DirectoryTreeHasNoReparsePoint $Tooling 'Live tooling cache'
    Assert-RegularFile $Props 'Live props file'
    $packageEntries = @(Get-ChildItem -LiteralPath $Packages -Force)
    if ($packageEntries.Count -ne 1 -or -not ($packageEntries[0] -is [IO.FileInfo]) -or
        $packageEntries[0].Name -cne [string]$Lock.assetName -or
        (Get-FileHash -LiteralPath $packageEntries[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant() -cne [string]$Lock.sha256) {
        return $false
    }
    $head = Invoke-GitProbe @('-C',$Tooling,'rev-parse','HEAD')
    $branch = Invoke-GitProbe @('-C',$Tooling,'rev-parse','--abbrev-ref','HEAD')
    $storedRemoteUrls = Invoke-GitProbe @('-C',$Tooling,'config','--local','--get-all','remote.origin.url')
    $status = Invoke-GitProbe @('-C',$Tooling,'status','--porcelain','--untracked-files=all')
    if (-not $head.Succeeded -or -not $branch.Succeeded -or -not $storedRemoteUrls.Succeeded -or -not $status.Succeeded) {
        return $false
    }
    return $head.Output.Count -eq 1 -and $head.Output[0].Trim() -ceq [string]$Lock.workflowCommit -and
        $branch.Output.Count -eq 1 -and $branch.Output[0].Trim() -ceq 'HEAD' -and
        $storedRemoteUrls.Output.Count -eq 1 -and $storedRemoteUrls.Output[0] -ceq $RemoteUrl -and
        $status.Output.Count -eq 0 -and [IO.File]::ReadAllText($Props) -ceq $PropsContent
}

$lockPath = Get-NormalizedPath $LockFile
$repositoryRoot = Split-Path -Parent $lockPath
if ((Split-Path -Leaf $lockPath) -cne 'modkit.lock.json' -or
    -not (Test-Path -LiteralPath $repositoryRoot -PathType Container)) {
    throw 'LockFile must be the repository-root modkit.lock.json.'
}
$modKitRoot = Join-Path $repositoryRoot '.modkit'
$packages = Get-NormalizedPath $Destination
$props = Get-NormalizedPath $PropsOutput
$tooling = Join-Path $modKitRoot 'tooling'
$expectedPackages = Join-Path $modKitRoot 'packages'
$expectedProps = Join-Path $modKitRoot 'generated/ModKit.lock.props'
if (-not [string]::Equals($packages, $expectedPackages, $script:PathComparison) -or
    -not [string]::Equals($props, $expectedProps, $script:PathComparison)) {
    throw 'Destination and PropsOutput must be the repository-owned .modkit package and generated paths.'
}
foreach ($entry in @(
    [pscustomobject]@{ Path = $repositoryRoot; Label = 'Repository root' },
    [pscustomobject]@{ Path = $modKitRoot; Label = 'ModKit cache root' },
    [pscustomobject]@{ Path = $packages; Label = 'Package destination' },
    [pscustomobject]@{ Path = $tooling; Label = 'Tooling destination' },
    [pscustomobject]@{ Path = $props; Label = 'Props output' }
)) {
    Assert-NoReparseAncestor $entry.Path $entry.Label
}
$lock = Read-ClosedLock $lockPath
$nugetPackages = Join-Path $modKitRoot "nuget-packages/$($lock.sha256)"
Assert-NoReparseAncestor $nugetPackages 'Digest-keyed NuGet package cache'
$lockHash = (Get-FileHash -LiteralPath $lockPath -Algorithm SHA256).Hash.ToLowerInvariant()
$initialRelease = Get-ReleaseSnapshot $lock

$attempt = [guid]::NewGuid().ToString('N')
$downloadRoot = Join-Path ([IO.Path]::GetTempPath()) "farmtogether2-modkit-resolve-$attempt"
$stagingRoot = Join-Path $repositoryRoot ".modkit-resolve-$attempt.preparing"
$packagesPreparing = Join-Path $stagingRoot 'packages'
$toolingPreparing = Join-Path $stagingRoot 'tooling'
$propsPreparing = Join-Path $stagingRoot 'generated/ModKit.lock.props'
$packagesBackup = Join-Path $modKitRoot ".packages-$attempt.backup"
$toolingBackup = Join-Path $modKitRoot ".tooling-$attempt.backup"
$propsBackup = Join-Path $modKitRoot "generated/.ModKit.lock.props-$attempt.backup"
foreach ($path in @($downloadRoot,$stagingRoot,$packagesBackup,$toolingBackup,$propsBackup)) {
    if (Test-Path -LiteralPath $path) {
        throw "Resolver-owned attempt path already exists: $path"
    }
}
$downloadRemoved = $false
$stagingRemoved = $false
$transactionComplete = $false
try {
    Assert-NoReparseAncestor ([IO.Path]::GetDirectoryName($downloadRoot)) 'Download temporary parent'
    Invoke-ResolvePoint 'before-create-download-root'
    [IO.Directory]::CreateDirectory($downloadRoot) | Out-Null
    Invoke-ResolvePoint 'before-create-packages-preparing'
    [IO.Directory]::CreateDirectory($packagesPreparing) | Out-Null
    Invoke-ResolvePoint 'before-create-props-preparing'
    [IO.Directory]::CreateDirectory((Split-Path -Parent $propsPreparing)) | Out-Null

    & gh release download $lock.releaseTag -R $lock.repository `
        --pattern $lock.assetName `
        --dir $downloadRoot
    if ($LASTEXITCODE -ne 0) {
        throw "gh release download failed with exit code $LASTEXITCODE."
    }
    $downloaded = @(Get-ChildItem -LiteralPath $downloadRoot -File -Force)
    if ($downloaded.Count -ne 1 -or $downloaded[0].Name -cne [string]$lock.assetName) {
        throw 'Release download did not produce the one locked package asset.'
    }
    Assert-RegularFile $downloaded[0].FullName 'Downloaded ModKit package'
    if ((Get-FileHash -LiteralPath $downloaded[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant() -cne [string]$lock.sha256) {
        throw 'Downloaded ModKit package SHA-256 differs from modkit.lock.json.'
    }
    Invoke-ResolvePoint 'after-download-verify'

    $stagedPackage = Join-Path $packagesPreparing $lock.assetName
    [IO.File]::Copy($downloaded[0].FullName, $stagedPackage, $false)
    Assert-RegularFile $stagedPackage 'Staged ModKit package'
    if ((Get-FileHash -LiteralPath $stagedPackage -Algorithm SHA256).Hash.ToLowerInvariant() -cne [string]$lock.sha256) {
        throw 'Staged ModKit package hash mismatch.'
    }
    Invoke-ResolvePoint 'after-stage-package'

    $null = Invoke-Git @('init',$toolingPreparing) 'Tooling git init'
    $remoteUrl = "https://github.com/$($lock.repository).git"
    $null = Invoke-Git @('-C',$toolingPreparing,'remote','add','origin',$remoteUrl) 'Tooling remote add'
    $null = Invoke-Git @('-C',$toolingPreparing,'fetch','--no-tags','--depth=1','origin',$lock.workflowCommit) 'Tooling commit fetch'
    $null = Invoke-Git @('-C',$toolingPreparing,'checkout','--detach',$lock.workflowCommit) 'Tooling detached checkout'
    $head = @(Invoke-Git @('-C',$toolingPreparing,'rev-parse','HEAD') 'Tooling HEAD verification')
    $branch = @(Invoke-Git @('-C',$toolingPreparing,'rev-parse','--abbrev-ref','HEAD') 'Tooling detached-state verification')
    $storedRemoteUrls = @(Invoke-Git @('-C',$toolingPreparing,'config','--local','--get-all','remote.origin.url') 'Tooling stored remote verification')
    if ($head.Count -ne 1 -or $head[0].Trim() -cne [string]$lock.workflowCommit -or
        $branch.Count -ne 1 -or $branch[0].Trim() -cne 'HEAD' -or
        $storedRemoteUrls.Count -ne 1 -or $storedRemoteUrls[0] -cne $remoteUrl) {
        throw 'Fetched tooling checkout does not match the locked detached commit and repository.'
    }
    Assert-DirectoryTreeHasNoReparsePoint $toolingPreparing 'Fetched tooling checkout'
    Invoke-ResolvePoint 'after-checkout-tooling'

    $toolProject = Join-Path $toolingPreparing 'tools/FarmTogether2.ModKit.Tool/FarmTogether2.ModKit.Tool.csproj'
    Assert-RegularFile $toolProject 'Locked ModKit tool project'
    $toolBuildRoot = Join-Path $stagingRoot '.tool-build'
    $toolOutput = Join-Path $toolBuildRoot 'bin'
    $toolIntermediate = Join-Path $toolBuildRoot 'obj/'
    [IO.Directory]::CreateDirectory($toolOutput) | Out-Null
    [IO.Directory]::CreateDirectory($toolIntermediate) | Out-Null
    & dotnet restore $toolProject --locked-mode `
        "-p:BaseIntermediateOutputPath=$toolIntermediate"
    if ($LASTEXITCODE -ne 0) {
        throw 'Locked tooling restore failed.'
    }
    & dotnet build $toolProject -c Release --no-restore --output $toolOutput `
        "-p:BaseIntermediateOutputPath=$toolIntermediate"
    if ($LASTEXITCODE -ne 0) {
        throw 'Locked tooling build failed.'
    }
    $toolAssembly = Join-Path $toolOutput 'FarmTogether2.ModKit.Tool.dll'
    Assert-RegularFile $toolAssembly 'Built locked ModKit tool'
    & dotnet $toolAssembly lock verify --file $lockPath
    if ($LASTEXITCODE -ne 0) {
        throw 'Locked tooling rejected modkit.lock.json.'
    }
    & dotnet $toolAssembly ref-package verify `
        --package $stagedPackage --package-id $lock.packageId --version $lock.packageVersion
    if ($LASTEXITCODE -ne 0) {
        throw 'Locked tooling rejected the reference package.'
    }
    Invoke-OwnedPathCleanup $toolBuildRoot
    $toolLocalBuildDirectories = @(
        (Join-Path (Split-Path -Parent $toolProject) 'bin'),
        (Join-Path (Split-Path -Parent $toolProject) 'obj')
    )
    if (@($toolLocalBuildDirectories | Where-Object { Test-Path -LiteralPath $_ }).Count -ne 0) {
        throw 'Locked tooling verification wrote build output inside the checkout.'
    }
    $null = Invoke-Git @('-C',$toolingPreparing,'diff','--exit-code','HEAD','--') 'Tooling tracked-tree verification'
    $status = @(Invoke-Git @('-C',$toolingPreparing,'status','--porcelain','--untracked-files=all') 'Tooling status verification')
    if ($status.Count -ne 0) {
        throw 'Locked tooling checkout became dirty during verification.'
    }
    Assert-DirectoryTreeHasNoReparsePoint $toolingPreparing 'Prepared tooling checkout'

    $escapedSource = [Security.SecurityElement]::Escape($packages)
    $escapedNugetPackages = [Security.SecurityElement]::Escape($nugetPackages)
    $propsContent = @"
<Project>
  <PropertyGroup>
    <FarmTogether2GameApiRefVersion>$($lock.packageVersion)</FarmTogether2GameApiRefVersion>
    <FarmTogether2ModKitPackageSource>$escapedSource</FarmTogether2ModKitPackageSource>
    <RestorePackagesPath>$escapedNugetPackages</RestorePackagesPath>
  </PropertyGroup>
</Project>
"@.Replace("`r`n", "`n")
    [IO.File]::WriteAllText($propsPreparing, $propsContent, [Text.UTF8Encoding]::new($false))
    Assert-RegularFile $propsPreparing 'Prepared ModKit props'
    Invoke-ResolvePoint 'after-stage-props'

    Invoke-OwnedPathCleanup $downloadRoot
    $downloadRemoved = $true
    $finalLock = Read-ClosedLock $lockPath
    Assert-LockValuesEqual $lock $finalLock
    if ((Get-FileHash -LiteralPath $lockPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $lockHash) {
        throw 'ModKit lock bytes changed during resolution.'
    }
    $finalRelease = Get-ReleaseSnapshot $lock
    Assert-ReleaseSnapshotEqual $initialRelease $finalRelease

    foreach ($entry in @(
        [pscustomobject]@{ Path = $modKitRoot; Label = 'ModKit cache root' },
        [pscustomobject]@{ Path = $packages; Label = 'Package destination' },
        [pscustomobject]@{ Path = $tooling; Label = 'Tooling destination' },
        [pscustomobject]@{ Path = $props; Label = 'Props output' }
    )) {
        Assert-NoReparseAncestor $entry.Path $entry.Label
    }
    if (Test-LiveCacheMatch $packages $tooling $props $lock $propsContent $remoteUrl) {
        Invoke-OwnedPathCleanup $stagingRoot
        $stagingRemoved = $true
        return
    }

    if (Test-Path -LiteralPath $modKitRoot -PathType Leaf) {
        throw "Live ModKit cache root is a file: $modKitRoot"
    }
    if (Test-Path -LiteralPath $modKitRoot -PathType Container) {
        Assert-DirectoryTreeHasNoReparsePoint $modKitRoot 'Live ModKit cache root'
    }
    foreach ($directory in @($packages,$tooling)) {
        if (Test-Path -LiteralPath $directory -PathType Leaf) {
            throw "Live cache path is a file instead of a directory: $directory"
        }
    }
    if (Test-Path -LiteralPath $props -PathType Container) {
        throw "Live props path is a directory: $props"
    }

    $priorFingerprint = Get-TreeFingerprint $modKitRoot
    $createdRoot = -not (Test-Path -LiteralPath $modKitRoot)
    if ($createdRoot) {
        [IO.Directory]::CreateDirectory($modKitRoot) | Out-Null
    }
    $generatedRoot = Split-Path -Parent $props
    $createdGenerated = -not (Test-Path -LiteralPath $generatedRoot)
    if ($createdGenerated) {
        [IO.Directory]::CreateDirectory($generatedRoot) | Out-Null
    }
    Assert-DirectoryTreeHasNoReparsePoint $modKitRoot 'Live ModKit cache root'

    $packagesBackedUp = $false
    $toolingBackedUp = $false
    $propsBackedUp = $false
    $packagesPromoted = $false
    $toolingPromoted = $false
    $propsPromoted = $false
    try {
        if (Test-Path -LiteralPath $packages -PathType Container) {
            [IO.Directory]::Move($packages, $packagesBackup)
            $packagesBackedUp = $true
        }
        Invoke-ResolvePoint 'after-backup-packages'
        if (Test-Path -LiteralPath $tooling -PathType Container) {
            [IO.Directory]::Move($tooling, $toolingBackup)
            $toolingBackedUp = $true
        }
        Invoke-ResolvePoint 'after-backup-tooling'
        if (Test-Path -LiteralPath $props -PathType Leaf) {
            [IO.File]::Move($props, $propsBackup, $false)
            $propsBackedUp = $true
        }
        Invoke-ResolvePoint 'after-backup-props'

        [IO.Directory]::Move($packagesPreparing, $packages)
        $packagesPromoted = $true
        Invoke-ResolvePoint 'after-promote-packages'
        [IO.Directory]::Move($toolingPreparing, $tooling)
        $toolingPromoted = $true
        Invoke-ResolvePoint 'after-promote-tooling'
        [IO.File]::Move($propsPreparing, $props, $false)
        $propsPromoted = $true
        Invoke-ResolvePoint 'after-promote-props'

        if (-not (Test-LiveCacheMatch $packages $tooling $props $lock $propsContent $remoteUrl)) {
            throw 'Live ModKit cache does not match the locked package, tooling, and props after promotion.'
        }
        $transactionComplete = $true
        $cleanupErrors = [Collections.Generic.List[string]]::new()
        foreach ($cleanup in @(
            [pscustomobject]@{ Path = $stagingRoot; Label = 'staging tree'; FailurePoint = 'after-cleanup-staging'; IsStaging = $true },
            [pscustomobject]@{ Path = $propsBackup; Label = 'props backup'; FailurePoint = 'after-cleanup-props-backup'; IsStaging = $false },
            [pscustomobject]@{ Path = $toolingBackup; Label = 'tooling backup'; FailurePoint = 'after-cleanup-tooling-backup'; IsStaging = $false },
            [pscustomobject]@{ Path = $packagesBackup; Label = 'package backup'; FailurePoint = 'after-cleanup-packages-backup'; IsStaging = $false }
        )) {
            try {
                Invoke-OwnedPathCleanup $cleanup.Path
                if ($cleanup.IsStaging) {
                    $stagingRemoved = $true
                }
                Invoke-ResolvePoint $cleanup.FailurePoint
            } catch {
                $cleanupErrors.Add("$($cleanup.Label): $($_.Exception.Message)")
            }
        }
        if ($cleanupErrors.Count -ne 0) {
            throw "ModKit cache committed successfully, but cleanup failed: $($cleanupErrors -join '; ')"
        }
    } finally {
        if (-not $transactionComplete) {
            if ($propsPromoted -and (Test-Path -LiteralPath $props -PathType Leaf)) { [IO.File]::Delete($props) }
            if ($toolingPromoted -and (Test-Path -LiteralPath $tooling -PathType Container)) { Invoke-OwnedPathCleanup $tooling }
            if ($packagesPromoted -and (Test-Path -LiteralPath $packages -PathType Container)) { Invoke-OwnedPathCleanup $packages }
            if ($propsBackedUp -and (Test-Path -LiteralPath $propsBackup -PathType Leaf)) { [IO.File]::Move($propsBackup, $props, $false) }
            if ($toolingBackedUp -and (Test-Path -LiteralPath $toolingBackup -PathType Container)) { [IO.Directory]::Move($toolingBackup, $tooling) }
            if ($packagesBackedUp -and (Test-Path -LiteralPath $packagesBackup -PathType Container)) { [IO.Directory]::Move($packagesBackup, $packages) }
            if ($createdGenerated -and (Test-Path -LiteralPath $generatedRoot -PathType Container) -and
                @(Get-ChildItem -LiteralPath $generatedRoot -Force).Count -eq 0) {
                [IO.Directory]::Delete($generatedRoot)
            }
            if ($createdRoot -and (Test-Path -LiteralPath $modKitRoot -PathType Container) -and
                @(Get-ChildItem -LiteralPath $modKitRoot -Force).Count -eq 0) {
                [IO.Directory]::Delete($modKitRoot)
            }
            if ((Get-TreeFingerprint $modKitRoot) -cne $priorFingerprint) {
                throw 'ModKit cache rollback did not restore the exact prior tree.'
            }
        }
    }
    if (-not $transactionComplete) {
        throw 'ModKit cache transaction did not complete.'
    }
} finally {
    if (-not $downloadRemoved -and (Test-Path -LiteralPath $downloadRoot)) {
        Invoke-OwnedPathCleanup $downloadRoot
    }
    if (-not $transactionComplete -and -not $stagingRemoved -and (Test-Path -LiteralPath $stagingRoot)) {
        Invoke-OwnedPathCleanup $stagingRoot
    }
}
