using System.Diagnostics;
using System.Text;
using Xunit;

namespace FarmTogether2.ModKit.Tests;

public sealed class LockResolverTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private static readonly string ToolProject = Path.Combine(Root, "tools", "FarmTogether2.ModKit.Tool", "FarmTogether2.ModKit.Tool.csproj");
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void WriterProducesDeterministicClosedLockAndVerifierAcceptsIt()
    {
        using Fixture fixture = new();
        ProcessResult first = fixture.WriteLock();
        first.AssertSuccess();
        byte[] bytes = File.ReadAllBytes(fixture.LockPath);

        fixture.WriteLock().AssertSuccess();

        Assert.Equal(bytes, File.ReadAllBytes(fixture.LockPath));
        Assert.Equal(
            """
            {
              "schemaVersion": 1,
              "repository": "abmcar/FarmTogether2-ModKit",
              "workflowCommit": "0123456789abcdef0123456789abcdef01234567",
              "packageId": "FarmTogether2.GameApi.Ref",
              "packageVersion": "1.0.0",
              "releaseTag": "v1.0.0",
              "assetName": "FarmTogether2.GameApi.Ref.1.0.0.nupkg",
              "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
            }

            """.Replace("\r\n", "\n", StringComparison.Ordinal),
            File.ReadAllText(fixture.LockPath).Replace("\r\n", "\n", StringComparison.Ordinal));
        fixture.VerifyLock().AssertSuccess();
    }

    public static TheoryData<string, string> InvalidLocks => new()
    {
        { "duplicate", CanonicalJson.Replace("{", "{\"schemaVersion\":1,", StringComparison.Ordinal) },
        { "extra", CanonicalJson.Replace("}", ",\"extra\":true}", StringComparison.Ordinal) },
        { "wrong-schema-type", CanonicalJson.Replace("\"schemaVersion\":1", "\"schemaVersion\":\"1\"", StringComparison.Ordinal) },
        { "noncanonical-schema-number", CanonicalJson.Replace("\"schemaVersion\":1", "\"schemaVersion\":1.0", StringComparison.Ordinal) },
        { "null", CanonicalJson.Replace("\"repository\":\"abmcar/FarmTogether2-ModKit\"", "\"repository\":null", StringComparison.Ordinal) },
        { "wrong-case", CanonicalJson.Replace("\"repository\"", "\"Repository\"", StringComparison.Ordinal) },
        { "nested", CanonicalJson.Replace("\"packageId\":\"FarmTogether2.GameApi.Ref\"", "\"packageId\":{\"id\":\"FarmTogether2.GameApi.Ref\"}", StringComparison.Ordinal) },
        { "uppercase-commit", CanonicalJson.Replace(Commit, Commit.ToUpperInvariant(), StringComparison.Ordinal) },
        { "short-commit", CanonicalJson.Replace(Commit, Commit[..^1], StringComparison.Ordinal) },
        { "prerelease", CanonicalJson.Replace("1.0.0", "1.0.0-beta", StringComparison.Ordinal) },
        { "tag-mismatch", CanonicalJson.Replace("\"releaseTag\":\"v1.0.0\"", "\"releaseTag\":\"v2.0.0\"", StringComparison.Ordinal) },
        { "asset-mismatch", CanonicalJson.Replace("FarmTogether2.GameApi.Ref.1.0.0.nupkg", "wrong.nupkg", StringComparison.Ordinal) },
        { "uppercase-hash", CanonicalJson.Replace(Hash, Hash.ToUpperInvariant(), StringComparison.Ordinal) },
        { "short-hash", CanonicalJson.Replace(Hash, Hash[..^1], StringComparison.Ordinal) },
        { "array-root", $"[{CanonicalJson}]" }
    };

    [Theory]
    [MemberData(nameof(InvalidLocks))]
    public void VerifierRejectsMalformedOrNoncanonicalLocks(string _, string json)
    {
        using Fixture fixture = new();
        File.WriteAllText(fixture.LockPath, json, new UTF8Encoding(false));

        fixture.VerifyLock().AssertFailure();
    }

    [Fact]
    public void InvalidWriteAndUnknownOptionsDoNotMutateExistingLock()
    {
        using Fixture fixture = new();
        File.WriteAllText(fixture.LockPath, "sentinel", new UTF8Encoding(false));

        fixture.WriteLock(sha256: Hash.ToUpperInvariant()).AssertFailure();
        Assert.Equal("sentinel", File.ReadAllText(fixture.LockPath));

        fixture.WriteLock(extraArguments: ["--unknown", "value"]).AssertFailure("Unknown command options");
        Assert.Equal("sentinel", File.ReadAllText(fixture.LockPath));
    }

    [Fact]
    public void UnknownCommandsAndRepeatedSingletonsFailWithoutOutput()
    {
        using Fixture fixture = new();
        fixture.Run(["unknown", "write", "--output", fixture.LockPath]).AssertFailure("Unknown command");
        Assert.False(File.Exists(fixture.LockPath));

        fixture.WriteLock(extraArguments: ["--repository", "owner/other"]).AssertFailure("exactly once");
        Assert.False(File.Exists(fixture.LockPath));
    }

    [Fact]
    public void WriterAndVerifierRejectSymlinkAncestorsWithoutTouchingTargets()
    {
        using Fixture fixture = new();
        string outside = Path.Combine(fixture.DirectoryPath, "outside");
        string link = Path.Combine(fixture.DirectoryPath, "linked");
        Directory.CreateDirectory(outside);
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new Xunit.Sdk.XunitException($"Symbolic-link test setup failed: {exception.Message}");
        }
        string linkedLock = Path.Combine(link, "modkit.lock.json");
        fixture.WriteLock(output: linkedLock).AssertFailure("symlink");
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));

        File.WriteAllText(Path.Combine(outside, "modkit.lock.json"), CanonicalJson, new UTF8Encoding(false));
        fixture.Run(["lock", "verify", "--file", linkedLock]).AssertFailure("symlink");
    }

    private const string CanonicalJson =
        "{\"schemaVersion\":1," +
        "\"repository\":\"abmcar/FarmTogether2-ModKit\"," +
        "\"workflowCommit\":\"" + Commit + "\"," +
        "\"packageId\":\"FarmTogether2.GameApi.Ref\"," +
        "\"packageVersion\":\"1.0.0\"," +
        "\"releaseTag\":\"v1.0.0\"," +
        "\"assetName\":\"FarmTogether2.GameApi.Ref.1.0.0.nupkg\"," +
        "\"sha256\":\"" + Hash + "\"}";

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), $"modkit-lock-{Guid.NewGuid():N}");
            Directory.CreateDirectory(DirectoryPath);
            LockPath = Path.Combine(DirectoryPath, "modkit.lock.json");
        }

        public string DirectoryPath { get; }
        public string LockPath { get; }

        public ProcessResult WriteLock(string sha256 = Hash, IEnumerable<string>? extraArguments = null, string? output = null)
        {
            List<string> arguments =
            [
                "lock", "write",
                "--output", output ?? LockPath,
                "--schema-version", "1",
                "--repository", "abmcar/FarmTogether2-ModKit",
                "--workflow-commit", Commit,
                "--package-id", "FarmTogether2.GameApi.Ref",
                "--package-version", "1.0.0",
                "--release-tag", "v1.0.0",
                "--asset-name", "FarmTogether2.GameApi.Ref.1.0.0.nupkg",
                "--sha256", sha256
            ];
            if (extraArguments is not null)
                arguments.AddRange(extraArguments);
            return Run(arguments);
        }

        public ProcessResult VerifyLock() => Run(["lock", "verify", "--file", LockPath]);

        public ProcessResult Run(IEnumerable<string> toolArguments)
        {
            ProcessStartInfo startInfo = new("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (string argument in new[]
                     {
                         "run", "--project", ToolProject, "-c", TestBuildConfiguration.Current, "--no-build", "--"
                     }.Concat(toolArguments))
                startInfo.ArgumentList.Add(argument);
            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start dotnet.");
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

        public void AssertFailure(string? expected = null)
        {
            Assert.NotEqual(0, ExitCode);
            if (expected is not null)
                Assert.Contains(expected, $"{Stdout}\n{Stderr}", StringComparison.OrdinalIgnoreCase);
        }
    }
}
