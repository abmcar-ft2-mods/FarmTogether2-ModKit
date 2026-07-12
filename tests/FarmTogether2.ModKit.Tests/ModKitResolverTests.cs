using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mono.Cecil;
using Xunit;

namespace FarmTogether2.ModKit.Tests;

public sealed class ModKitResolverTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string AssetName = "FarmTogether2.GameApi.Ref.1.0.0.nupkg";
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private static readonly string Script = Path.Combine(Root, "scripts", "Resolve-ModKit.ps1");
    private static readonly string ToolProject = Path.Combine(Root, "tools", "FarmTogether2.ModKit.Tool", "FarmTogether2.ModKit.Tool.csproj");
    private static readonly IReadOnlyDictionary<string, Version> Assemblies =
        new Dictionary<string, Version>(StringComparer.Ordinal)
        {
            ["Assembly-CSharp"] = new(0, 0, 0, 0),
            ["Il2Cppmscorlib"] = new(4, 0, 0, 0),
            ["MilkstoneUnityExtensions"] = new(1, 0, 0, 0),
            ["UnityEngine.CoreModule"] = new(0, 0, 0, 0),
            ["UnityEngine.IMGUIModule"] = new(0, 0, 0, 0),
            ["UnityEngine.InputLegacyModule"] = new(0, 0, 0, 0),
            ["UnityEngine.TextRenderingModule"] = new(0, 0, 0, 0)
        };

    [Fact]
    public void ResolverInstallsExactPackageDetachedToolingAndGeneratedProps()
    {
        using Fixture fixture = new();

        fixture.Run().AssertSuccess();

        Assert.Equal(File.ReadAllBytes(fixture.AssetPath), File.ReadAllBytes(Path.Combine(fixture.Packages, AssetName)));
        Assert.True(File.Exists(Path.Combine(fixture.Tooling, "tools", "FarmTogether2.ModKit.Tool", "FarmTogether2.ModKit.Tool.csproj")));
        string props = File.ReadAllText(fixture.Props);
        Assert.Contains("<FarmTogether2GameApiRefVersion>1.0.0</FarmTogether2GameApiRefVersion>", props, StringComparison.Ordinal);
        Assert.Contains(System.Security.SecurityElement.Escape(fixture.Packages), props, StringComparison.Ordinal);
        string gitLog = File.ReadAllText(fixture.GitLog);
        Assert.Contains($"fetch --no-tags --depth=1 origin {Commit}", gitLog, StringComparison.Ordinal);
        Assert.Contains($"checkout --detach {Commit}", gitLog, StringComparison.Ordinal);
        Assert.DoesNotContain("clean -ffdx", gitLog, StringComparison.Ordinal);
        fixture.AssertNoResolverTemporaries();

        Dictionary<string, string> first = SnapshotTree(fixture.ModKitRoot);
        fixture.Run().AssertSuccess();
        Assert.Equal(first, SnapshotTree(fixture.ModKitRoot));
        fixture.AssertNoResolverTemporaries();
    }

    [Fact]
    public void InvalidLockAndInvalidTopologyFailBeforeNetworkOrCacheMutation()
    {
        using Fixture fixture = new();
        fixture.WritePriorCache();
        Dictionary<string, string> prior = SnapshotTree(fixture.ModKitRoot);
        File.WriteAllText(fixture.LockPath, "{\"schemaVersion\":1,\"schemaVersion\":1}", new UTF8Encoding(false));

        fixture.Run().AssertFailure("duplicate");

        Assert.Equal(prior, SnapshotTree(fixture.ModKitRoot));
        Assert.False(File.Exists(fixture.GhLog));
        Assert.False(File.Exists(fixture.GitLog));

        fixture.WriteLock();
        fixture.Run(destination: Path.Combine(fixture.RepositoryRoot, "other-packages")).AssertFailure("repository-owned");
        Assert.Equal(prior, SnapshotTree(fixture.ModKitRoot));
    }

    [Fact]
    public void DownloadOrCheckoutFailurePreservesPriorLiveCache()
    {
        using Fixture fixture = new();
        fixture.WritePriorCache();
        Dictionary<string, string> prior = SnapshotTree(fixture.ModKitRoot);
        File.AppendAllText(fixture.AssetPath, "changed after lock", new UTF8Encoding(false));

        fixture.Run().AssertFailure("SHA-256 differs");
        Assert.Equal(prior, SnapshotTree(fixture.ModKitRoot));
        fixture.AssertNoResolverTemporaries();

        fixture.RestoreAssetAndLock();
        fixture.Run(gitFailure: "fetch").AssertFailure("fetch");
        Assert.Equal(prior, SnapshotTree(fixture.ModKitRoot));
        fixture.AssertNoResolverTemporaries();

        fixture.Run(fetchedHead: "1111111111111111111111111111111111111111").AssertFailure("does not match");
        Assert.Equal(prior, SnapshotTree(fixture.ModKitRoot));
        fixture.AssertNoResolverTemporaries();
    }

    [Fact]
    public void FailureWithNoPriorCacheLeavesRepositoryTreeUnchanged()
    {
        using Fixture fixture = new();
        Dictionary<string, string> prior = SnapshotTree(fixture.RepositoryRoot);

        fixture.Run(gitFailure: "fetch").AssertFailure("fetch");

        Assert.Equal(prior, SnapshotTree(fixture.RepositoryRoot));
        Assert.False(Directory.Exists(fixture.ModKitRoot));
        fixture.AssertNoResolverTemporaries();
    }

    [Fact]
    public void UnknownParameterFailsBeforeNetworkAndRepositoryMutation()
    {
        using Fixture fixture = new();
        Dictionary<string, string> prior = SnapshotTree(fixture.RepositoryRoot);

        fixture.Run(extraArguments: ["-Unexpected", "value"]).AssertFailure();

        Assert.Equal(prior, SnapshotTree(fixture.RepositoryRoot));
        Assert.False(File.Exists(fixture.GhLog));
        Assert.False(File.Exists(fixture.GitLog));
    }

    [Fact]
    public void ReleaseIdentityChangeBeforePromotionPreservesPriorCache()
    {
        using Fixture fixture = new();
        fixture.WritePriorCache();
        Dictionary<string, string> prior = SnapshotTree(fixture.ModKitRoot);
        fixture.WriteSecondRelease(releaseId: 7002);

        fixture.Run(useSecondRelease: true).AssertFailure("changed during resolution");

        Assert.Equal(prior, SnapshotTree(fixture.ModKitRoot));
        fixture.AssertNoResolverTemporaries();
    }

    [Fact]
    public void MatchingPackageAndPropsWithDamagedToolingAreReplaced()
    {
        using Fixture fixture = new();
        fixture.WriteMatchingCacheWithDamagedTooling();

        fixture.Run(failLiveGitOnce: true).AssertSuccess();

        Assert.Equal(
            File.ReadAllBytes(fixture.AssetPath),
            File.ReadAllBytes(Path.Combine(fixture.Packages, AssetName)));
        Assert.False(File.Exists(Path.Combine(fixture.Tooling, "damaged.txt")));
        Assert.True(File.Exists(Path.Combine(
            fixture.Tooling,
            "tools",
            "FarmTogether2.ModKit.Tool",
            "FarmTogether2.ModKit.Tool.csproj")));
        fixture.AssertNoResolverTemporaries();
    }

    [Fact]
    public void CleanupFailureReportsErrorWithoutRollingBackCommittedCache()
    {
        using Fixture fixture = new();
        fixture.WritePriorCache();

        fixture.Run(failurePoint: "after-cleanup-props-backup")
            .AssertFailure("committed successfully, but cleanup failed");

        Assert.Equal(
            File.ReadAllBytes(fixture.AssetPath),
            File.ReadAllBytes(Path.Combine(fixture.Packages, AssetName)));
        Assert.False(File.Exists(Path.Combine(fixture.Packages, "prior.nupkg")));
        Assert.False(File.Exists(Path.Combine(fixture.Tooling, "prior.txt")));
        Assert.Contains(
            "<FarmTogether2GameApiRefVersion>1.0.0</FarmTogether2GameApiRefVersion>",
            File.ReadAllText(fixture.Props),
            StringComparison.Ordinal);
        fixture.AssertNoResolverTemporaries();
    }

    public static TheoryData<string> InitialDirectoryFailurePoints => new()
    {
        "before-create-download-root",
        "before-create-packages-preparing",
        "before-create-props-preparing"
    };

    [Theory]
    [MemberData(nameof(InitialDirectoryFailurePoints))]
    public void InitialDirectoryCreationFailureCleansOwnedPaths(string failurePoint)
    {
        using Fixture fixture = new();
        Dictionary<string, string> prior = SnapshotTree(fixture.RepositoryRoot);
        string[] downloadsBefore = SnapshotResolverDownloadDirectories();

        fixture.Run(failurePoint: failurePoint).AssertFailure(failurePoint);

        Assert.Equal(prior, SnapshotTree(fixture.RepositoryRoot));
        Assert.Equal(downloadsBefore, SnapshotResolverDownloadDirectories());
        fixture.AssertNoResolverTemporaries();
    }

    [Fact]
    public void SymlinkCacheRootIsRejectedBeforeNetworkWithoutTouchingTarget()
    {
        using Fixture fixture = new();
        string outside = Path.Combine(fixture.DirectoryPath, "outside-cache");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "sentinel.txt"), "outside", new UTF8Encoding(false));
        try
        {
            Directory.CreateSymbolicLink(fixture.ModKitRoot, outside);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new Xunit.Sdk.XunitException($"Symbolic-link test setup failed: {exception.Message}");
        }

        fixture.Run().AssertFailure("symlink");

        Assert.Equal("outside", File.ReadAllText(Path.Combine(outside, "sentinel.txt")));
        Assert.False(File.Exists(fixture.GhLog));
        Assert.False(File.Exists(fixture.GitLog));
    }

    public static TheoryData<string> TransactionFailurePoints => new()
    {
        "after-download-verify",
        "after-stage-package",
        "after-checkout-tooling",
        "after-stage-props",
        "after-backup-packages",
        "after-backup-tooling",
        "after-backup-props",
        "after-promote-packages",
        "after-promote-tooling",
        "after-promote-props"
    };

    [Theory]
    [MemberData(nameof(TransactionFailurePoints))]
    public void EveryPromotionFailureRollsBackExactPriorCache(string failurePoint)
    {
        using Fixture fixture = new();
        fixture.WritePriorCache();
        Dictionary<string, string> prior = SnapshotTree(fixture.ModKitRoot);

        fixture.Run(failurePoint: failurePoint).AssertFailure(failurePoint);

        Assert.Equal(prior, SnapshotTree(fixture.ModKitRoot));
        fixture.AssertNoResolverTemporaries();
    }

    [Fact]
    public void ExistingSymlinkInLiveCacheIsRejectedWithoutTouchingTarget()
    {
        using Fixture fixture = new();
        fixture.WritePriorCache();
        string outside = Path.Combine(fixture.DirectoryPath, "outside.txt");
        File.WriteAllText(outside, "outside", new UTF8Encoding(false));
        string link = Path.Combine(fixture.Packages, "linked.txt");
        try
        {
            File.CreateSymbolicLink(link, outside);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new Xunit.Sdk.XunitException($"Symbolic-link test setup failed: {exception.Message}");
        }

        fixture.Run().AssertFailure("symlink");

        Assert.Equal("outside", File.ReadAllText(outside));
        Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
    }

    private static Dictionary<string, string> SnapshotTree(string root)
    {
        Dictionary<string, string> snapshot = new(StringComparer.Ordinal);
        if (!Directory.Exists(root))
            return snapshot;
        foreach (string directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, directory).Replace('\\', '/');
            snapshot.Add("D:" + relative, "-");
        }
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            using FileStream stream = File.OpenRead(file);
            snapshot.Add("F:" + relative, Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
        }
        return snapshot;
    }

    private static string[] SnapshotResolverDownloadDirectories() => Directory
        .EnumerateDirectories(Path.GetTempPath(), "farmtogether2-modkit-resolve-*", SearchOption.TopDirectoryOnly)
        .Order(StringComparer.Ordinal)
        .ToArray();

    private sealed class Fixture : IDisposable
    {
        private readonly string _shimDirectory;
        private readonly string _releaseJson;
        private readonly string _referenceJson;
        private readonly string _secondReleaseJson;
        private readonly byte[] _assetBytes;

        public Fixture()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), $"modkit-resolver-{Guid.NewGuid():N}");
            RepositoryRoot = Path.Combine(DirectoryPath, "repository");
            ModKitRoot = Path.Combine(RepositoryRoot, ".modkit");
            Packages = Path.Combine(ModKitRoot, "packages");
            Tooling = Path.Combine(ModKitRoot, "tooling");
            Props = Path.Combine(ModKitRoot, "generated", "ModKit.lock.props");
            LockPath = Path.Combine(RepositoryRoot, "modkit.lock.json");
            AssetPath = Path.Combine(DirectoryPath, AssetName);
            GhLog = Path.Combine(DirectoryPath, "gh.log");
            GitLog = Path.Combine(DirectoryPath, "git.log");
            _shimDirectory = Path.Combine(DirectoryPath, "shim");
            _releaseJson = Path.Combine(DirectoryPath, "release.json");
            _referenceJson = Path.Combine(DirectoryPath, "reference.json");
            _secondReleaseJson = Path.Combine(DirectoryPath, "release-second.json");
            Directory.CreateDirectory(RepositoryRoot);
            Directory.CreateDirectory(_shimDirectory);
            CreateReferencePackage();
            _assetBytes = File.ReadAllBytes(AssetPath);
            WriteLock();
            WriteRelease();
            WriteReference(Commit);
            WriteShims();
        }

        public string DirectoryPath { get; }
        public string RepositoryRoot { get; }
        public string ModKitRoot { get; }
        public string Packages { get; }
        public string Tooling { get; }
        public string Props { get; }
        public string LockPath { get; }
        public string AssetPath { get; }
        public string GhLog { get; }
        public string GitLog { get; }

        public void WritePriorCache()
        {
            Directory.CreateDirectory(Packages);
            Directory.CreateDirectory(Tooling);
            Directory.CreateDirectory(Path.GetDirectoryName(Props)!);
            File.WriteAllText(Path.Combine(Packages, "prior.nupkg"), "prior package", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(Tooling, "prior.txt"), "prior tooling", new UTF8Encoding(false));
            File.WriteAllText(Props, "prior props", new UTF8Encoding(false));
        }

        public void WriteMatchingCacheWithDamagedTooling()
        {
            Directory.CreateDirectory(Packages);
            Directory.CreateDirectory(Tooling);
            Directory.CreateDirectory(Path.GetDirectoryName(Props)!);
            File.Copy(AssetPath, Path.Combine(Packages, AssetName));
            File.WriteAllText(Path.Combine(Tooling, "damaged.txt"), "damaged tooling", new UTF8Encoding(false));
            File.WriteAllText(Props, ExpectedPropsContent(), new UTF8Encoding(false));
        }

        public void RestoreAssetAndLock()
        {
            File.WriteAllBytes(AssetPath, _assetBytes);
            WriteLock();
            WriteRelease();
        }

        public void WriteSecondRelease(long releaseId)
        {
            string hash = Sha256(AssetPath);
            var release = new
            {
                id = releaseId,
                tag_name = "v1.0.0",
                draft = false,
                prerelease = false,
                assets = new[]
                {
                    new { id = 8001, name = AssetName, state = "uploaded", size = new FileInfo(AssetPath).Length, digest = "sha256:" + hash }
                }
            };
            File.WriteAllText(_secondReleaseJson, JsonSerializer.Serialize(release), new UTF8Encoding(false));
        }

        public void WriteLock()
        {
            string hash = Sha256(AssetPath);
            var value = new
            {
                schemaVersion = 1,
                repository = "abmcar/FarmTogether2-ModKit",
                workflowCommit = Commit,
                packageId = "FarmTogether2.GameApi.Ref",
                packageVersion = "1.0.0",
                releaseTag = "v1.0.0",
                assetName = AssetName,
                sha256 = hash
            };
            File.WriteAllText(
                LockPath,
                JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) + "\n",
                new UTF8Encoding(false));
        }

        public ProcessResult Run(
            string? failurePoint = null,
            string? gitFailure = null,
            string fetchedHead = Commit,
            string? destination = null,
            bool useSecondRelease = false,
            bool failLiveGitOnce = false,
            IEnumerable<string>? extraArguments = null)
        {
            ProcessStartInfo startInfo = new("pwsh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (string argument in new[]
            {
                "-NoLogo", "-NoProfile", "-File", Script,
                "-LockFile", LockPath,
                "-Destination", destination ?? Packages,
                "-PropsOutput", Props
            })
            {
                startInfo.ArgumentList.Add(argument);
            }
            if (extraArguments is not null)
            {
                foreach (string argument in extraArguments)
                    startInfo.ArgumentList.Add(argument);
            }
            startInfo.Environment["PATH"] = _shimDirectory + Path.PathSeparator +
                (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
            startInfo.Environment["FAKE_GH_RELEASE_JSON"] = _releaseJson;
            startInfo.Environment["FAKE_GH_REFERENCE_JSON"] = _referenceJson;
            startInfo.Environment["FAKE_GH_ASSET"] = AssetPath;
            startInfo.Environment["FAKE_GH_LOG"] = GhLog;
            startInfo.Environment["FAKE_GIT_LOG"] = GitLog;
            startInfo.Environment["FAKE_GIT_SOURCE"] = Root;
            startInfo.Environment["FAKE_GIT_HEAD"] = fetchedHead;
            if (useSecondRelease)
                startInfo.Environment["FAKE_GH_SECOND_RELEASE_JSON"] = _secondReleaseJson;
            if (failurePoint is not null)
                startInfo.Environment["FARMT2_MODKIT_RESOLVE_FAIL_AT"] = failurePoint;
            if (gitFailure is not null)
                startInfo.Environment["FAKE_GIT_FAIL_AT"] = gitFailure;
            if (failLiveGitOnce)
            {
                startInfo.Environment["FAKE_GIT_FAIL_LIVE_ONCE"] = "1";
                startInfo.Environment["FAKE_GIT_LIVE_PATH"] = Tooling;
                startInfo.Environment["FAKE_GIT_LIVE_FAILURE_MARKER"] = Path.Combine(
                    DirectoryPath,
                    "live-git-failure.marker");
            }
            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start pwsh.");
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return new ProcessResult(process.ExitCode, stdout, stderr);
        }

        public void AssertNoResolverTemporaries()
        {
            Assert.Empty(Directory.EnumerateFileSystemEntries(RepositoryRoot, ".modkit-resolve-*.preparing", SearchOption.TopDirectoryOnly));
            if (!Directory.Exists(ModKitRoot))
                return;
            Assert.Empty(Directory.EnumerateFileSystemEntries(ModKitRoot, ".*-*.preparing", SearchOption.TopDirectoryOnly));
            Assert.Empty(Directory.EnumerateFileSystemEntries(ModKitRoot, ".*-*.backup", SearchOption.TopDirectoryOnly));
            string generated = Path.Combine(ModKitRoot, "generated");
            if (Directory.Exists(generated))
            {
                Assert.Empty(Directory.EnumerateFileSystemEntries(generated, ".*.preparing", SearchOption.TopDirectoryOnly));
                Assert.Empty(Directory.EnumerateFileSystemEntries(generated, ".*.backup", SearchOption.TopDirectoryOnly));
            }
        }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
                Directory.Delete(DirectoryPath, recursive: true);
        }

        private void WriteRelease()
        {
            string hash = Sha256(AssetPath);
            var release = new
            {
                id = 7001,
                tag_name = "v1.0.0",
                draft = false,
                prerelease = false,
                assets = new[]
                {
                    new { id = 8001, name = AssetName, state = "uploaded", size = new FileInfo(AssetPath).Length, digest = "sha256:" + hash }
                }
            };
            File.WriteAllText(_releaseJson, JsonSerializer.Serialize(release), new UTF8Encoding(false));
        }

        private void WriteReference(string commit)
        {
            var reference = new { @object = new { type = "commit", sha = commit } };
            File.WriteAllText(_referenceJson, JsonSerializer.Serialize(reference), new UTF8Encoding(false));
        }

        private string ExpectedPropsContent()
        {
            string escapedPackages = System.Security.SecurityElement.Escape(Packages)
                ?? throw new InvalidOperationException("Could not XML-escape the fixture package path.");
            return $"""
                <Project>
                  <PropertyGroup>
                    <FarmTogether2GameApiRefVersion>1.0.0</FarmTogether2GameApiRefVersion>
                    <FarmTogether2ModKitPackageSource>{escapedPackages}</FarmTogether2ModKitPackageSource>
                  </PropertyGroup>
                </Project>

                """.Replace("\r\n", "\n", StringComparison.Ordinal);
        }

        private void CreateReferencePackage()
        {
            string assemblyRoot = Path.Combine(DirectoryPath, "assemblies");
            Directory.CreateDirectory(assemblyRoot);
            List<string> arguments =
            [
                "run", "--project", ToolProject, "-c", TestBuildConfiguration.Current, "--no-build", "--",
                "ref-package", "write",
                "--output", AssetPath,
                "--package-id", "FarmTogether2.GameApi.Ref",
                "--version", "1.0.0"
            ];
            foreach ((string name, Version version) in Assemblies)
            {
                string path = Path.Combine(assemblyRoot, $"{name}.dll");
                using AssemblyDefinition assembly = AssemblyDefinition.CreateAssembly(
                    new AssemblyNameDefinition(name, version), name, ModuleKind.Dll);
                assembly.Write(path, new WriterParameters { DeterministicMvid = true });
                arguments.Add("--assembly");
                arguments.Add(path);
            }
            RunProcess("dotnet", arguments).AssertSuccess();
        }

        private void WriteShims()
        {
            string ghScript = Path.Combine(_shimDirectory, "gh-shim.ps1");
            File.WriteAllText(ghScript, """
                $ErrorActionPreference = 'Stop'
                Add-Content -LiteralPath $env:FAKE_GH_LOG -Value ($args -join ' ')
                if ($args[0] -ceq 'api') {
                    $endpoint = $args[-1]
                    if ($endpoint -like '*/releases/tags/*') {
                        $releaseCalls = @(Select-String -LiteralPath $env:FAKE_GH_LOG -SimpleMatch 'releases/tags/').Count
                        if ($env:FAKE_GH_SECOND_RELEASE_JSON -and $releaseCalls -ge 2) { Get-Content -Raw -LiteralPath $env:FAKE_GH_SECOND_RELEASE_JSON; exit 0 }
                        Get-Content -Raw -LiteralPath $env:FAKE_GH_RELEASE_JSON; exit 0
                    }
                    if ($endpoint -like '*/git/ref/tags/*') { Get-Content -Raw -LiteralPath $env:FAKE_GH_REFERENCE_JSON; exit 0 }
                    exit 64
                }
                if ($args[0] -ceq 'release' -and $args[1] -ceq 'download') {
                    $directoryIndex = [Array]::IndexOf($args, '--dir')
                    if ($directoryIndex -lt 0) { exit 65 }
                    Copy-Item -LiteralPath $env:FAKE_GH_ASSET -Destination (Join-Path $args[$directoryIndex + 1] (Split-Path -Leaf $env:FAKE_GH_ASSET))
                    exit 0
                }
                exit 66
                """.Replace("\r\n", "\n", StringComparison.Ordinal), new UTF8Encoding(false));

            string gitScript = Path.Combine(_shimDirectory, "git-shim.ps1");
            File.WriteAllText(gitScript, """
                $ErrorActionPreference = 'Stop'
                Add-Content -LiteralPath $env:FAKE_GIT_LOG -Value ($args -join ' ')
                if ($env:FAKE_GIT_FAIL_AT -and ($args -contains $env:FAKE_GIT_FAIL_AT)) { exit 71 }
                if ($env:FAKE_GIT_FAIL_LIVE_ONCE -and $args -contains '-C') {
                    $root = $args[[Array]::IndexOf($args, '-C') + 1]
                    if ($root -ceq $env:FAKE_GIT_LIVE_PATH -and -not (Test-Path -LiteralPath $env:FAKE_GIT_LIVE_FAILURE_MARKER)) {
                        [IO.File]::WriteAllText($env:FAKE_GIT_LIVE_FAILURE_MARKER, 'failed')
                        exit 73
                    }
                }
                if ($args -contains 'init') {
                    $destination = $args[-1]
                    $toolDestination = Join-Path $destination 'tools/FarmTogether2.ModKit.Tool'
                    New-Item -ItemType Directory -Force -Path $toolDestination | Out-Null
                    Get-ChildItem -LiteralPath (Join-Path $env:FAKE_GIT_SOURCE 'tools/FarmTogether2.ModKit.Tool') -File | Copy-Item -Destination $toolDestination
                    foreach ($name in 'Directory.Build.props','Directory.Packages.props','NuGet.config','global.json') {
                        Copy-Item -LiteralPath (Join-Path $env:FAKE_GIT_SOURCE $name) -Destination (Join-Path $destination $name)
                    }
                    exit 0
                }
                if ($args -contains 'rev-parse' -and $args -contains '--abbrev-ref') { 'HEAD'; exit 0 }
                if ($args -contains 'rev-parse') { $env:FAKE_GIT_HEAD; exit 0 }
                if ($args -contains 'get-url') { 'https://github.com/abmcar/FarmTogether2-ModKit.git'; exit 0 }
                if ($args -contains 'status') { exit 0 }
                if ($args -contains 'clean') {
                    $root = $args[[Array]::IndexOf($args, '-C') + 1]
                    Get-ChildItem -LiteralPath $root -Directory -Recurse -Force | Where-Object Name -in @('bin','obj') | Sort-Object FullName -Descending | Remove-Item -Recurse -Force
                    exit 0
                }
                if ($args -contains 'diff' -or $args -contains 'remote' -or $args -contains 'fetch' -or $args -contains 'checkout') { exit 0 }
                exit 72
                """.Replace("\r\n", "\n", StringComparison.Ordinal), new UTF8Encoding(false));

            WriteWrapper("gh", ghScript);
            WriteWrapper("git", gitScript);
        }

        private void WriteWrapper(string name, string scriptPath)
        {
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(
                    Path.Combine(_shimDirectory, name + ".cmd"),
                    $"@pwsh -NoLogo -NoProfile -File \"{scriptPath}\" %*\r\n",
                    new UTF8Encoding(false));
            }
            else
            {
                string wrapper = Path.Combine(_shimDirectory, name);
                File.WriteAllText(
                    wrapper,
                    $"#!/bin/sh\nexec pwsh -NoLogo -NoProfile -File '{scriptPath.Replace("'", "'\\''", StringComparison.Ordinal)}' \"$@\"\n",
                    new UTF8Encoding(false));
                File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        private static ProcessResult RunProcess(string fileName, IEnumerable<string> arguments)
        {
            ProcessStartInfo startInfo = new(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);
            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {fileName}.");
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return new ProcessResult(process.ExitCode, stdout, stderr);
        }

        private static string Sha256(string path)
        {
            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr)
    {
        public void AssertSuccess() => Assert.True(ExitCode == 0, $"stdout:\n{Stdout}\nstderr:\n{Stderr}");

        public void AssertFailure(string? expected = null)
        {
            Assert.NotEqual(0, ExitCode);
            if (expected is not null)
                Assert.Contains(expected, $"{Stdout}\n{Stderr}", StringComparison.OrdinalIgnoreCase);
        }
    }
}
