using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace FarmTogether2.ModKit.Tests;

public sealed class ModKitVersionTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private static readonly string VersionScript = Path.Combine(Root, "scripts", "Set-ModKitVersion.ps1");
    private static readonly string PackageScript = Path.Combine(Root, "scripts", "Pack-GameApiRef.ps1");

    [Fact]
    public void VersionEditorChangesOnlyPackageVersionAndPrependsChangelogEntry()
    {
        using Fixture fixture = new();
        string lockBefore = Sha256(fixture.LockPath);
        string stubBefore = Sha256(fixture.StubProjectPath);

        fixture.RunVersion("1.2.3").AssertSuccess();

        Assert.Contains("<Version>1.2.3</Version>", File.ReadAllText(fixture.RefProjectPath), StringComparison.Ordinal);
        Assert.StartsWith("# Changelog\n\n## 1.2.3 - ", File.ReadAllText(fixture.ChangelogPath), StringComparison.Ordinal);
        Assert.Contains("- Prepare `FarmTogether2.GameApi.Ref` version 1.2.3.", File.ReadAllText(fixture.ChangelogPath), StringComparison.Ordinal);
        Assert.Equal(lockBefore, Sha256(fixture.LockPath));
        Assert.Equal(stubBefore, Sha256(fixture.StubProjectPath));
        fixture.AssertNoVersionTemporaries();

        Dictionary<string, string> first = fixture.SnapshotFiles();
        fixture.RunVersion("1.2.3").AssertSuccess();
        Assert.Equal(first, fixture.SnapshotFiles());
        fixture.AssertNoVersionTemporaries();
    }

    [Fact]
    public void InterruptedVersionEditConvergesOnRerun()
    {
        using Fixture fixture = new();

        fixture.RunVersion("2.0.0", failurePoint: "after-project-promotion").AssertFailure(197);
        Assert.Contains("<Version>2.0.0</Version>", File.ReadAllText(fixture.RefProjectPath), StringComparison.Ordinal);
        Assert.DoesNotContain("## 2.0.0 - ", File.ReadAllText(fixture.ChangelogPath), StringComparison.Ordinal);

        fixture.RunVersion("2.0.0").AssertSuccess();
        Assert.Contains("<Version>2.0.0</Version>", File.ReadAllText(fixture.RefProjectPath), StringComparison.Ordinal);
        Assert.Equal(1, Count(File.ReadAllText(fixture.ChangelogPath), "## 2.0.0 - "));
        fixture.AssertNoVersionTemporaries();
    }

    [Fact]
    public void VersionEditorPreservesCrLfChangelogLineEndings()
    {
        using Fixture fixture = new();
        string crlf = File.ReadAllText(fixture.ChangelogPath).Replace("\n", "\r\n", StringComparison.Ordinal);
        File.WriteAllText(fixture.ChangelogPath, crlf, new UTF8Encoding(false));

        fixture.RunVersion("1.0.1").AssertSuccess();

        string written = File.ReadAllText(fixture.ChangelogPath);
        Assert.Contains("## 1.0.1 - ", written, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", written.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        fixture.RunVersion("1.0.1").AssertSuccess();
        Assert.Equal(written, File.ReadAllText(fixture.ChangelogPath));
    }

    [Fact]
    public void InvalidVersionAndUnknownParameterDoNotMutateFiles()
    {
        using Fixture fixture = new();
        Dictionary<string, string> before = fixture.SnapshotFiles();

        fixture.RunVersion("01.0.0").AssertFailure();
        Assert.Equal(before, fixture.SnapshotFiles());

        fixture.RunVersion("1.2.3", extraArguments: ["-Unexpected", "value"]).AssertFailure();
        Assert.Equal(before, fixture.SnapshotFiles());
        fixture.AssertNoVersionTemporaries();
    }

    [Fact]
    public void PackageFilenameComesFromReferenceProjectVersion()
    {
        using Fixture fixture = new();
        fixture.RunVersion("3.4.5").AssertSuccess();

        ProcessResult result = fixture.RunPack();

        result.AssertSuccess();
        string expected = Path.Combine(fixture.PackageOutput, "FarmTogether2.GameApi.Ref.3.4.5.nupkg");
        Assert.True(File.Exists(expected), $"Expected package was not created: {expected}");
        Assert.Contains(expected, File.ReadAllText(fixture.DotNetLog), StringComparison.Ordinal);
        Assert.DoesNotContain("FarmTogether2.GameApi.Ref.1.0.0.nupkg", File.ReadAllText(fixture.DotNetLog), StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownPackParameterDoesNotCreateOutputOrInvokeDotNet()
    {
        using Fixture fixture = new();

        fixture.RunPack(["-Unexpected", "value"]).AssertFailure();

        Assert.False(Directory.Exists(fixture.PackageOutput));
        Assert.False(File.Exists(fixture.DotNetLog));
    }

    private static int Count(string value, string needle)
    {
        int count = 0;
        int offset = 0;
        while ((offset = value.IndexOf(needle, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += needle.Length;
        }
        return count;
    }

    private static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _shimDirectory;

        public Fixture()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), $"modkit-version-{Guid.NewGuid():N}");
            RefProjectPath = Path.Combine(DirectoryPath, "src", "FarmTogether2.GameApi.Ref", "FarmTogether2.GameApi.Ref.csproj");
            StubProjectPath = Path.Combine(DirectoryPath, "src", "Stubs", "MilkstoneUnityExtensions", "MilkstoneUnityExtensions.csproj");
            ChangelogPath = Path.Combine(DirectoryPath, "CHANGELOG.md");
            LockPath = Path.Combine(DirectoryPath, "modkit.lock.json");
            PackageOutput = Path.Combine(DirectoryPath, "artifacts", "packages");
            DotNetLog = Path.Combine(DirectoryPath, "dotnet.log");
            _shimDirectory = Path.Combine(DirectoryPath, "shim");
            Directory.CreateDirectory(Path.GetDirectoryName(RefProjectPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(StubProjectPath)!);
            Directory.CreateDirectory(Path.Combine(DirectoryPath, "scripts"));
            Directory.CreateDirectory(Path.Combine(DirectoryPath, "tools", "FarmTogether2.ModKit.Tool"));
            Directory.CreateDirectory(_shimDirectory);

            File.Copy(VersionScript, Path.Combine(DirectoryPath, "scripts", "Set-ModKitVersion.ps1"));
            File.Copy(PackageScript, Path.Combine(DirectoryPath, "scripts", "Pack-GameApiRef.ps1"));
            File.WriteAllText(
                RefProjectPath,
                "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net8.0</TargetFramework>\n    <Version>1.0.0</Version>\n  </PropertyGroup>\n</Project>\n",
                new UTF8Encoding(false));
            File.WriteAllText(
                StubProjectPath,
                "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <AssemblyVersion>1.0.0.0</AssemblyVersion>\n  </PropertyGroup>\n</Project>\n",
                new UTF8Encoding(false));
            File.WriteAllText(ChangelogPath, "# Changelog\n\n## 1.0.0 - 2026-07-11\n\n- Initial release.\n", new UTF8Encoding(false));
            File.WriteAllText(LockPath, "{\"packageVersion\":\"1.0.0\"}\n", new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(DirectoryPath, "tools", "FarmTogether2.ModKit.Tool", "FarmTogether2.ModKit.Tool.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />\n",
                new UTF8Encoding(false));
            foreach (string assembly in new[]
            {
                "Assembly-CSharp",
                "Il2Cppmscorlib",
                "MilkstoneUnityExtensions",
                "UnityEngine.CoreModule",
                "UnityEngine.IMGUIModule",
                "UnityEngine.InputLegacyModule",
                "UnityEngine.TextRenderingModule"
            })
            {
                string directory = Path.Combine(DirectoryPath, "src", "Stubs", assembly, "bin", "Release", "net6.0");
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, assembly + ".dll"), "fixture", new UTF8Encoding(false));
            }
            WriteDotNetShim();
        }

        public string DirectoryPath { get; }
        public string RefProjectPath { get; }
        public string StubProjectPath { get; }
        public string ChangelogPath { get; }
        public string LockPath { get; }
        public string PackageOutput { get; }
        public string DotNetLog { get; }

        public ProcessResult RunVersion(string version, string? failurePoint = null, IEnumerable<string>? extraArguments = null)
        {
            List<string> arguments =
            [
                "-NoLogo", "-NoProfile", "-File", Path.Combine(DirectoryPath, "scripts", "Set-ModKitVersion.ps1"),
                "-Version", version
            ];
            if (extraArguments is not null)
                arguments.AddRange(extraArguments);
            ProcessStartInfo startInfo = StartInfo("pwsh", arguments);
            if (failurePoint is not null)
                startInfo.Environment["FARMT2_MODKIT_VERSION_FAIL_AT"] = failurePoint;
            return Run(startInfo);
        }

        public ProcessResult RunPack(IEnumerable<string>? extraArguments = null)
        {
            List<string> arguments =
            [
                "-NoLogo", "-NoProfile", "-File", Path.Combine(DirectoryPath, "scripts", "Pack-GameApiRef.ps1"),
                "-OutputDirectory", PackageOutput,
                "-Configuration", "Release",
                "-NoBuild"
            ];
            if (extraArguments is not null)
                arguments.AddRange(extraArguments);
            ProcessStartInfo startInfo = StartInfo("pwsh", arguments);
            startInfo.Environment["PATH"] = _shimDirectory + Path.PathSeparator +
                (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
            startInfo.Environment["FAKE_DOTNET_LOG"] = DotNetLog;
            return Run(startInfo);
        }

        public Dictionary<string, string> SnapshotFiles()
        {
            Dictionary<string, string> result = new(StringComparer.Ordinal);
            foreach (string file in Directory.EnumerateFiles(DirectoryPath, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(DirectoryPath, file).Replace('\\', '/');
                result.Add(relative, Sha256(file));
            }
            return result;
        }

        public void AssertNoVersionTemporaries()
        {
            Assert.Empty(Directory.EnumerateFiles(DirectoryPath, ".*.preparing", SearchOption.AllDirectories));
        }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
                Directory.Delete(DirectoryPath, recursive: true);
        }

        private void WriteDotNetShim()
        {
            string shim = Path.Combine(_shimDirectory, "dotnet-shim.ps1");
            File.WriteAllText(
                shim,
                """
                $ErrorActionPreference = 'Stop'
                [IO.File]::WriteAllLines($env:FAKE_DOTNET_LOG, [string[]]$args)
                $outputIndex = [Array]::IndexOf($args, '--output')
                if ($outputIndex -lt 0 -or $outputIndex + 1 -ge $args.Count) { exit 61 }
                $output = $args[$outputIndex + 1]
                [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output)) | Out-Null
                [IO.File]::WriteAllText($output, 'fixture')
                exit 0
                """.Replace("\r\n", "\n", StringComparison.Ordinal),
                new UTF8Encoding(false));
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(Path.Combine(_shimDirectory, "dotnet.cmd"), $"@pwsh -NoLogo -NoProfile -File \"{shim}\" %*\r\n", new UTF8Encoding(false));
            }
            else
            {
                string wrapper = Path.Combine(_shimDirectory, "dotnet");
                File.WriteAllText(
                    wrapper,
                    $"#!/bin/sh\nexec pwsh -NoLogo -NoProfile -File '{shim.Replace("'", "'\\''", StringComparison.Ordinal)}' \"$@\"\n",
                    new UTF8Encoding(false));
                File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        private static ProcessStartInfo StartInfo(string file, IEnumerable<string> arguments)
        {
            ProcessStartInfo result = new(file)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (string argument in arguments)
                result.ArgumentList.Add(argument);
            return result;
        }

        private static ProcessResult Run(ProcessStartInfo startInfo)
        {
            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start process.");
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return new ProcessResult(process.ExitCode, stdout, stderr);
        }
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr)
    {
        public void AssertSuccess() => Assert.True(ExitCode == 0, $"stdout:\n{Stdout}\nstderr:\n{Stderr}");

        public void AssertFailure(int? expectedExitCode = null)
        {
            Assert.NotEqual(0, ExitCode);
            if (expectedExitCode is not null)
                Assert.Equal(expectedExitCode.Value, ExitCode);
        }
    }
}
