using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace FarmTogether2.ModKit.Tests;

[Trait("Category", "LongRunning")]
[Trait("ReleaseShard", "6")]
public sealed class PackModScriptTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private static readonly string Script = Path.Combine(Root, "scripts", "Pack-Mod.ps1");
    private static readonly string PlayerVerifier = Path.Combine(Root, "scripts", "Test-PlayerPackage.ps1");

    [Fact]
    public void ScriptPackagesBuiltProjectAndReplacesOnlyVerifiedPriorOutput()
    {
        using Fixture fixture = new();
        fixture.Build().AssertSuccess();
        fixture.Pack().AssertSuccess();
        fixture.Verify().AssertSuccess();
        Assert.DoesNotContain(Directory.EnumerateDirectories(fixture.ToolTemporaryDirectory),
            path => Path.GetFileName(path).StartsWith("farmtogether2-modkit-tool-", StringComparison.Ordinal));
        Dictionary<string, string> first = fixture.OutputHashes();

        fixture.Pack().AssertSuccess();
        Assert.Equal(first, fixture.OutputHashes());
    }

    [Fact]
    public void ScriptRejectsUnownedOutputWithoutMutation()
    {
        using Fixture fixture = new();
        fixture.Build().AssertSuccess();
        Directory.CreateDirectory(fixture.Output);
        string sentinel = Path.Combine(fixture.Output, "sentinel.txt");
        File.WriteAllText(sentinel, "sentinel", new UTF8Encoding(false));

        fixture.Pack().AssertFailure("exactly");
        Assert.Equal("sentinel", File.ReadAllText(sentinel));
        Assert.Single(Directory.EnumerateFileSystemEntries(fixture.Output));
    }

    [Fact]
    public void MissingPdbFailsBeforeOutputMutation()
    {
        using Fixture fixture = new();
        fixture.Build().AssertSuccess();
        string pdb = Path.Combine(fixture.Repository, "src", "Fixture", "bin", "Release", "net10.0", "FarmTogether2.ScriptFixture.pdb");
        File.Delete(pdb);

        fixture.Pack().AssertFailure("PDB");
        Assert.False(Directory.Exists(fixture.Output));
        Assert.DoesNotContain(Directory.EnumerateDirectories(fixture.ToolTemporaryDirectory),
            path => Path.GetFileName(path).StartsWith("farmtogether2-modkit-tool-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConcurrentPackProcessesSerializeForTheSameCanonicalOutput()
    {
        using Fixture fixture = new();
        fixture.Build().AssertSuccess();

        ProcessResult[] results = await Task.WhenAll(
            Task.Run(() => fixture.Pack()),
            Task.Run(() => fixture.Pack()));

        Assert.All(results, result => result.AssertSuccess());
        fixture.Verify().AssertSuccess();
        fixture.AssertNoTransactionPaths();
    }

    [Fact]
    public void NextPackRecoversAnAbandonedTransactionWithoutLosingTheLivePackage()
    {
        using Fixture fixture = new();
        fixture.Build().AssertSuccess();
        fixture.Pack().AssertSuccess();
        Dictionary<string, string> original = fixture.OutputHashes();

        fixture.Pack(hardCrashAt: "after-backup").AssertExitCode(86);
        fixture.Pack().AssertSuccess();

        Assert.Equal(original, fixture.OutputHashes());
        fixture.Verify().AssertSuccess();
        fixture.AssertNoTransactionPaths();
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), $"modkit-pack-script-{Guid.NewGuid():N}");
            Repository = Path.Combine(DirectoryPath, "repository");
            Output = Path.Combine(Repository, "artifacts");
            ToolTemporaryDirectory = Path.Combine(DirectoryPath, "tool-temporary");
            string projectDirectory = Path.Combine(Repository, "src", "Fixture");
            Directory.CreateDirectory(projectDirectory);
            Directory.CreateDirectory(Path.Combine(Repository, "packaging"));
            Directory.CreateDirectory(ToolTemporaryDirectory);
            File.WriteAllText(Path.Combine(projectDirectory, "Fixture.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <AssemblyName>FarmTogether2.ScriptFixture</AssemblyName>
                    <Version>1.0.0</Version>
                    <DebugType>portable</DebugType>
                  </PropertyGroup>
                </Project>
                """.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(projectDirectory, "Class1.cs"), "public sealed class Class1 { }\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(Repository, "packaging", "README_安装说明.txt"), "install\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(Repository, "LICENSE"), "MIT\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(Repository, "mod.json"), """
                {
                  "schemaVersion": 1,
                  "id": "com.abmcar.farmtogether2.scriptfixture",
                  "displayName": "FarmTogether2.ScriptFixture",
                  "assemblyName": "FarmTogether2.ScriptFixture",
                  "version": "1.0.0",
                  "project": "src/Fixture/Fixture.csproj",
                  "testProjects": [],
                  "guardScripts": [],
                  "installReadme": "packaging/README_安装说明.txt",
                  "supportedSteamBuild": "24069957"
                }
                """.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n", new UTF8Encoding(false));
        }

        public string DirectoryPath { get; }
        public string Repository { get; }
        public string Output { get; }
        public string ToolTemporaryDirectory { get; }

        public ProcessResult Build() => Run("dotnet", ["build", Path.Combine(Repository, "src", "Fixture", "Fixture.csproj"), "-c", "Release"]);

        public ProcessResult Pack(string? hardCrashAt = null) => Run(
            "pwsh",
            ["-NoLogo", "-NoProfile", "-File", Script, "-RepositoryRoot", Repository, "-Configuration", "Release", "-OutputDirectory", Output],
            hardCrashAt);

        public ProcessResult Verify() => Run("pwsh", ["-NoLogo", "-NoProfile", "-File", PlayerVerifier, "-ModConfig", Path.Combine(Repository, "mod.json"), "-Artifacts", Output]);

        public Dictionary<string, string> OutputHashes() => Directory.EnumerateFiles(Output)
            .ToDictionary(path => Path.GetFileName(path)!, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(), StringComparer.Ordinal);

        public void AssertNoTransactionPaths() => Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(Repository),
            path => Path.GetFileName(path).StartsWith(".artifacts.modkit-pack", StringComparison.Ordinal));

        private ProcessResult Run(string fileName, IEnumerable<string> args, string? hardCrashAt = null)
        {
            ProcessStartInfo startInfo = new(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = Repository
            };
            foreach (string argument in args)
                startInfo.ArgumentList.Add(argument);
            startInfo.Environment["PATH"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet") + Path.PathSeparator +
                (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
            startInfo.Environment["DOTNET_ROOT"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet");
            startInfo.Environment["TMPDIR"] = ToolTemporaryDirectory;
            startInfo.Environment["TMP"] = ToolTemporaryDirectory;
            startInfo.Environment["TEMP"] = ToolTemporaryDirectory;
            startInfo.Environment["FARMT2_PACK_HARD_CRASH_AT"] = hardCrashAt;
            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {fileName}.");
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
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr)
    {
        public void AssertSuccess() => Assert.True(ExitCode == 0, $"stdout:\n{Stdout}\nstderr:\n{Stderr}");
        public void AssertFailure(string expected)
        {
            Assert.NotEqual(0, ExitCode);
            Assert.Contains(expected, Stdout + "\n" + Stderr, StringComparison.OrdinalIgnoreCase);
        }

        public void AssertExitCode(int expected) => Assert.True(
            ExitCode == expected,
            $"Expected exit code {expected}, found {ExitCode}.\nstdout:\n{Stdout}\nstderr:\n{Stderr}");
    }
}
