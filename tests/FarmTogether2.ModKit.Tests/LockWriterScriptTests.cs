using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mono.Cecil;
using Xunit;

namespace FarmTogether2.ModKit.Tests;

[Trait("Category", "LongRunning")]
public sealed class LockWriterScriptTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string AssetName = "FarmTogether2.GameApi.Ref.1.0.0.nupkg";
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private static readonly string Script = Path.Combine(Root, "scripts", "Write-ModKitLock.ps1");
    private static readonly string ToolAssembly = Path.Combine(
        Root,
        "tools",
        "FarmTogether2.ModKit.Tool",
        "bin",
        TestBuildConfiguration.Current,
        "net8.0",
        "FarmTogether2.ModKit.Tool.dll");
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
    public void WriterBindsManifestTagReleaseCommitAssetAndHashBeforeLockPromotion()
    {
        using Fixture fixture = new();

        fixture.Run().AssertSuccess();

        Assert.Equal(fixture.ManifestJson, File.ReadAllText(fixture.LockPath));
        string log = File.ReadAllText(fixture.GhLog);
        Assert.Contains("releases/tags/v1.0.0", log, StringComparison.Ordinal);
        Assert.Contains("git/ref/tags/v1.0.0", log, StringComparison.Ordinal);
        Assert.Contains("release download", log, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidManifestFailsBeforeGhAndPreservesPriorLock()
    {
        using Fixture fixture = new();
        File.WriteAllText(fixture.LockPath, "sentinel", new UTF8Encoding(false));
        fixture.WriteManifest(fixture.ManifestJson.Replace("{", "{\"schemaVersion\":1,", StringComparison.Ordinal));

        fixture.Run().AssertFailure("duplicate");

        Assert.Equal("sentinel", File.ReadAllText(fixture.LockPath));
        Assert.False(File.Exists(fixture.GhLog));
    }

    [Fact]
    public void TagCommitMismatchFailsBeforeDownloadAndPreservesPriorLock()
    {
        using Fixture fixture = new();
        File.WriteAllText(fixture.LockPath, "sentinel", new UTF8Encoding(false));
        fixture.WriteReference("1111111111111111111111111111111111111111");

        fixture.Run().AssertFailure("commit differs");

        Assert.Equal("sentinel", File.ReadAllText(fixture.LockPath));
        Assert.DoesNotContain("release download", File.ReadAllText(fixture.GhLog), StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadHashMismatchPreservesPriorLock()
    {
        using Fixture fixture = new();
        File.WriteAllText(fixture.LockPath, "sentinel", new UTF8Encoding(false));
        File.AppendAllText(fixture.AssetPath, "changed after manifest", new UTF8Encoding(false));

        fixture.Run().AssertFailure("SHA-256 differs");

        Assert.Equal("sentinel", File.ReadAllText(fixture.LockPath));
        Assert.Contains("release download", File.ReadAllText(fixture.GhLog), StringComparison.Ordinal);
    }

    [Fact]
    public void HashBoundButInvalidPackageIsRejectedBeforeLockPromotion()
    {
        using Fixture fixture = new();
        File.WriteAllText(fixture.LockPath, "sentinel", new UTF8Encoding(false));
        File.WriteAllText(fixture.AssetPath, "not a NuGet package", new UTF8Encoding(false));
        fixture.RefreshManifestAndRelease();

        fixture.Run().AssertFailure();

        Assert.Equal("sentinel", File.ReadAllText(fixture.LockPath));
    }

    [Fact]
    public void ReleaseIdentityChangeDuringDownloadPreservesPriorLock()
    {
        using Fixture fixture = new();
        File.WriteAllText(fixture.LockPath, "sentinel", new UTF8Encoding(false));
        fixture.WriteSecondRelease(releaseId: 7002);

        fixture.Run(useSecondRelease: true).AssertFailure("changed during lock creation");

        Assert.Equal("sentinel", File.ReadAllText(fixture.LockPath));
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("wrong-type")]
    [InlineData("wrong-case")]
    public void OtherClosedSchemaViolationsFailBeforeGh(string kind)
    {
        using Fixture fixture = new();
        File.WriteAllText(fixture.LockPath, "sentinel", new UTF8Encoding(false));
        string invalid = kind switch
        {
            "extra" => fixture.ManifestJson.Replace("}", ",\"extra\":true}", StringComparison.Ordinal),
            "wrong-type" => fixture.ManifestJson.Replace("\"repository\": \"abmcar/FarmTogether2-ModKit\"", "\"repository\": 1", StringComparison.Ordinal),
            "wrong-case" => fixture.ManifestJson.Replace("\"repository\"", "\"Repository\"", StringComparison.Ordinal),
            _ => throw new InvalidOperationException()
        };
        fixture.WriteManifest(invalid);

        fixture.Run().AssertFailure();

        Assert.Equal("sentinel", File.ReadAllText(fixture.LockPath));
        Assert.False(File.Exists(fixture.GhLog));
    }

    [Fact]
    public void UnknownParameterDoesNotTouchNetworkOrPriorLock()
    {
        using Fixture fixture = new();
        File.WriteAllText(fixture.LockPath, "sentinel", new UTF8Encoding(false));

        fixture.Run(extraArguments: ["-Unexpected", "value"]).AssertFailure();

        Assert.Equal("sentinel", File.ReadAllText(fixture.LockPath));
        Assert.False(File.Exists(fixture.GhLog));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _shimDirectory;
        private readonly string _releaseJson;
        private readonly string _referenceJson;
        private readonly string _secondReleaseJson;

        public Fixture()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), $"modkit-lock-writer-{Guid.NewGuid():N}");
            RepositoryRoot = Path.Combine(DirectoryPath, "repository");
            _shimDirectory = Path.Combine(DirectoryPath, "shim");
            Directory.CreateDirectory(RepositoryRoot);
            Directory.CreateDirectory(_shimDirectory);
            ManifestPath = Path.Combine(DirectoryPath, "modkit-release.json");
            LockPath = Path.Combine(RepositoryRoot, "modkit.lock.json");
            AssetPath = Path.Combine(DirectoryPath, AssetName);
            GhLog = Path.Combine(DirectoryPath, "gh.log");
            _releaseJson = Path.Combine(DirectoryPath, "release.json");
            _referenceJson = Path.Combine(DirectoryPath, "reference.json");
            _secondReleaseJson = Path.Combine(DirectoryPath, "release-second.json");
            CreateReferencePackage();
            RefreshManifestAndRelease();
            WriteReference(Commit);
            WriteGhShim();
        }

        public string DirectoryPath { get; }
        public string RepositoryRoot { get; }
        public string ManifestPath { get; }
        public string LockPath { get; }
        public string AssetPath { get; }
        public string GhLog { get; }
        public string ManifestJson => File.ReadAllText(ManifestPath);

        public void RefreshManifestAndRelease()
        {
            string hash = Sha256(AssetPath);
            var manifest = new
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
            string json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + "\n";
            WriteManifest(json);
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

        public void WriteManifest(string json) => File.WriteAllText(ManifestPath, json, new UTF8Encoding(false));

        public void WriteReference(string commit)
        {
            var reference = new { @object = new { type = "commit", sha = commit } };
            File.WriteAllText(_referenceJson, JsonSerializer.Serialize(reference), new UTF8Encoding(false));
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

        public ProcessResult Run(bool useSecondRelease = false, IEnumerable<string>? extraArguments = null)
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
                "-RepositoryRoot", RepositoryRoot,
                "-ModKitRepository", "abmcar/FarmTogether2-ModKit",
                "-Tag", "v1.0.0",
                "-ExpectedReleaseManifest", ManifestPath
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
            if (useSecondRelease)
                startInfo.Environment["FAKE_GH_SECOND_RELEASE_JSON"] = _secondReleaseJson;
            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start pwsh.");
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return new ProcessResult(process.ExitCode, stdout, stderr);
        }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
                Directory.Delete(DirectoryPath, recursive: true);
        }

        private void CreateReferencePackage()
        {
            string assemblyRoot = Path.Combine(DirectoryPath, "assemblies");
            Directory.CreateDirectory(assemblyRoot);
            List<string> arguments =
            [
                ToolAssembly,
                "ref-package", "write",
                "--output", AssetPath,
                "--package-id", "FarmTogether2.GameApi.Ref",
                "--version", "1.0.0"
            ];
            foreach ((string name, Version version) in Assemblies)
            {
                string path = Path.Combine(assemblyRoot, $"{name}.dll");
                AssemblyNameDefinition identity = new(name, version)
                {
                    HashAlgorithm = Mono.Cecil.AssemblyHashAlgorithm.SHA1
                };
                using AssemblyDefinition assembly = AssemblyDefinition.CreateAssembly(
                    identity, name, ModuleKind.Dll);
                assembly.Write(path, new WriterParameters { DeterministicMvid = true });
                arguments.Add("--assembly");
                arguments.Add(path);
            }
            ProcessResult result = RunDotNet(arguments);
            result.AssertSuccess();
        }

        private void WriteGhShim()
        {
            string scriptPath = Path.Combine(_shimDirectory, "gh-shim.ps1");
            string body = """
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
                """;
            File.WriteAllText(scriptPath, body.Replace("\r\n", "\n", StringComparison.Ordinal), new UTF8Encoding(false));
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(
                    Path.Combine(_shimDirectory, "gh.cmd"),
                    $"@pwsh -NoLogo -NoProfile -File \"{scriptPath}\" %*\r\n",
                    new UTF8Encoding(false));
            }
            else
            {
                string wrapper = Path.Combine(_shimDirectory, "gh");
                File.WriteAllText(
                    wrapper,
                    $"#!/bin/sh\nexec pwsh -NoLogo -NoProfile -File '{scriptPath.Replace("'", "'\\''", StringComparison.Ordinal)}' \"$@\"\n",
                    new UTF8Encoding(false));
                File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        private static ProcessResult RunDotNet(IEnumerable<string> arguments)
        {
            ProcessStartInfo startInfo = new("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);
            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start dotnet.");
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
