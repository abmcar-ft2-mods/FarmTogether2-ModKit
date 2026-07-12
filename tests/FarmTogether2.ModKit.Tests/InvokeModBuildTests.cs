using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace FarmTogether2.ModKit.Tests;

public sealed class InvokeModBuildTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private static readonly string Script = Path.Combine(Root, "scripts", "Invoke-ModBuild.ps1");

    [Fact]
    public void LocalInteropDeployIsPassedOnlyToPluginProject()
    {
        using Fixture fixture = new();
        fixture.Run("LocalInterop", gameDir: fixture.GameDirectory, deploy: true).AssertSuccess();
        string[] commands = File.ReadAllLines(fixture.DotNetLog);
        string[] testProjectCommands = commands.Where(line => line.Contains("Fixture.Tests.csproj", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(testProjectCommands);
        Assert.All(testProjectCommands, line => Assert.Contains("DeployToGame=false", line, StringComparison.Ordinal));
        Assert.Contains(commands, line => line.Contains("build", StringComparison.Ordinal) && line.Contains("Fixture.csproj", StringComparison.Ordinal) && line.Contains("DeployToGame=false", StringComparison.Ordinal));
        Assert.DoesNotContain(commands, line => line.Contains("DeployToGame=true", StringComparison.Ordinal));
        Assert.DoesNotContain(Directory.EnumerateFiles(fixture.GameDirectory, "*", SearchOption.AllDirectories), path => path.EndsWith("Fixture.Tests.dll", StringComparison.Ordinal));
        Assert.Equal("plugin\n", File.ReadAllText(Path.Combine(fixture.GameDirectory, "BepInEx", "plugins", "Fixture", "Fixture.dll")).Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Equal("guard\n", File.ReadAllText(fixture.GuardLog).Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void HostedRejectsLocalPathsBeforeAnyBuild()
    {
        using Fixture fixture = new();
        fixture.Run("Hosted", interopDir: fixture.InteropDirectory).AssertFailure("Hosted");
        Assert.False(File.Exists(fixture.DotNetLog));
        Assert.False(File.Exists(fixture.GuardLog));
    }

    [Fact]
    public void LocalInteropRequiresExactlyOneSourceAndExplicitGameForDeploy()
    {
        using Fixture fixture = new();
        fixture.Run("LocalInterop").AssertFailure("exactly one");
        fixture.Run("LocalInterop", interopDir: fixture.InteropDirectory, gameDir: fixture.GameDirectory).AssertFailure("exactly one");
        fixture.Run("LocalInterop", interopDir: fixture.InteropDirectory, deploy: true).AssertFailure("GameDir");
        Assert.False(File.Exists(fixture.DotNetLog));
    }

    [Fact]
    public void FailedBuildStopsBeforeTestsAndGuards()
    {
        using Fixture fixture = new();
        fixture.Run("Hosted", failWhenArgumentsContain: "build").AssertFailure("build");
        string[] commands = File.ReadAllLines(fixture.DotNetLog);
        Assert.Contains(commands, line => line.StartsWith("restore ", StringComparison.Ordinal));
        Assert.Contains(commands, line => line.StartsWith("build ", StringComparison.Ordinal));
        Assert.DoesNotContain(commands, line => line.StartsWith("test ", StringComparison.Ordinal));
        Assert.False(File.Exists(fixture.GuardLog));
    }

    [Fact]
    public void WrongToolingHeadFailsBeforeRestore()
    {
        using Fixture fixture = new();
        fixture.Run("Hosted", fakeHead: new string('b', 40)).AssertFailure("HEAD");
        Assert.False(File.Exists(fixture.DotNetLog));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _shimDirectory;

        public Fixture()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), $"modkit-invoke-build-{Guid.NewGuid():N}");
            Repository = Path.Combine(DirectoryPath, "repository");
            _shimDirectory = Path.Combine(DirectoryPath, "shim");
            GameDirectory = Path.Combine(DirectoryPath, "game");
            InteropDirectory = Path.Combine(GameDirectory, "BepInEx", "interop");
            DotNetLog = Path.Combine(DirectoryPath, "dotnet.log");
            GuardLog = Path.Combine(DirectoryPath, "guard.log");
            BuiltPlugin = Path.Combine(Repository, "src", "bin", "Fixture.dll");
            Directory.CreateDirectory(Path.Combine(Repository, "src"));
            Directory.CreateDirectory(Path.Combine(Repository, "tests"));
            Directory.CreateDirectory(Path.Combine(Repository, ".modkit", "tooling", ".git"));
            string packageDirectory = Path.Combine(Repository, ".modkit", "packages");
            Directory.CreateDirectory(packageDirectory);
            Directory.CreateDirectory(InteropDirectory);
            Directory.CreateDirectory(_shimDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(BuiltPlugin)!);
            File.WriteAllText(BuiltPlugin, "plugin\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(Repository, "src", "Fixture.csproj"), "<Project />\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(Repository, "tests", "Fixture.Tests.csproj"), "<Project />\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(Repository, "tests", "Guard.ps1"), "Add-Content -LiteralPath $env:FAKE_GUARD_LOG -Value guard\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(Repository, "mod.json"), """
                {
                  "schemaVersion": 1,
                  "id": "com.abmcar.farmtogether2.fixture",
                  "displayName": "Fixture",
                  "assemblyName": "Fixture",
                  "version": "1.0.0",
                  "project": "src/Fixture.csproj",
                  "testProjects": ["tests/Fixture.Tests.csproj"],
                  "guardScripts": ["tests/Guard.ps1"],
                  "installReadme": "packaging/README_安装说明.txt",
                  "supportedSteamBuild": "24069957"
                }
                """.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n", new UTF8Encoding(false));
            string package = Path.Combine(packageDirectory, "FarmTogether2.GameApi.Ref.1.0.0.nupkg");
            File.WriteAllBytes(package, [1, 2, 3, 4]);
            string packageHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(package))).ToLowerInvariant();
            File.WriteAllText(Path.Combine(Repository, "modkit.lock.json"), $$"""
                {
                  "schemaVersion": 1,
                  "repository": "abmcar/FarmTogether2-ModKit",
                  "workflowCommit": "{{Commit}}",
                  "packageId": "FarmTogether2.GameApi.Ref",
                  "packageVersion": "1.0.0",
                  "releaseTag": "v1.0.0",
                  "assetName": "FarmTogether2.GameApi.Ref.1.0.0.nupkg",
                  "sha256": "{{packageHash}}"
                }
                """.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n", new UTF8Encoding(false));
            WriteShims();
        }

        public string DirectoryPath { get; }
        public string Repository { get; }
        public string GameDirectory { get; }
        public string InteropDirectory { get; }
        public string DotNetLog { get; }
        public string GuardLog { get; }
        public string BuiltPlugin { get; }

        public ProcessResult Run(
            string mode,
            string? interopDir = null,
            string? gameDir = null,
            bool deploy = false,
            string? failWhenArgumentsContain = null,
            string fakeHead = Commit)
        {
            List<string> args =
            [
                "-NoLogo", "-NoProfile", "-File", Script,
                "-RepositoryRoot", Repository,
                "-GameApiMode", mode,
                "-Configuration", "Release"
            ];
            if (interopDir is not null)
                args.AddRange(["-InteropDir", interopDir]);
            if (gameDir is not null)
                args.AddRange(["-GameDir", gameDir]);
            if (deploy)
                args.Add("-DeployToGame");
            ProcessStartInfo startInfo = new("pwsh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (string argument in args)
                startInfo.ArgumentList.Add(argument);
            startInfo.Environment["PATH"] = _shimDirectory + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
            startInfo.Environment["FAKE_DOTNET_LOG"] = DotNetLog;
            startInfo.Environment["FAKE_DOTNET_FAIL_CONTAINS"] = failWhenArgumentsContain;
            startInfo.Environment["FAKE_GIT_HEAD"] = fakeHead;
            startInfo.Environment["FAKE_GUARD_LOG"] = GuardLog;
            startInfo.Environment["FAKE_PLUGIN_DLL"] = BuiltPlugin;
            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start pwsh.");
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return new ProcessResult(process.ExitCode, stdout, stderr);
        }

        private void WriteShims()
        {
            string dotnetScript = Path.Combine(_shimDirectory, "dotnet-shim.ps1");
            File.WriteAllText(dotnetScript, """
                $line = $args -join ' '
                Add-Content -LiteralPath $env:FAKE_DOTNET_LOG -Value $line
                if ($env:FAKE_DOTNET_FAIL_CONTAINS -and $args[0] -ceq $env:FAKE_DOTNET_FAIL_CONTAINS) { exit 71 }
                if ($args[0] -ceq 'msbuild') { [pscustomobject]@{ Properties = [pscustomobject]@{ TargetPath = $env:FAKE_PLUGIN_DLL } } | ConvertTo-Json -Compress; exit 0 }
                exit 0
                """.Replace("\r\n", "\n", StringComparison.Ordinal), new UTF8Encoding(false));
            string gitScript = Path.Combine(_shimDirectory, "git-shim.ps1");
            File.WriteAllText(gitScript, """
                if ($args -contains 'rev-parse' -and $args -contains '--abbrev-ref') { 'HEAD'; exit 0 }
                if ($args -contains 'rev-parse') { $env:FAKE_GIT_HEAD; exit 0 }
                if ($args -contains 'status') { exit 0 }
                exit 72
                """.Replace("\r\n", "\n", StringComparison.Ordinal), new UTF8Encoding(false));
            WriteWrapper("dotnet", dotnetScript);
            WriteWrapper("git", gitScript);
        }

        private void WriteWrapper(string name, string scriptPath)
        {
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(Path.Combine(_shimDirectory, name + ".cmd"), $"@pwsh -NoLogo -NoProfile -File \"{scriptPath}\" %*\r\n", new UTF8Encoding(false));
            }
            else
            {
                string wrapper = Path.Combine(_shimDirectory, name);
                File.WriteAllText(wrapper, $"#!/bin/sh\nexec pwsh -NoLogo -NoProfile -File '{scriptPath.Replace("'", "'\\''", StringComparison.Ordinal)}' \"$@\"\n", new UTF8Encoding(false));
                File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
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
    }
}
