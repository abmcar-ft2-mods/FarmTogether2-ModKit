using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace FarmTogether2.ModKit.Tests;

[Trait("Category", "LongRunning")]
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
        Assert.All(commands, line => Assert.DoesNotContain("-p:InteropDir=", line, StringComparison.Ordinal));
        string[] testProjectCommands = commands.Where(line => line.Contains("Fixture.Tests.csproj", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(testProjectCommands);
        Assert.All(testProjectCommands, line => Assert.Contains("DeployToGame=false", line, StringComparison.Ordinal));
        Assert.Contains(commands, line => line.Contains("build", StringComparison.Ordinal) && line.Contains("Fixture.csproj", StringComparison.Ordinal) && line.Contains("DeployToGame=false", StringComparison.Ordinal));
        Assert.DoesNotContain(commands, line => line.Contains("DeployToGame=true", StringComparison.Ordinal));
        Assert.DoesNotContain(Directory.EnumerateFiles(fixture.GameDirectory, "*", SearchOption.AllDirectories), path => path.EndsWith("Fixture.Tests.dll", StringComparison.Ordinal));
        Assert.Equal("plugin\n", File.ReadAllText(Path.Combine(fixture.GameDirectory, "BepInEx", "plugins", "Fixture", "Fixture.dll")).Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Equal("symbols\n", File.ReadAllText(Path.Combine(fixture.GameDirectory, "BepInEx", "plugins", "Fixture", "Fixture.pdb")).Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Equal("guard\n", File.ReadAllText(fixture.GuardLog).Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void LocalInteropDeployRequiresTheBuiltPluginPdb()
    {
        using Fixture fixture = new();
        File.Delete(fixture.BuiltPluginPdb);

        fixture.Run("LocalInterop", gameDir: fixture.GameDirectory, deploy: true).AssertFailure("PDB");

        string pluginDirectory = Path.Combine(fixture.GameDirectory, "BepInEx", "plugins", "Fixture");
        Assert.False(Directory.Exists(pluginDirectory));
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
    public void MultipleTestProjectsAndGuardsRemainDistinct()
    {
        using Fixture fixture = new();
        fixture.AddSecondTestProjectAndGuard();

        fixture.Run("Hosted").AssertSuccess();

        string[] commands = File.ReadAllLines(fixture.DotNetLog);
        string[] restoreCommands = commands.Where(line => line.StartsWith("restore ", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(restoreCommands);
        Assert.All(restoreCommands, line =>
        {
            Assert.Contains($"--packages {fixture.PackageCache}", line, StringComparison.Ordinal);
            Assert.Contains("--no-cache", line, StringComparison.Ordinal);
        });
        Assert.Equal(2, commands.Count(line => line.StartsWith("test ", StringComparison.Ordinal)));
        Assert.Contains(commands, line => line.Contains("Fixture.Tests.csproj", StringComparison.Ordinal));
        Assert.Contains(commands, line => line.Contains("Fixture.Second.Tests.csproj", StringComparison.Ordinal));
        Assert.Equal("guard\nguard-two\n", File.ReadAllText(fixture.GuardLog).Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void WrongToolingHeadFailsBeforeRestore()
    {
        using Fixture fixture = new();
        fixture.Run("Hosted", fakeHead: new string('b', 40)).AssertFailure("HEAD");
        Assert.False(File.Exists(fixture.DotNetLog));
    }

    [Fact]
    public void ReplacementCommitCannotSwapRunnerBeforeRawGitValidation()
    {
        using Fixture fixture = new();
        fixture.InstallCleanReplacementRunnerInjection();

        fixture.Run("Hosted", useRealGit: true).AssertFailure("dirty");

        Assert.False(File.Exists(fixture.ReplacementMarker));
        Assert.False(File.Exists(fixture.DotNetLog));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" Fixture ")]
    public void TrustedConfigVerificationRejectsBlankOrUntrimmedDisplayNameBeforeBuild(string displayName)
    {
        using Fixture fixture = new();
        fixture.ReplaceConfigValue("\"displayName\": \"Fixture\"", $"\"displayName\": \"{displayName}\"");

        fixture.Run("Hosted").AssertFailure("displayName");

        fixture.AssertTrustedToolRunsAfterGitValidation();
        Assert.False(File.Exists(fixture.DotNetLog));
        Assert.False(File.Exists(fixture.GuardLog));
    }

    [Fact]
    public void TrustedConfigVerificationRejectsUnsafeInstallReadmeBeforeBuild()
    {
        using Fixture fixture = new();
        fixture.ReplaceConfigValue(
            "\"installReadme\": \"packaging/README_安装说明.txt\"",
            "\"installReadme\": \"../outside.txt\"");

        fixture.Run("Hosted").AssertFailure("installReadme");

        fixture.AssertTrustedToolRunsAfterGitValidation();
        Assert.False(File.Exists(fixture.DotNetLog));
        Assert.False(File.Exists(fixture.GuardLog));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _shimDirectory;
        private readonly string _realGitShimDirectory;

        public Fixture()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), $"modkit-invoke-build-{Guid.NewGuid():N}");
            Repository = Path.Combine(DirectoryPath, "repository");
            _shimDirectory = Path.Combine(DirectoryPath, "shim");
            _realGitShimDirectory = Path.Combine(DirectoryPath, "real-git-shim");
            GameDirectory = Path.Combine(DirectoryPath, "game");
            InteropDirectory = Path.Combine(GameDirectory, "BepInEx", "interop");
            DotNetLog = Path.Combine(DirectoryPath, "dotnet.log");
            GuardLog = Path.Combine(DirectoryPath, "guard.log");
            SequenceLog = Path.Combine(DirectoryPath, "sequence.log");
            ReplacementMarker = Path.Combine(DirectoryPath, "replacement-runner-injected.txt");
            BuiltPlugin = Path.Combine(Repository, "src", "bin", "Fixture.dll");
            BuiltPluginPdb = Path.Combine(Repository, "src", "bin", "Fixture.pdb");
            Directory.CreateDirectory(Path.Combine(Repository, "src"));
            Directory.CreateDirectory(Path.Combine(Repository, "tests"));
            Directory.CreateDirectory(Path.Combine(Repository, ".modkit", "tooling", ".git"));
            string packageDirectory = Path.Combine(Repository, ".modkit", "packages");
            Directory.CreateDirectory(packageDirectory);
            Directory.CreateDirectory(InteropDirectory);
            Directory.CreateDirectory(_shimDirectory);
            Directory.CreateDirectory(_realGitShimDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(BuiltPlugin)!);
            File.WriteAllText(BuiltPlugin, "plugin\n", new UTF8Encoding(false));
            File.WriteAllText(BuiltPluginPdb, "symbols\n", new UTF8Encoding(false));
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
            PackageCache = Path.Combine(Repository, ".modkit", "nuget-packages", packageHash);
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
        public string SequenceLog { get; }
        public string ReplacementMarker { get; }
        public string BuiltPlugin { get; }
        public string BuiltPluginPdb { get; }
        public string PackageCache { get; }

        public void ReplaceConfigValue(string oldValue, string newValue)
        {
            string path = Path.Combine(Repository, "mod.json");
            string original = File.ReadAllText(path);
            string updated = original.Replace(oldValue, newValue, StringComparison.Ordinal);
            Assert.NotEqual(original, updated);
            File.WriteAllText(path, updated, new UTF8Encoding(false));
        }

        public void AddSecondTestProjectAndGuard()
        {
            File.WriteAllText(Path.Combine(Repository, "tests", "Fixture.Second.Tests.csproj"), "<Project />\n", new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(Repository, "tests", "Guard.Second.ps1"),
                "Add-Content -LiteralPath $env:FAKE_GUARD_LOG -Value guard-two\n",
                new UTF8Encoding(false));
            ReplaceConfigValue(
                "\"testProjects\": [\"tests/Fixture.Tests.csproj\"]",
                "\"testProjects\": [\"tests/Fixture.Tests.csproj\", \"tests/Fixture.Second.Tests.csproj\"]");
            ReplaceConfigValue(
                "\"guardScripts\": [\"tests/Guard.ps1\"]",
                "\"guardScripts\": [\"tests/Guard.ps1\", \"tests/Guard.Second.ps1\"]");
        }

        public void AssertTrustedToolRunsAfterGitValidation()
        {
            string[] sequence = File.ReadAllLines(SequenceLog);
            int head = Array.FindIndex(sequence, line => line.Contains("rev-parse HEAD", StringComparison.Ordinal) && !line.Contains("--abbrev-ref", StringComparison.Ordinal));
            int detached = Array.FindIndex(sequence, line => line.Contains("rev-parse --abbrev-ref HEAD", StringComparison.Ordinal));
            int clean = Array.FindIndex(sequence, line => line.Contains("status --porcelain --untracked-files=all", StringComparison.Ordinal));
            int tool = Array.FindIndex(sequence, line => line.StartsWith("tool mod-config verify --file ", StringComparison.Ordinal));
            Assert.True(head >= 0 && detached > head && clean > detached && tool > clean, string.Join("\n", sequence));
        }

        public void InstallCleanReplacementRunnerInjection()
        {
            string tooling = Path.Combine(Repository, ".modkit", "tooling");
            string runner = Path.Combine(tooling, "scripts", "Invoke-ModKitTool.ps1");
            Directory.Delete(Path.Combine(tooling, ".git"), recursive: true);
            Git(tooling, "init", "-b", "main").AssertSuccess();
            Git(tooling, "config", "core.autocrlf", "false").AssertSuccess();
            Git(tooling, "config", "user.name", "fixture").AssertSuccess();
            Git(tooling, "config", "user.email", "fixture@users.noreply.github.com").AssertSuccess();
            Git(tooling, "add", "--", "scripts/Invoke-ModKitTool.ps1").AssertSuccess();
            Git(tooling, "commit", "-m", "trusted runner").AssertSuccess();
            ProcessResult trustedResult = Git(tooling, "rev-parse", "HEAD");
            trustedResult.AssertSuccess();
            string trusted = trustedResult.Stdout.Trim();

            File.WriteAllText(
                runner,
                "Add-Content -LiteralPath $env:REPLACEMENT_MARKER -Value injected\n" + File.ReadAllText(runner),
                new UTF8Encoding(false));
            Git(tooling, "add", "--", "scripts/Invoke-ModKitTool.ps1").AssertSuccess();
            Git(tooling, "commit", "-m", "replacement runner injection").AssertSuccess();
            ProcessResult replacementResult = Git(tooling, "rev-parse", "HEAD");
            replacementResult.AssertSuccess();
            string replacement = replacementResult.Stdout.Trim();
            Git(tooling, "checkout", "--detach", trusted).AssertSuccess();
            Git(tooling, "replace", trusted, replacement).AssertSuccess();
            Git(tooling, "reset", "--hard", "HEAD").AssertSuccess();

            ProcessResult visibleStatus = Git(tooling, "status", "--porcelain", "--untracked-files=all");
            visibleStatus.AssertSuccess();
            Assert.Empty(visibleStatus.Stdout);
            ProcessResult rawStatus = Git(tooling, "--no-replace-objects", "status", "--porcelain", "--untracked-files=all");
            rawStatus.AssertSuccess();
            Assert.Contains("scripts/Invoke-ModKitTool.ps1", rawStatus.Stdout, StringComparison.Ordinal);

            string lockPath = Path.Combine(Repository, "modkit.lock.json");
            string lockText = File.ReadAllText(lockPath);
            Assert.Contains(Commit, lockText, StringComparison.Ordinal);
            File.WriteAllText(lockPath, lockText.Replace(Commit, trusted, StringComparison.Ordinal), new UTF8Encoding(false));
        }

        public ProcessResult Run(
            string mode,
            string? interopDir = null,
            string? gameDir = null,
            bool deploy = false,
            string? failWhenArgumentsContain = null,
            string fakeHead = Commit,
            bool useRealGit = false)
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
            startInfo.Environment["PATH"] = (useRealGit ? _realGitShimDirectory : _shimDirectory) + Path.PathSeparator +
                (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
            startInfo.Environment["FAKE_DOTNET_LOG"] = DotNetLog;
            startInfo.Environment["FAKE_DOTNET_FAIL_CONTAINS"] = failWhenArgumentsContain;
            startInfo.Environment["FAKE_GIT_HEAD"] = fakeHead;
            startInfo.Environment["FAKE_GUARD_LOG"] = GuardLog;
            startInfo.Environment["FAKE_PLUGIN_DLL"] = BuiltPlugin;
            startInfo.Environment["FAKE_SEQUENCE_LOG"] = SequenceLog;
            startInfo.Environment["REPLACEMENT_MARKER"] = ReplacementMarker;
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
                Add-Content -LiteralPath $env:FAKE_SEQUENCE_LOG -Value ("git " + ($args -join ' '))
                if ($args -contains 'rev-parse' -and $args -contains '--abbrev-ref') { 'HEAD'; exit 0 }
                if ($args -contains 'rev-parse') { $env:FAKE_GIT_HEAD; exit 0 }
                if ($args -contains 'status') { exit 0 }
                exit 72
                """.Replace("\r\n", "\n", StringComparison.Ordinal), new UTF8Encoding(false));
            WriteWrapper("dotnet", dotnetScript);
            WriteWrapper("dotnet", dotnetScript, _realGitShimDirectory);
            WriteWrapper("git", gitScript);
            string runnerDirectory = Path.Combine(Repository, ".modkit", "tooling", "scripts");
            Directory.CreateDirectory(runnerDirectory);
            File.WriteAllText(Path.Combine(runnerDirectory, "Invoke-ModKitTool.ps1"), """
                #requires -Version 7.0
                param([Parameter(ValueFromRemainingArguments)][string[]]$ToolArguments)
                Add-Content -LiteralPath $env:FAKE_SEQUENCE_LOG -Value ("tool " + ($ToolArguments -join ' '))
                if ($ToolArguments.Count -ne 4 -or $ToolArguments[0] -cne 'mod-config' -or $ToolArguments[1] -cne 'verify' -or $ToolArguments[2] -cne '--file') { throw 'Unexpected trusted tool arguments.' }
                $config = Get-Content -LiteralPath $ToolArguments[3] -Raw | ConvertFrom-Json
                if ([string]::IsNullOrWhiteSpace($config.displayName) -or $config.displayName -cne $config.displayName.Trim()) { throw 'Mod displayName must be non-empty canonical text.' }
                $readme = [string]$config.installReadme
                if ([string]::IsNullOrWhiteSpace($readme) -or $readme -cne $readme.Trim() -or
                    $readme.Contains('\') -or $readme.Contains(':') -or $readme.StartsWith('/') -or
                    @($readme.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -ne 0 -or
                    -not $readme.EndsWith('.txt', [StringComparison]::OrdinalIgnoreCase)) { throw 'Mod installReadme contains an unsafe or noncanonical relative path.' }
                """.Replace("\r\n", "\n", StringComparison.Ordinal), new UTF8Encoding(false));
        }

        private void WriteWrapper(string name, string scriptPath, string? directory = null)
        {
            directory ??= _shimDirectory;
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(Path.Combine(directory, name + ".cmd"), $"@pwsh -NoLogo -NoProfile -File \"{scriptPath}\" %*\r\n", new UTF8Encoding(false));
            }
            else
            {
                string wrapper = Path.Combine(directory, name);
                File.WriteAllText(wrapper, $"#!/bin/sh\nexec pwsh -NoLogo -NoProfile -File '{scriptPath.Replace("'", "'\\''", StringComparison.Ordinal)}' \"$@\"\n", new UTF8Encoding(false));
                File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        private static ProcessResult Git(string repository, params string[] arguments)
        {
            ProcessStartInfo startInfo = new("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("-C");
            startInfo.ArgumentList.Add(repository);
            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);
            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start git.");
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return new ProcessResult(process.ExitCode, stdout, stderr);
        }

        public void Dispose()
        {
            TestFileSystem.DeleteDirectoryTree(DirectoryPath);
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
