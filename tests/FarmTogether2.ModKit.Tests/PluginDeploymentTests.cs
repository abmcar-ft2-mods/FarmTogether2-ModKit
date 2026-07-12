using System.Diagnostics;
using System.Text;
using Xunit;

namespace FarmTogether2.ModKit.Tests;

public sealed class PluginDeploymentTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private static readonly string Script = Path.Combine(Root, "scripts", "Install-ModPlugin.ps1");

    [Fact]
    public void FailureAfterAtomicPromotionRestoresThePreviousDll()
    {
        using Fixture fixture = new();
        File.WriteAllText(fixture.Target, "old\n", new UTF8Encoding(false));
        File.WriteAllText(fixture.Source, "new\n", new UTF8Encoding(false));

        fixture.Run(failAt: "after-promote").AssertFailure("after-promote");
        Assert.Equal("old\n", File.ReadAllText(fixture.Target).Replace("\r\n", "\n", StringComparison.Ordinal));
        fixture.AssertNoAttemptFiles();

        fixture.Run().AssertSuccess();
        Assert.Equal("new\n", File.ReadAllText(fixture.Target).Replace("\r\n", "\n", StringComparison.Ordinal));
        fixture.AssertNoAttemptFiles();
    }

    [Fact]
    public void ExistingTargetSymlinkIsRejectedWithoutChangingItsTarget()
    {
        using Fixture fixture = new();
        string outside = Path.Combine(fixture.DirectoryPath, "outside.dll");
        File.WriteAllText(outside, "outside\n", new UTF8Encoding(false));
        File.WriteAllText(fixture.Source, "new\n", new UTF8Encoding(false));
        try
        {
            File.CreateSymbolicLink(fixture.Target, outside);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        fixture.Run().AssertFailure("symlink");
        Assert.Equal("outside\n", File.ReadAllText(outside).Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.True(File.ResolveLinkTarget(fixture.Target, returnFinalTarget: false) is not null);
        fixture.AssertNoAttemptFiles();
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), $"modkit-plugin-deploy-{Guid.NewGuid():N}");
            GameDirectory = Path.Combine(DirectoryPath, "game");
            Source = Path.Combine(DirectoryPath, "source.dll");
            TargetDirectory = Path.Combine(GameDirectory, "BepInEx", "plugins", "Fixture");
            Target = Path.Combine(TargetDirectory, "Fixture.dll");
            Directory.CreateDirectory(TargetDirectory);
        }

        public string DirectoryPath { get; }
        public string GameDirectory { get; }
        public string Source { get; }
        public string TargetDirectory { get; }
        public string Target { get; }

        public ProcessResult Run(string? failAt = null)
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
                "-SourceDll", Source, "-GameDir", GameDirectory, "-AssemblyName", "Fixture"
            })
            {
                startInfo.ArgumentList.Add(argument);
            }
            startInfo.Environment["FARMT2_DEPLOY_FAIL_AT"] = failAt;
            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start pwsh.");
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return new ProcessResult(process.ExitCode, stdout, stderr);
        }

        public void AssertNoAttemptFiles() => Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(TargetDirectory),
            path => Path.GetFileName(path).Contains(".modkit-deploy-", StringComparison.Ordinal));

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
    }
}
