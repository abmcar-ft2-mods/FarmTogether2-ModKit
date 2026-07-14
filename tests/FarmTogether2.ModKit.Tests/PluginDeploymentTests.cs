using System.Diagnostics;
using System.Text;
using Xunit;

namespace FarmTogether2.ModKit.Tests;

public sealed class PluginDeploymentTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private static readonly string Script = Path.Combine(Root, "scripts", "Install-ModPlugin.ps1");

    [Fact]
    public void SuccessfulDeploymentReplacesDllAndPdb()
    {
        using Fixture fixture = new();
        fixture.WriteSources();
        fixture.WriteExistingPair();

        fixture.Run().AssertSuccess();

        Assert.Equal("new dll\n", ReadNormalized(fixture.TargetDll));
        Assert.Equal("new pdb\n", ReadNormalized(fixture.TargetPdb));
        fixture.AssertNoAttemptFiles();
    }

    [Fact]
    public void FailureAfterDllPromotionRestoresThePreviousPair()
    {
        using Fixture fixture = new();
        fixture.WriteSources();
        fixture.WriteExistingPair();

        fixture.Run(failAt: "after-dll-promote").AssertFailure("after-dll-promote");

        Assert.Equal("old dll\n", ReadNormalized(fixture.TargetDll));
        Assert.Equal("old pdb\n", ReadNormalized(fixture.TargetPdb));
        fixture.AssertNoAttemptFiles();
    }

    [Fact]
    public void FailureAfterPdbPromotionRestoresPreviousDllAndRemovesNewPdbWhenNoPriorPdbExists()
    {
        using Fixture fixture = new();
        fixture.WriteSources();
        File.WriteAllText(fixture.TargetDll, "old dll\n", new UTF8Encoding(false));

        fixture.Run(failAt: "after-pdb-promote").AssertFailure("after-pdb-promote");

        Assert.Equal("old dll\n", ReadNormalized(fixture.TargetDll));
        Assert.False(File.Exists(fixture.TargetPdb));
        fixture.AssertNoAttemptFiles();
    }

    [Fact]
    public void PdbRollbackFailureDoesNotPreventDllRollback()
    {
        using Fixture fixture = new();
        fixture.WriteSources();
        fixture.WriteExistingPair();

        fixture.Run(
            failAt: "after-pdb-promote",
            rollbackFailAt: "before-pdb-rollback").AssertFailure("before-pdb-rollback");

        Assert.Equal("old dll\n", ReadNormalized(fixture.TargetDll));
        Assert.Equal("new pdb\n", ReadNormalized(fixture.TargetPdb));
        Assert.Contains(
            Directory.EnumerateFileSystemEntries(fixture.TargetDirectory),
            path => Path.GetFileName(path).Contains(".pdb.modkit-deploy-", StringComparison.Ordinal) &&
                path.EndsWith(".backup", StringComparison.Ordinal));
    }

    [Fact]
    public void ExistingDllTargetSymlinkIsRejectedWithoutChangingItsTarget()
    {
        using Fixture fixture = new();
        string outside = Path.Combine(fixture.DirectoryPath, "outside.dll");
        File.WriteAllText(outside, "outside\n", new UTF8Encoding(false));
        fixture.WriteSources();
        try
        {
            File.CreateSymbolicLink(fixture.TargetDll, outside);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        fixture.Run().AssertFailure("symlink");
        Assert.Equal("outside\n", ReadNormalized(outside));
        Assert.True(File.ResolveLinkTarget(fixture.TargetDll, returnFinalTarget: false) is not null);
        fixture.AssertNoAttemptFiles();
    }

    [Fact]
    public void ExistingPdbTargetSymlinkIsRejectedWithoutChangingItsTarget()
    {
        using Fixture fixture = new();
        string outside = Path.Combine(fixture.DirectoryPath, "outside.pdb");
        File.WriteAllText(outside, "outside pdb\n", new UTF8Encoding(false));
        fixture.WriteSources();
        File.WriteAllText(fixture.TargetDll, "old dll\n", new UTF8Encoding(false));
        try
        {
            File.CreateSymbolicLink(fixture.TargetPdb, outside);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        fixture.Run().AssertFailure("symlink");

        Assert.Equal("old dll\n", ReadNormalized(fixture.TargetDll));
        Assert.Equal("outside pdb\n", ReadNormalized(outside));
        Assert.True(File.ResolveLinkTarget(fixture.TargetPdb, returnFinalTarget: false) is not null);
        fixture.AssertNoAttemptFiles();
    }

    private static string ReadNormalized(string path) =>
        File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), $"modkit-plugin-deploy-{Guid.NewGuid():N}");
            GameDirectory = Path.Combine(DirectoryPath, "game");
            SourceDll = Path.Combine(DirectoryPath, "source.dll");
            SourcePdb = Path.Combine(DirectoryPath, "source.pdb");
            TargetDirectory = Path.Combine(GameDirectory, "BepInEx", "plugins", "Fixture");
            TargetDll = Path.Combine(TargetDirectory, "Fixture.dll");
            TargetPdb = Path.Combine(TargetDirectory, "Fixture.pdb");
            Directory.CreateDirectory(TargetDirectory);
        }

        public string DirectoryPath { get; }
        public string GameDirectory { get; }
        public string SourceDll { get; }
        public string SourcePdb { get; }
        public string TargetDirectory { get; }
        public string TargetDll { get; }
        public string TargetPdb { get; }

        public void WriteSources()
        {
            File.WriteAllText(SourceDll, "new dll\n", new UTF8Encoding(false));
            File.WriteAllText(SourcePdb, "new pdb\n", new UTF8Encoding(false));
        }

        public void WriteExistingPair()
        {
            File.WriteAllText(TargetDll, "old dll\n", new UTF8Encoding(false));
            File.WriteAllText(TargetPdb, "old pdb\n", new UTF8Encoding(false));
        }

        public ProcessResult Run(string? failAt = null, string? rollbackFailAt = null)
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
                "-SourceDll", SourceDll,
                "-SourcePdb", SourcePdb,
                "-GameDir", GameDirectory,
                "-AssemblyName", "Fixture"
            })
            {
                startInfo.ArgumentList.Add(argument);
            }
            startInfo.Environment["FARMT2_DEPLOY_FAIL_AT"] = failAt;
            startInfo.Environment["FARMT2_DEPLOY_ROLLBACK_FAIL_AT"] = rollbackFailAt;
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
