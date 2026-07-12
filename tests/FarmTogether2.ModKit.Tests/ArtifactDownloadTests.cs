using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Xunit;

namespace FarmTogether2.ModKit.Tests;

public sealed class ArtifactDownloadTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private static readonly string Script = Path.Combine(Root, "scripts/Receive-VerifiedArtifact.ps1");

    [Fact]
    public void DownloadsRedirectedArchiveAndBindsArtifactIdentity()
    {
        using Fixture fixture = new();

        ProcessResult result = fixture.Run();

        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Equal(fixture.ArchiveSha256, Sha256(fixture.ArchivePath));
        Assert.Equal("candidate", File.ReadAllText(Path.Combine(fixture.Destination, "candidate.json")));
        Assert.Equal("payload", File.ReadAllText(Path.Combine(fixture.Destination, "payload", "data.bin")));
        Assert.Equal(1, fixture.Server.DownloadCount);
        Assert.Equal("Bearer fixture-token", fixture.Server.RedirectAuthorization);
        Assert.Null(fixture.Server.ArchiveAuthorization);

        string ghLog = File.ReadAllText(fixture.GhLogPath);
        Assert.Contains("api", ghLog, StringComparison.Ordinal);
        Assert.Contains("repos/owner/repo/actions/artifacts/42", ghLog, StringComparison.Ordinal);
        Assert.Contains("X-GitHub-Api-Version: 2026-03-10", ghLog, StringComparison.Ordinal);
        Assert.Contains("auth token", ghLog, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsPlainHttpArchiveUrlWithoutExplicitLoopbackTestSeam()
    {
        using Fixture fixture = new();

        ProcessResult result = fixture.Run(allowLoopbackHttp: false);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("must use HTTPS", result.Stderr, StringComparison.Ordinal);
        Assert.Null(fixture.Server.RedirectAuthorization);
        Assert.Equal(0, fixture.Server.DownloadCount);
        Assert.False(File.Exists(fixture.ArchivePath));
        Assert.False(Directory.Exists(fixture.Destination));
    }

    [Fact]
    public void RejectsHttpsToHttpRedirectBeforeContactingDowngradeTarget()
    {
        using Fixture fixture = new() { UseHttpsRedirect = true };

        ProcessResult result = fixture.Run(
            allowLoopbackHttp: false,
            skipLoopbackCertificateCheck: true);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("Bearer fixture-token", fixture.Server.RedirectAuthorization);
        Assert.Equal(0, fixture.Server.DownloadCount);
        Assert.Null(fixture.Server.ArchiveAuthorization);
        Assert.False(File.Exists(fixture.ArchivePath));
        Assert.False(Directory.Exists(fixture.Destination));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    [InlineData("run")]
    [InlineData("commit")]
    [InlineData("digest")]
    [InlineData("expired")]
    public void RejectsMismatchedApiArtifactIdentityBeforeDownload(string mismatch)
    {
        using Fixture fixture = new();
        fixture.MetadataArtifactId = mismatch == "id" ? 43 : Fixture.ArtifactId;
        fixture.MetadataName = mismatch == "name" ? "wrong-name" : Fixture.ArtifactName;
        fixture.MetadataRunId = mismatch == "run" ? 1002 : Fixture.RunId;
        fixture.MetadataCommit = mismatch == "commit" ? new string('b', 40) : Fixture.Commit;
        fixture.MetadataDigest = mismatch == "digest" ? "sha256:" + new string('0', 64) : fixture.ExpectedDigest;
        fixture.MetadataExpired = mismatch == "expired";

        ProcessResult result = fixture.Run();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(0, fixture.Server.DownloadCount);
        Assert.False(File.Exists(fixture.ArchivePath));
        Assert.False(Directory.Exists(fixture.Destination));
    }

    [Fact]
    public void RejectsDownloadedBytesThatDoNotMatchApiDigest()
    {
        using Fixture fixture = new();
        string wrongDigest = "sha256:" + Sha256(Encoding.UTF8.GetBytes("different archive"));
        fixture.MetadataDigest = wrongDigest;

        ProcessResult result = fixture.Run(expectedDigest: wrongDigest);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(1, fixture.Server.DownloadCount);
        Assert.False(File.Exists(fixture.ArchivePath));
        Assert.False(Directory.Exists(fixture.Destination));
    }

    [Fact]
    public void RejectsMismatchedPreexistingArchiveWithoutReplacingOrDownloading()
    {
        using Fixture fixture = new();
        byte[] existing = Encoding.UTF8.GetBytes("wrong cached archive");
        File.WriteAllBytes(fixture.ArchivePath, existing);

        ProcessResult result = fixture.Run();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(0, fixture.Server.DownloadCount);
        Assert.Equal(existing, File.ReadAllBytes(fixture.ArchivePath));
        Assert.False(Directory.Exists(fixture.Destination));
    }

    [Fact]
    public void RejectsPreexistingArchiveReparsePointWithoutFollowingIt()
    {
        using Fixture fixture = new();
        string outside = Path.Combine(fixture.RootDirectory, "outside.zip");
        File.WriteAllBytes(outside, fixture.ArchiveBytes);
        CreateFileSymbolicLinkOrSkip(fixture.ArchivePath, outside);

        ProcessResult result = fixture.Run();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(0, fixture.Server.DownloadCount);
        Assert.Equal(fixture.ArchiveBytes, File.ReadAllBytes(outside));
        Assert.True((File.GetAttributes(fixture.ArchivePath) & FileAttributes.ReparsePoint) != 0);
        Assert.False(Directory.Exists(fixture.Destination));
    }

    [Fact]
    public void RejectsArchiveParentReparseAncestorBeforeAnyWrite()
    {
        using Fixture fixture = new();
        string outside = Path.Combine(fixture.RootDirectory, "outside-cache");
        string outsideParent = Path.Combine(outside, "container");
        Directory.CreateDirectory(outsideParent);
        Directory.Delete(fixture.CacheRoot, recursive: true);
        CreateDirectorySymbolicLinkOrSkip(fixture.CacheRoot, outside);

        ProcessResult result = fixture.Run();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("reparse-point ancestor", result.Stderr, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outsideParent));
        Assert.Null(fixture.Server.RedirectAuthorization);
        Assert.Equal(0, fixture.Server.DownloadCount);
        Assert.False(Directory.Exists(fixture.Destination));
    }

    [WindowsFact]
    public void RejectsArchiveParentJunctionAncestorWhenCapabilityIsAvailable()
    {
        using Fixture fixture = new();
        string outside = Path.Combine(fixture.RootDirectory, "outside-junction-cache");
        string outsideParent = Path.Combine(outside, "container");
        Directory.CreateDirectory(outsideParent);
        Directory.Delete(fixture.CacheRoot, recursive: true);
        CreateDirectoryJunction(fixture.CacheRoot, outside);

        ProcessResult result = fixture.Run();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("reparse-point ancestor", result.Stderr, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outsideParent));
        Assert.Null(fixture.Server.RedirectAuthorization);
        Assert.Equal(0, fixture.Server.DownloadCount);
        Assert.False(Directory.Exists(fixture.Destination));
    }

    [Fact]
    public void RejectsDestinationParentReparseAncestorBeforeAnyWrite()
    {
        using Fixture fixture = new();
        string outside = Path.Combine(fixture.RootDirectory, "outside-live");
        string outsideParent = Path.Combine(outside, "container");
        Directory.CreateDirectory(outsideParent);
        Directory.Delete(fixture.LiveRoot, recursive: true);
        CreateDirectorySymbolicLinkOrSkip(fixture.LiveRoot, outside);

        ProcessResult result = fixture.Run();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("reparse-point ancestor", result.Stderr, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outsideParent));
        Assert.Null(fixture.Server.RedirectAuthorization);
        Assert.Equal(0, fixture.Server.DownloadCount);
    }

    [Fact]
    public void ResumesFromVerifiedArchiveAfterInjectedInterruption()
    {
        using Fixture fixture = new();

        ProcessResult interrupted = fixture.Run(failurePoint: "after-archive-verify");
        Assert.NotEqual(0, interrupted.ExitCode);
        Assert.Equal(fixture.ArchiveSha256, Sha256(fixture.ArchivePath));
        Assert.False(Directory.Exists(fixture.Destination));
        Assert.Equal(1, fixture.Server.DownloadCount);

        ProcessResult resumed = fixture.Run();

        Assert.True(resumed.ExitCode == 0, resumed.Stdout + Environment.NewLine + resumed.Stderr);
        Assert.Equal(1, fixture.Server.DownloadCount);
        Assert.Equal("payload", File.ReadAllText(Path.Combine(fixture.Destination, "payload", "data.bin")));
    }

    [Fact]
    public async Task RejectsReparseAncestorIntroducedBeforeFinalPromotionWithoutWritingThroughIt()
    {
        using Fixture fixture = new();
        AssertDirectorySymbolicLinkSupportOrSkip(fixture.RootDirectory);
        string archiveParent = Path.GetDirectoryName(fixture.ArchivePath)!;
        string signal = Path.Combine(archiveParent, "promotion.ready");
        string proceed = Path.Combine(archiveParent, "promotion.continue");
        Task<ProcessResult> running = Task.Run(() => fixture.Run(
            hookPoint: "after-fresh-manifest",
            hookSignalPath: signal,
            hookContinuePath: proceed));
        await WaitForReceiverHookAsync(running, signal, proceed);

        string movedLive = Path.Combine(fixture.RootDirectory, "original-live");
        string outsideLive = Path.Combine(fixture.RootDirectory, "outside-live");
        string outsideParent = Path.Combine(outsideLive, "container");
        Directory.CreateDirectory(outsideParent);
        Directory.Move(fixture.LiveRoot, movedLive);
        Directory.CreateSymbolicLink(fixture.LiveRoot, outsideLive);
        File.WriteAllText(proceed, "continue");

        ProcessResult result = await running;

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("reparse-point ancestor", result.Stderr, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outsideParent));
        Assert.False(Directory.Exists(fixture.Destination));
    }

    [Theory]
    [InlineData("file")]
    [InlineData("symlink")]
    public async Task RejectsFreshExtractionMutationAfterValidatedManifestAndCleansStaging(string mutation)
    {
        using Fixture fixture = new();
        if (mutation == "symlink")
            AssertFileSymbolicLinkSupportOrSkip(fixture.RootDirectory);
        string archiveParent = Path.GetDirectoryName(fixture.ArchivePath)!;
        string destinationParent = Path.GetDirectoryName(fixture.Destination)!;
        string signal = Path.Combine(archiveParent, $"fresh-{mutation}.ready");
        string proceed = Path.Combine(archiveParent, $"fresh-{mutation}.continue");
        string outside = Path.Combine(fixture.RootDirectory, $"outside-{mutation}.bin");
        File.WriteAllText(outside, "outside");
        Task<ProcessResult> running = Task.Run(() => fixture.Run(
            hookPoint: "after-fresh-manifest",
            hookSignalPath: signal,
            hookContinuePath: proceed));
        await WaitForReceiverHookAsync(running, signal, proceed);

        string extractionRoot = Assert.Single(Directory.EnumerateDirectories(
            destinationParent,
            $".{Path.GetFileName(fixture.Destination)}.extract-*"));
        string stagedPayload = Path.Combine(extractionRoot, "payload", "data.bin");
        if (mutation == "file")
        {
            File.WriteAllText(stagedPayload, "tampered");
        }
        else
        {
            File.Delete(stagedPayload);
            File.CreateSymbolicLink(stagedPayload, outside);
        }
        File.WriteAllText(proceed, "continue");

        ProcessResult result = await running;

        Assert.NotEqual(0, result.ExitCode);
        string expectedError = mutation == "file"
            ? "Fresh extraction changed after validation hash mismatch: payload/data.bin"
            : "Fresh extraction after validation contains a reparse point";
        AssertStandardErrorContains(result, expectedError);
        Assert.Equal("outside", File.ReadAllText(outside));
        Assert.False(Directory.Exists(fixture.Destination));
        Assert.Empty(Directory.EnumerateDirectories(
            destinationParent,
            $".{Path.GetFileName(fixture.Destination)}.extract-*"));
    }

    [Theory]
    [InlineData("file")]
    [InlineData("symlink")]
    public async Task RejectsExistingDestinationMutationAfterManifestWithoutReplacingLiveTree(string mutation)
    {
        using Fixture fixture = new();
        if (mutation == "symlink")
            AssertFileSymbolicLinkSupportOrSkip(fixture.RootDirectory);
        fixture.WriteValidDestination();
        string archiveParent = Path.GetDirectoryName(fixture.ArchivePath)!;
        string destinationParent = Path.GetDirectoryName(fixture.Destination)!;
        string signal = Path.Combine(archiveParent, "existing.ready");
        string proceed = Path.Combine(archiveParent, "existing.continue");
        Task<ProcessResult> running = Task.Run(() => fixture.Run(
            hookPoint: "after-existing-manifest",
            hookSignalPath: signal,
            hookContinuePath: proceed));
        await WaitForReceiverHookAsync(running, signal, proceed);

        string livePayload = Path.Combine(fixture.Destination, "payload", "data.bin");
        string outside = Path.Combine(fixture.RootDirectory, $"existing-outside-{mutation}.bin");
        File.WriteAllText(outside, "outside");
        if (mutation == "file")
        {
            File.WriteAllText(livePayload, "changed-during-validation");
        }
        else
        {
            File.Delete(livePayload);
            File.CreateSymbolicLink(livePayload, outside);
        }
        File.WriteAllText(proceed, "continue");

        ProcessResult result = await running;

        Assert.NotEqual(0, result.ExitCode);
        string expectedError = mutation == "file"
            ? "DestinationDirectory changed during validation hash mismatch: payload/data.bin"
            : "DestinationDirectory after validation contains a reparse point";
        AssertStandardErrorContains(result, expectedError);
        if (mutation == "file")
            Assert.Equal("changed-during-validation", File.ReadAllText(livePayload));
        else
            Assert.True((File.GetAttributes(livePayload) & FileAttributes.ReparsePoint) != 0);
        Assert.Equal("outside", File.ReadAllText(outside));
        Assert.Empty(Directory.EnumerateDirectories(
            destinationParent,
            $".{Path.GetFileName(fixture.Destination)}.extract-*"));
    }

    [Fact]
    public void RejectsReceiverTestHookOutsideExplicitLoopbackHttpSeam()
    {
        using Fixture fixture = new() { UseHttpsRedirect = true };
        string archiveParent = Path.GetDirectoryName(fixture.ArchivePath)!;
        string signal = Path.Combine(archiveParent, "forbidden.ready");
        string proceed = Path.Combine(archiveParent, "forbidden.continue");

        ProcessResult result = fixture.Run(
            allowLoopbackHttp: false,
            skipLoopbackCertificateCheck: true,
            hookPoint: "after-fresh-manifest",
            hookSignalPath: signal,
            hookContinuePath: proceed);

        Assert.NotEqual(0, result.ExitCode);
        AssertStandardErrorContains(result, "restricted to the explicit loopback HTTP test seam");
        Assert.False(File.Exists(signal));
        Assert.Null(fixture.Server.RedirectAuthorization);
        Assert.Equal(0, fixture.Server.DownloadCount);
    }

    [Fact]
    public void RejectsCrashInjectionOutsideExplicitLoopbackHttpSeam()
    {
        using Fixture fixture = new() { UseHttpsRedirect = true };

        ProcessResult result = fixture.Run(
            failurePoint: "after-archive-verify",
            allowLoopbackHttp: false,
            skipLoopbackCertificateCheck: true);

        Assert.NotEqual(0, result.ExitCode);
        AssertStandardErrorContains(result, "crash injection is restricted to the explicit loopback HTTP test seam");
        Assert.Null(fixture.Server.RedirectAuthorization);
        Assert.Equal(0, fixture.Server.DownloadCount);
    }

    [Theory]
    [InlineData("traversal", "dot path segment")]
    [InlineData("dot-segment", "dot path segment")]
    [InlineData("rooted", "absolute or UNC-rooted")]
    [InlineData("unc", "absolute or UNC-rooted")]
    [InlineData("drive", "drive-rooted")]
    [InlineData("ads", "alternate-data-stream colon")]
    [InlineData("duplicate", "duplicate normalized entry name")]
    [InlineData("case-collision", "duplicate normalized entry name")]
    [InlineData("directory-entry", "directory entries are not allowed")]
    [InlineData("directory-attribute", "marked as a directory")]
    [InlineData("windows-reparse", "marked as a reparse point")]
    [InlineData("file-directory-collision", "file/directory path collision")]
    [InlineData("symlink", "not a regular file")]
    public void RejectsUnsafeZipWithoutCreatingLiveDestination(string kind, string expectedError)
    {
        using Fixture fixture = new();
        fixture.SetArchive(CreateUnsafeArchive(kind));

        ProcessResult result = fixture.Run();

        Assert.NotEqual(0, result.ExitCode);
        AssertStandardErrorContains(result, expectedError);
        Assert.False(Directory.Exists(fixture.Destination));
        Assert.False(File.Exists(Path.Combine(fixture.RootDirectory, "escape.txt")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(fixture.Destination)!, "escape.txt")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(fixture.Destination)!));
    }

    [Fact]
    public void CorruptZipDoesNotMutateExistingDestination()
    {
        using Fixture fixture = new();
        fixture.WriteValidDestination();
        SortedDictionary<string, string> before = SnapshotTree(fixture.Destination);
        fixture.SetArchive(Encoding.UTF8.GetBytes("not a ZIP archive"));

        ProcessResult result = fixture.Run();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(before, SnapshotTree(fixture.Destination));
        Assert.Empty(Directory.EnumerateFileSystemEntries(
            Path.GetDirectoryName(fixture.Destination)!,
            $".{Path.GetFileName(fixture.Destination)}.extract-*"));
    }

    [Fact]
    public void AcceptsExistingDestinationOnlyWhenItsExactTreeMatchesFreshExtraction()
    {
        using Fixture fixture = new();
        fixture.WriteValidDestination();
        SortedDictionary<string, string> before = SnapshotTree(fixture.Destination);

        ProcessResult result = fixture.Run();

        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Equal(before, SnapshotTree(fixture.Destination));
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("extra-directory")]
    public void RejectsDifferentExistingDestinationWithoutMutatingIt(string mismatch)
    {
        using Fixture fixture = new();
        fixture.WriteValidDestination();
        string payload = Path.Combine(fixture.Destination, "payload", "data.bin");
        if (mismatch == "changed")
            File.WriteAllText(payload, "changed");
        else if (mismatch == "missing")
            File.Delete(payload);
        else if (mismatch == "extra")
            File.WriteAllText(Path.Combine(fixture.Destination, "extra.txt"), "extra");
        else
            Directory.CreateDirectory(Path.Combine(fixture.Destination, "empty-extra"));
        SortedDictionary<string, string> before = SnapshotTree(fixture.Destination);

        ProcessResult result = fixture.Run();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(before, SnapshotTree(fixture.Destination));
    }

    [Fact]
    public void RejectsReparsePointInExistingDestination()
    {
        using Fixture fixture = new();
        Directory.CreateDirectory(Path.Combine(fixture.Destination, "payload"));
        File.WriteAllText(Path.Combine(fixture.Destination, "candidate.json"), "candidate");
        string outside = Path.Combine(fixture.RootDirectory, "outside.bin");
        File.WriteAllText(outside, "outside");
        string link = Path.Combine(fixture.Destination, "payload", "data.bin");
        CreateFileSymbolicLinkOrSkip(link, outside);

        ProcessResult result = fixture.Run();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("outside", File.ReadAllText(outside));
        Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
    }

    private static byte[] CreateUnsafeArchive(string kind) => kind switch
    {
        "traversal" => CreateArchive(new ZipItem("../escape.txt", "escape")),
        "dot-segment" => CreateArchive(new ZipItem("safe/./file.txt", "dot")),
        "rooted" => CreateArchive(new ZipItem("/rooted.txt", "rooted")),
        "unc" => CreateArchive(new ZipItem(@"\\server\share\file.txt", "unc")),
        "drive" => CreateArchive(new ZipItem(@"C:\escape.txt", "drive")),
        "ads" => CreateArchive(new ZipItem("safe/file.txt:stream", "ads")),
        "duplicate" => CreateArchive(new ZipItem("dir/file.txt", "one"), new ZipItem(@"dir\file.txt", "two")),
        "case-collision" => CreateArchive(new ZipItem("Dir/File.txt", "one"), new ZipItem("dir/file.txt", "two")),
        "directory-entry" => CreateArchive(new ZipItem("dir/", "")),
        "directory-attribute" => CreateArchive(new ZipItem("dir", "", (int)FileAttributes.Directory)),
        "windows-reparse" => CreateArchive(new ZipItem("link", "target", (int)FileAttributes.ReparsePoint)),
        "file-directory-collision" => CreateArchive(new ZipItem("node", "file"), new ZipItem("node/child.txt", "child")),
        "symlink" => CreateArchive(new ZipItem("link", "target", (0xA000 | 0x1FF) << 16)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    private static byte[] CreateArchive(params ZipItem[] items)
    {
        using MemoryStream stream = new();
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (ZipItem item in items)
            {
                ZipArchiveEntry entry = archive.CreateEntry(item.Name, CompressionLevel.NoCompression);
                if (item.ExternalAttributes is int attributes)
                    entry.ExternalAttributes = attributes;
                using StreamWriter writer = new(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write(item.Content);
            }
        }
        return stream.ToArray();
    }

    private static async Task WaitForReceiverHookAsync(
        Task<ProcessResult> running,
        string signalPath,
        string continuePath)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (!File.Exists(signalPath) && !running.IsCompleted && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        if (!File.Exists(signalPath) && !running.IsCompleted)
        {
            File.WriteAllText(continuePath, "continue");
            ProcessResult timedOut = await running;
            Assert.Fail("Receiver did not reach the requested test hook." + Environment.NewLine + timedOut.Stderr);
        }
        if (running.IsCompleted)
        {
            ProcessResult early = await running;
            Assert.Fail("Receiver exited before the requested test hook." + Environment.NewLine + early.Stderr);
        }
    }

    private static void AssertFileSymbolicLinkSupportOrSkip(string directory)
    {
        string target = Path.Combine(directory, $"symlink-probe-target-{Guid.NewGuid():N}");
        string link = Path.Combine(directory, $"symlink-probe-link-{Guid.NewGuid():N}");
        File.WriteAllText(target, "probe");
        try
        {
            CreateFileSymbolicLinkOrSkip(link, target);
        }
        finally
        {
            if (File.Exists(link))
                File.Delete(link);
            File.Delete(target);
        }
    }

    private static void CreateFileSymbolicLinkOrSkip(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or
                                          PlatformNotSupportedException or IOException)
        {
            Assert.Fail($"Symbolic links are required by this test: {exception.Message}");
        }
    }

    private static void CreateDirectorySymbolicLinkOrSkip(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or
                                          PlatformNotSupportedException or IOException)
        {
            Assert.Fail($"Directory symbolic links are required by this test: {exception.Message}");
        }
    }

    private static void CreateDirectoryJunction(string linkPath, string targetPath)
    {
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add($"mklink /J \"{linkPath}\" \"{targetPath}\"");
        using Process process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Unable to start cmd.exe for junction capability probe.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || !Directory.Exists(linkPath))
        {
            Assert.Fail(
                $"Directory junctions are unavailable: exit={process.ExitCode} stdout={stdout} stderr={stderr}");
        }
    }

    private static void AssertDirectorySymbolicLinkSupportOrSkip(string directory)
    {
        string target = Path.Combine(directory, $"symlink-probe-target-{Guid.NewGuid():N}");
        string link = Path.Combine(directory, $"symlink-probe-link-{Guid.NewGuid():N}");
        Directory.CreateDirectory(target);
        try
        {
            CreateDirectorySymbolicLinkOrSkip(link, target);
        }
        finally
        {
            if (Directory.Exists(link))
                Directory.Delete(link);
            Directory.Delete(target);
        }
    }

    private static void AssertStandardErrorContains(ProcessResult result, string expected)
    {
        string normalized = string.Join(
            " ",
            result.Stderr.Replace('|', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.True(normalized.Contains(expected, StringComparison.Ordinal), result.Stderr);
    }

    private static SortedDictionary<string, string> SnapshotTree(string directory)
    {
        var snapshot = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
            if (Directory.Exists(path))
                snapshot.Add("D:" + relative, "-");
            else
                snapshot.Add("F:" + relative, Sha256(path));
        }
        return snapshot;
    }

    private static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record ZipItem(string Name, string Content, int? ExternalAttributes = null);

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

    public sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows())
                Skip = "Windows junction coverage runs only on Windows.";
        }
    }

    private sealed class Fixture : IDisposable
    {
        public const long ArtifactId = 42;
        public const long RunId = 1001;
        public const string ArtifactName = "candidate-artifact";
        public const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        private readonly string _shimDirectory;
        private readonly string _metadataPath;

        public Fixture()
        {
            RootDirectory = Path.Combine(Path.GetTempPath(), $"modkit-artifact-{Guid.NewGuid():N}");
            Directory.CreateDirectory(RootDirectory);
            CacheRoot = Path.Combine(RootDirectory, "cache");
            LiveRoot = Path.Combine(RootDirectory, "live");
            string cache = Path.Combine(CacheRoot, "container");
            string live = Path.Combine(LiveRoot, "container");
            _shimDirectory = Path.Combine(RootDirectory, "shim");
            Directory.CreateDirectory(cache);
            Directory.CreateDirectory(live);
            Directory.CreateDirectory(_shimDirectory);
            ArchivePath = Path.Combine(cache, "artifact.zip");
            Destination = Path.Combine(live, "artifact");
            _metadataPath = Path.Combine(RootDirectory, "metadata.json");
            GhLogPath = Path.Combine(RootDirectory, "gh.log");
            Server = new LocalArtifactServer();
            CreateGhShim();
            SetArchive(CreateArchive(
                new ZipItem("candidate.json", "candidate"),
                new ZipItem("payload/data.bin", "payload")));
        }

        public string RootDirectory { get; }
        public string CacheRoot { get; }
        public string LiveRoot { get; }
        public string ArchivePath { get; }
        public string Destination { get; }
        public string GhLogPath { get; }
        public LocalArtifactServer Server { get; }
        public byte[] ArchiveBytes { get; private set; } = [];
        public string ArchiveSha256 => Sha256(ArchiveBytes);
        public string ExpectedDigest => "sha256:" + ArchiveSha256;

        public long MetadataArtifactId { get; set; } = ArtifactId;
        public long MetadataRunId { get; set; } = RunId;
        public string MetadataName { get; set; } = ArtifactName;
        public string MetadataCommit { get; set; } = Commit;
        public string? MetadataDigest { get; set; }
        public bool MetadataExpired { get; set; }
        public bool UseHttpsRedirect { get; set; }

        public void SetArchive(byte[] bytes)
        {
            ArchiveBytes = bytes;
            Server.ArchiveBytes = bytes;
            MetadataDigest = ExpectedDigest;
        }

        public void WriteValidDestination()
        {
            Directory.CreateDirectory(Path.Combine(Destination, "payload"));
            File.WriteAllText(Path.Combine(Destination, "candidate.json"), "candidate");
            File.WriteAllText(Path.Combine(Destination, "payload", "data.bin"), "payload");
        }

        public ProcessResult Run(
            string? expectedDigest = null,
            string? failurePoint = null,
            bool allowLoopbackHttp = true,
            bool skipLoopbackCertificateCheck = false,
            string? hookPoint = null,
            string? hookSignalPath = null,
            string? hookContinuePath = null)
        {
            WriteMetadata();
            var arguments = new[]
            {
                "-NoProfile", "-File", Script,
                "-Repository", "owner/repo",
                "-ArtifactId", ArtifactId.ToString(CultureInfo.InvariantCulture),
                "-ExpectedArtifactName", ArtifactName,
                "-ExpectedRunId", RunId.ToString(CultureInfo.InvariantCulture),
                "-ExpectedCommit", Commit,
                "-ExpectedDigest", expectedDigest ?? ExpectedDigest,
                "-ArchivePath", ArchivePath,
                "-DestinationDirectory", Destination
            };
            var startInfo = new ProcessStartInfo("pwsh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);
            startInfo.Environment["PATH"] = _shimDirectory + Path.PathSeparator +
                (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
            startInfo.Environment["FAKE_GH_METADATA_PATH"] = _metadataPath;
            startInfo.Environment["FAKE_GH_LOG_PATH"] = GhLogPath;
            startInfo.Environment.Remove("FARMT2_MODKIT_TEST_ALLOW_LOOPBACK_HTTP");
            startInfo.Environment.Remove("FARMT2_MODKIT_TEST_SKIP_LOOPBACK_CERTIFICATE_CHECK");
            startInfo.Environment.Remove("FARMT2_MODKIT_TEST_FAIL_AT");
            startInfo.Environment.Remove("FARMT2_MODKIT_TEST_HOOK_POINT");
            startInfo.Environment.Remove("FARMT2_MODKIT_TEST_HOOK_SIGNAL");
            startInfo.Environment.Remove("FARMT2_MODKIT_TEST_HOOK_CONTINUE");
            if (allowLoopbackHttp)
                startInfo.Environment["FARMT2_MODKIT_TEST_ALLOW_LOOPBACK_HTTP"] = "1";
            if (skipLoopbackCertificateCheck)
                startInfo.Environment["FARMT2_MODKIT_TEST_SKIP_LOOPBACK_CERTIFICATE_CHECK"] = "1";
            if (failurePoint is not null)
                startInfo.Environment["FARMT2_MODKIT_TEST_FAIL_AT"] = failurePoint;
            if (hookPoint is not null)
                startInfo.Environment["FARMT2_MODKIT_TEST_HOOK_POINT"] = hookPoint;
            if (hookSignalPath is not null)
                startInfo.Environment["FARMT2_MODKIT_TEST_HOOK_SIGNAL"] = hookSignalPath;
            if (hookContinuePath is not null)
                startInfo.Environment["FARMT2_MODKIT_TEST_HOOK_CONTINUE"] = hookContinuePath;

            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start pwsh.");
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return new ProcessResult(process.ExitCode, stdout, stderr);
        }

        private void WriteMetadata()
        {
            var metadata = new
            {
                id = MetadataArtifactId,
                name = MetadataName,
                expired = MetadataExpired,
                digest = MetadataDigest,
                archive_download_url = UseHttpsRedirect ? Server.HttpsRedirectUrl : Server.RedirectUrl,
                workflow_run = new { id = MetadataRunId, head_sha = MetadataCommit }
            };
            File.WriteAllText(_metadataPath, JsonSerializer.Serialize(metadata));
        }

        private void CreateGhShim()
        {
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(Path.Combine(_shimDirectory, "gh.cmd"),
                    "@echo off\r\n" +
                    "echo %*>>\"%FAKE_GH_LOG_PATH%\"\r\n" +
                    "if \"%1\"==\"api\" (type \"%FAKE_GH_METADATA_PATH%\" & exit /b 0)\r\n" +
                    "if \"%1 %2\"==\"auth token\" (echo fixture-token& exit /b 0)\r\n" +
                    "exit /b 64\r\n");
            }
            else
            {
                string path = Path.Combine(_shimDirectory, "gh");
                File.WriteAllText(path,
                    "#!/bin/sh\n" +
                    "printf '%s\\n' \"$*\" >> \"$FAKE_GH_LOG_PATH\"\n" +
                    "if [ \"$1\" = api ]; then cat \"$FAKE_GH_METADATA_PATH\"; exit 0; fi\n" +
                    "if [ \"$1\" = auth ] && [ \"$2\" = token ]; then printf '%s\\n' fixture-token; exit 0; fi\n" +
                    "exit 64\n");
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        public void Dispose()
        {
            Server.Dispose();
            if (Directory.Exists(RootDirectory))
                Directory.Delete(RootDirectory, recursive: true);
        }
    }

    private sealed class LocalArtifactServer : IDisposable
    {
        private readonly TcpListener _redirectListener;
        private readonly TcpListener _httpsRedirectListener;
        private readonly TcpListener _archiveListener;
        private readonly RSA _certificateKey;
        private readonly X509Certificate2 _certificate;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _redirectTask;
        private readonly Task _httpsRedirectTask;
        private readonly Task _archiveTask;
        private int _downloadCount;

        public LocalArtifactServer()
        {
            _redirectListener = new TcpListener(IPAddress.Loopback, 0);
            _httpsRedirectListener = new TcpListener(IPAddress.Loopback, 0);
            _archiveListener = new TcpListener(IPAddress.Loopback, 0);
            _redirectListener.Start();
            _httpsRedirectListener.Start();
            _archiveListener.Start();
            int redirectPort = ((IPEndPoint)_redirectListener.LocalEndpoint).Port;
            int httpsRedirectPort = ((IPEndPoint)_httpsRedirectListener.LocalEndpoint).Port;
            int archivePort = ((IPEndPoint)_archiveListener.LocalEndpoint).Port;
            RedirectUrl = $"http://127.0.0.1:{redirectPort}/redirect";
            HttpsRedirectUrl = $"https://127.0.0.1:{httpsRedirectPort}/redirect";
            ArchiveUrl = $"http://127.0.0.1:{archivePort}/archive";
            _certificateKey = RSA.Create(2048);
            var certificateRequest = new CertificateRequest(
                "CN=127.0.0.1",
                _certificateKey,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            var subjectAlternativeName = new SubjectAlternativeNameBuilder();
            subjectAlternativeName.AddIpAddress(IPAddress.Loopback);
            certificateRequest.CertificateExtensions.Add(subjectAlternativeName.Build());
            _certificate = certificateRequest.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(1));
            _redirectTask = Task.Run(() => ServeAsync(_redirectListener, isRedirect: true, useTls: false));
            _httpsRedirectTask = Task.Run(() => ServeAsync(_httpsRedirectListener, isRedirect: true, useTls: true));
            _archiveTask = Task.Run(() => ServeAsync(_archiveListener, isRedirect: false, useTls: false));
        }

        public string RedirectUrl { get; }
        public string HttpsRedirectUrl { get; }
        public string ArchiveUrl { get; }
        public byte[] ArchiveBytes { get; set; } = [];
        public int DownloadCount => Volatile.Read(ref _downloadCount);
        public string? RedirectAuthorization { get; private set; }
        public string? ArchiveAuthorization { get; private set; }

        private async Task ServeAsync(TcpListener listener, bool isRedirect, bool useTls)
        {
            try
            {
                while (!_cancellation.IsCancellationRequested)
                {
                    using TcpClient client = await listener.AcceptTcpClientAsync(_cancellation.Token);
                    await HandleAsync(client, isRedirect, useTls);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SocketException) when (_cancellation.IsCancellationRequested)
            {
            }
        }

        private async Task HandleAsync(TcpClient client, bool isRedirect, bool useTls)
        {
            Stream stream = client.GetStream();
            if (useTls)
            {
                var tlsStream = new SslStream(stream, leaveInnerStreamOpen: false);
                await tlsStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _certificate,
                    EnabledSslProtocols = SslProtocols.Tls12
                }, _cancellation.Token);
                stream = tlsStream;
            }
            using (stream)
            {
                string request = await ReadHeadersAsync(stream, _cancellation.Token);
                string[] lines = request.Split("\r\n", StringSplitOptions.None);
                string path = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries)[1];
                Dictionary<string, string> headers = lines.Skip(1)
                    .Where(line => line.Contains(':'))
                    .Select(line => line.Split(':', 2))
                    .ToDictionary(parts => parts[0], parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);

                if (isRedirect && path == "/redirect")
                {
                    headers.TryGetValue("Authorization", out string? authorization);
                    RedirectAuthorization = authorization;
                    await WriteResponseAsync(stream, "302 Found", [], new Dictionary<string, string>
                    {
                        ["Location"] = ArchiveUrl
                    });
                }
                else if (!isRedirect && path == "/archive")
                {
                    Interlocked.Increment(ref _downloadCount);
                    headers.TryGetValue("Authorization", out string? authorization);
                    ArchiveAuthorization = authorization;
                    await WriteResponseAsync(stream, "200 OK", ArchiveBytes, new Dictionary<string, string>
                    {
                        ["Content-Type"] = "application/zip"
                    });
                }
                else
                {
                    await WriteResponseAsync(stream, "404 Not Found", [], new Dictionary<string, string>());
                }
            }
        }

        private static async Task<string> ReadHeadersAsync(Stream stream, CancellationToken cancellationToken)
        {
            using MemoryStream buffer = new();
            byte[] one = new byte[1];
            while (buffer.Length < 64 * 1024)
            {
                int read = await stream.ReadAsync(one, cancellationToken);
                if (read == 0)
                    break;
                buffer.WriteByte(one[0]);
                if (buffer.Length >= 4)
                {
                    byte[] bytes = buffer.GetBuffer();
                    int length = checked((int)buffer.Length);
                    if (bytes[length - 4] == '\r' && bytes[length - 3] == '\n' &&
                        bytes[length - 2] == '\r' && bytes[length - 1] == '\n')
                        return Encoding.ASCII.GetString(bytes, 0, length);
                }
            }
            throw new InvalidDataException("HTTP fixture received an invalid request.");
        }

        private static async Task WriteResponseAsync(
            Stream stream,
            string status,
            byte[] body,
            IReadOnlyDictionary<string, string> headers)
        {
            var builder = new StringBuilder()
                .Append("HTTP/1.1 ").Append(status).Append("\r\n")
                .Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n")
                .Append("Connection: close\r\n");
            foreach ((string name, string value) in headers)
                builder.Append(name).Append(": ").Append(value).Append("\r\n");
            builder.Append("\r\n");
            byte[] prefix = Encoding.ASCII.GetBytes(builder.ToString());
            await stream.WriteAsync(prefix);
            await stream.WriteAsync(body);
        }

        public void Dispose()
        {
            _cancellation.Cancel();
            _redirectListener.Stop();
            _httpsRedirectListener.Stop();
            _archiveListener.Stop();
            try
            {
                Task.WhenAll(_redirectTask, _httpsRedirectTask, _archiveTask).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
            _certificate.Dispose();
            _certificateKey.Dispose();
            _cancellation.Dispose();
        }
    }
}
