using System.Diagnostics;
using System.Security.Cryptography;
using Xunit;

namespace FarmTogether2.ModKit.Tests;

public sealed class AtomicFileTransitionTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private static readonly string Script = Path.Combine(Root, "scripts/Set-VerifiedFileAtomically.ps1");

    [Fact]
    public void ReplacesAnAllowedLiveFileAndRemovesTemporary()
    {
        using Fixture fixture = new();
        fixture.WriteSource("desired");
        fixture.WriteDestination("original");

        ProcessResult result = fixture.Run(allowedCurrentSha256: fixture.DestinationHash());

        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Equal(fixture.SourceHash(), fixture.DestinationHash());
        Assert.False(File.Exists(fixture.Temporary));
    }

    [Fact]
    public void MissingDestinationRequiresExplicitPermission()
    {
        using Fixture fixture = new();
        fixture.WriteSource("desired");

        ProcessResult rejected = fixture.Run();
        Assert.NotEqual(0, rejected.ExitCode);
        Assert.False(File.Exists(fixture.Destination));

        ProcessResult accepted = fixture.Run(allowMissingCurrent: true);
        Assert.Equal(0, accepted.ExitCode);
        Assert.Equal(fixture.SourceHash(), fixture.DestinationHash());
    }

    [Fact]
    public void ThirdLiveHashFailsWithoutChangingLiveOrTemporary()
    {
        using Fixture fixture = new();
        fixture.WriteSource("desired");
        fixture.WriteDestination("third-party");
        File.WriteAllText(fixture.Temporary, "preserve-me");
        string liveBefore = fixture.DestinationHash();
        string temporaryBefore = Sha256(fixture.Temporary);

        ProcessResult result = fixture.Run(allowedCurrentSha256: Sha256Text("expected-original"));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(liveBefore, fixture.DestinationHash());
        Assert.Equal(temporaryBefore, Sha256(fixture.Temporary));
    }

    [Fact]
    public void TruncatedJournaledTemporaryIsRestagedOnlyWhenLiveIsAllowed()
    {
        using Fixture fixture = new();
        fixture.WriteSource("desired");
        fixture.WriteDestination("original");
        File.WriteAllText(fixture.Temporary, "truncated");

        ProcessResult result = fixture.Run(allowedCurrentSha256: fixture.DestinationHash());

        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Equal(fixture.SourceHash(), fixture.DestinationHash());
        Assert.False(File.Exists(fixture.Temporary));
    }

    [Fact]
    public void DesiredLiveFileReconcilesKnownTemporaryIdempotently()
    {
        using Fixture fixture = new();
        fixture.WriteSource("desired");
        fixture.WriteDestination("desired");
        File.WriteAllText(fixture.Temporary, "truncated");

        ProcessResult result = fixture.Run();

        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Equal(fixture.SourceHash(), fixture.DestinationHash());
        Assert.False(File.Exists(fixture.Temporary));
    }

    [Theory]
    [InlineData("before-stage-copy")]
    [InlineData("after-stage-copy")]
    [InlineData("after-stage-hash")]
    [InlineData("before-atomic-move")]
    [InlineData("after-atomic-move")]
    [InlineData("after-destination-hash")]
    public void RerunConvergesAfterInjectedFileBoundaryFailure(string failurePoint)
    {
        using Fixture fixture = new();
        fixture.WriteSource("desired");
        fixture.WriteDestination("original");
        string allowed = fixture.DestinationHash();

        ProcessResult interrupted = fixture.Run(allowedCurrentSha256: allowed, failurePoint: failurePoint);
        Assert.NotEqual(0, interrupted.ExitCode);

        ProcessResult resumed = fixture.Run(allowedCurrentSha256: allowed);
        Assert.Equal(0, resumed.ExitCode);
        Assert.Equal(fixture.SourceHash(), fixture.DestinationHash());
        Assert.False(File.Exists(fixture.Temporary));
    }

    [Fact]
    public void RejectsTemporaryOutsideDestinationDirectory()
    {
        using Fixture fixture = new(temporaryInDestinationDirectory: false);
        fixture.WriteSource("desired");
        fixture.WriteDestination("original");
        string before = fixture.DestinationHash();

        ProcessResult result = fixture.Run(allowedCurrentSha256: before);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(before, fixture.DestinationHash());
    }

    private static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string Sha256Text(string value) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"modkit-atomic-{Guid.NewGuid():N}");

        public Fixture(bool temporaryInDestinationDirectory = true)
        {
            Directory.CreateDirectory(_root);
            string live = Path.Combine(_root, "live");
            Directory.CreateDirectory(live);
            Source = Path.Combine(_root, "candidate.dll");
            Destination = Path.Combine(live, "plugin.dll");
            Temporary = temporaryInDestinationDirectory
                ? Path.Combine(live, ".plugin.test-attempt.tmp")
                : Path.Combine(_root, ".plugin.test-attempt.tmp");
        }

        public string Source { get; }
        public string Destination { get; }
        public string Temporary { get; }

        public void WriteSource(string content) => File.WriteAllText(Source, content);
        public void WriteDestination(string content) => File.WriteAllText(Destination, content);
        public string SourceHash() => Sha256(Source);
        public string DestinationHash() => Sha256(Destination);

        public ProcessResult Run(
            string? allowedCurrentSha256 = null,
            bool allowMissingCurrent = false,
            string? failurePoint = null)
        {
            var arguments = new List<string>
            {
                "-NoProfile", "-File", Script,
                "-Source", Source,
                "-Destination", Destination,
                "-ExpectedSha256", SourceHash(),
                "-TemporaryPath", Temporary
            };
            if (allowedCurrentSha256 is not null)
            {
                arguments.Add("-AllowedCurrentSha256");
                arguments.Add(allowedCurrentSha256);
            }
            if (allowMissingCurrent)
                arguments.Add("-AllowMissingCurrent");

            var startInfo = new ProcessStartInfo("pwsh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);
            if (failurePoint is not null)
                startInfo.Environment["FARMT2_MODKIT_TEST_FAIL_AT"] = failurePoint;

            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start pwsh.");
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return new ProcessResult(process.ExitCode, stdout, stderr);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}
