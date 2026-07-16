using System.Diagnostics;
using System.Text;
using Xunit;

namespace FarmTogether2.ModKit.Tests;

[Trait("Category", "LongRunning")]
public sealed class ToolRunnerTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));

    [Fact]
    public void RunnerBuildsClosedHeadSourcesOutsideToolingTreeAndCleansItsOwnedAttempt()
    {
        using Fixture fixture = new();

        fixture.Run().AssertSuccess();

        fixture.AssertNoBuildOutputOrTemporaryAttempt();
    }

    [Fact]
    public void RunnerRejectsExtraModuleInitializerSourceBeforeItCanExecute()
    {
        using Fixture fixture = new();
        string marker = Path.Combine(fixture.DirectoryPath, "injected.txt");
        File.WriteAllText(Path.Combine(fixture.ToolDirectory, "Injected.cs"), """
            using System.Runtime.CompilerServices;
            internal static class Injected
            {
                [ModuleInitializer]
                internal static void Initialize() => File.WriteAllText(Environment.GetEnvironmentVariable("MODKIT_INJECTION_MARKER")!, "injected");
            }
            """.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n", new UTF8Encoding(false));

        fixture.Run(marker).AssertFailure("closed");

        Assert.False(File.Exists(marker));
        fixture.AssertNoBuildOutputOrTemporaryAttempt();
    }

    [Fact]
    public void RunnerRejectsTrackedSourceWhoseBytesDifferFromImmutableHead()
    {
        using Fixture fixture = new();
        File.AppendAllText(Path.Combine(fixture.ToolDirectory, "Program.cs"), "// post-checkout tamper\n", new UTF8Encoding(false));

        fixture.Run().AssertFailure("HEAD");

        fixture.AssertNoBuildOutputOrTemporaryAttempt();
    }

    [Fact]
    public void RunnerRejectsMissingSourceFromClosedAllowlist()
    {
        using Fixture fixture = new();
        File.Delete(Path.Combine(fixture.ToolDirectory, "Program.cs"));

        fixture.Run().AssertFailure("closed");

        fixture.AssertNoBuildOutputOrTemporaryAttempt();
    }

    [Fact]
    public void RunnerRejectsNestedSourceOutsideClosedAllowlist()
    {
        using Fixture fixture = new();
        string nested = Path.Combine(fixture.ToolDirectory, "Nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "Injected.cs"), "internal static class Injected;\n", new UTF8Encoding(false));

        fixture.Run().AssertFailure("closed");

        fixture.AssertNoBuildOutputOrTemporaryAttempt();
    }

    [Fact]
    public void RunnerRejectsCleanDetachedReplacementCommitInjection()
    {
        using Fixture fixture = new();
        string marker = Path.Combine(fixture.DirectoryPath, "replacement-injected.txt");
        fixture.InstallCleanReplacementCommitInjection();

        fixture.Run(marker).AssertFailure("immutable HEAD");

        Assert.False(File.Exists(marker));
        fixture.AssertNoBuildOutputOrTemporaryAttempt();
    }

    [Fact]
    public void RunnerIgnoresAmbientDirectoryBuildTargetsAboveStagedSource()
    {
        using Fixture fixture = new();
        string marker = Path.Combine(fixture.DirectoryPath, "ambient-target-injected.txt");
        fixture.WriteInjectionTarget(Path.Combine(fixture.Temporary, "Directory.Build.targets"), marker);

        fixture.Run().AssertSuccess();

        Assert.False(File.Exists(marker));
        fixture.AssertNoBuildOutputOrTemporaryAttempt();
    }

    [Fact]
    public void RunnerIgnoresAmbientDirectoryBuildResponseAboveStagedSource()
    {
        using Fixture fixture = new();
        string marker = Path.Combine(fixture.DirectoryPath, "ambient-response-injected.txt");
        string target = Path.Combine(fixture.DirectoryPath, "inject.targets");
        fixture.WriteInjectionTarget(target, marker);
        File.WriteAllText(
            Path.Combine(fixture.Temporary, "Directory.Build.rsp"),
            $"\"-p:CustomAfterMicrosoftCommonTargets={target}\"\n",
            new UTF8Encoding(false));

        fixture.Run().AssertSuccess();

        Assert.False(File.Exists(marker));
        fixture.AssertNoBuildOutputOrTemporaryAttempt();
    }

    [Fact]
    public void RunnerIgnoresAmbientMsBuildImportHooksFromEnvironment()
    {
        using Fixture fixture = new();
        string marker = Path.Combine(fixture.DirectoryPath, "ambient-environment-injected.txt");
        string target = Path.Combine(fixture.DirectoryPath, "environment-inject.targets");
        fixture.WriteInjectionTarget(target, marker);

        fixture.Run(ambientImport: target).AssertSuccess();

        Assert.False(File.Exists(marker));
        fixture.AssertNoBuildOutputOrTemporaryAttempt();
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), $"modkit-tool-runner-{Guid.NewGuid():N}");
            Checkout = Path.Combine(DirectoryPath, "tooling");
            ToolDirectory = Path.Combine(Checkout, "tools", "FarmTogether2.ModKit.Tool");
            string scripts = Path.Combine(Checkout, "scripts");
            Temporary = Path.Combine(DirectoryPath, "temporary");
            Directory.CreateDirectory(ToolDirectory);
            Directory.CreateDirectory(scripts);
            Directory.CreateDirectory(Temporary);
            foreach (string source in Directory.EnumerateFiles(Path.Combine(Root, "tools", "FarmTogether2.ModKit.Tool")))
                File.Copy(source, Path.Combine(ToolDirectory, Path.GetFileName(source)));
            foreach (string name in new[] { "Directory.Build.props", "Directory.Packages.props", "NuGet.config", "global.json" })
                File.Copy(Path.Combine(Root, name), Path.Combine(Checkout, name));
            Runner = Path.Combine(scripts, "Invoke-ModKitTool.ps1");
            File.Copy(Path.Combine(Root, "scripts", "Invoke-ModKitTool.ps1"), Runner);
            LockFile = Path.Combine(DirectoryPath, "modkit.lock.json");
            File.WriteAllText(LockFile, """
                {
                  "schemaVersion": 1,
                  "repository": "abmcar/FarmTogether2-ModKit",
                  "workflowCommit": "0123456789abcdef0123456789abcdef01234567",
                  "packageId": "FarmTogether2.GameApi.Ref",
                  "packageVersion": "1.0.0",
                  "releaseTag": "v1.0.0",
                  "assetName": "FarmTogether2.GameApi.Ref.1.0.0.nupkg",
                  "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                }
                """.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n", new UTF8Encoding(false));
            Git("init", "-b", "main").AssertSuccess();
            Git("config", "core.autocrlf", "false").AssertSuccess();
            Git("config", "user.name", "fixture").AssertSuccess();
            Git("config", "user.email", "fixture@users.noreply.github.com").AssertSuccess();
            Git("add", "--", ".").AssertSuccess();
            Git("commit", "-m", "fixture tooling").AssertSuccess();
            Git("checkout", "--detach", "HEAD").AssertSuccess();
            ProcessResult head = Git("rev-parse", "HEAD");
            head.AssertSuccess();
            TrustedHead = head.Stdout.Trim();
        }

        public string DirectoryPath { get; }
        public string Checkout { get; }
        public string ToolDirectory { get; }
        public string Temporary { get; }
        public string Runner { get; }
        public string LockFile { get; }
        public string TrustedHead { get; }

        public void WriteInjectionTarget(string path, string marker)
        {
            string escapedMarker = System.Security.SecurityElement.Escape(marker) ?? throw new InvalidOperationException("Could not XML-escape marker path.");
            File.WriteAllText(path, $$"""
                <Project>
                  <Target Name="AmbientInjection" BeforeTargets="CoreCompile">
                    <WriteLinesToFile File="{{escapedMarker}}" Lines="injected" Overwrite="true" />
                  </Target>
                </Project>
                """.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n", new UTF8Encoding(false));
        }

        public void InstallCleanReplacementCommitInjection()
        {
            string programPath = Path.Combine(ToolDirectory, "Program.cs");
            string program = File.ReadAllText(programPath);
            const string mainPrefix = "    public static int Main(string[] args)\n    {\n";
            string injectedPrefix = mainPrefix +
                "        File.WriteAllText(Environment.GetEnvironmentVariable(\"MODKIT_INJECTION_MARKER\")!, \"injected\");\n";
            Assert.Contains(mainPrefix, program, StringComparison.Ordinal);
            File.WriteAllText(programPath, program.Replace(mainPrefix, injectedPrefix, StringComparison.Ordinal), new UTF8Encoding(false));
            Git("add", "--", "tools/FarmTogether2.ModKit.Tool/Program.cs").AssertSuccess();
            Git("commit", "-m", "replacement injection").AssertSuccess();
            ProcessResult replacement = Git("rev-parse", "HEAD");
            replacement.AssertSuccess();
            string replacementHead = replacement.Stdout.Trim();
            Git("checkout", "--detach", TrustedHead).AssertSuccess();
            Git("replace", TrustedHead, replacementHead).AssertSuccess();
            Git("reset", "--hard", "HEAD").AssertSuccess();

            ProcessResult head = Git("rev-parse", "HEAD");
            head.AssertSuccess();
            Assert.Equal(TrustedHead, head.Stdout.Trim());
            ProcessResult branch = Git("rev-parse", "--abbrev-ref", "HEAD");
            branch.AssertSuccess();
            Assert.Equal("HEAD", branch.Stdout.Trim());
            ProcessResult status = Git("status", "--porcelain", "--untracked-files=all");
            status.AssertSuccess();
            Assert.Empty(status.Stdout);
        }

        public ProcessResult Run(string? injectionMarker = null, string? ambientImport = null)
        {
            ProcessStartInfo startInfo = CreateStartInfo(
                "pwsh",
                ["-NoLogo", "-NoProfile", "-File", Runner, "lock", "verify", "--file", LockFile]);
            startInfo.Environment["PATH"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet") + Path.PathSeparator +
                (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
            startInfo.Environment["DOTNET_ROOT"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet");
            startInfo.Environment["TMPDIR"] = Temporary;
            startInfo.Environment["TMP"] = Temporary;
            startInfo.Environment["TEMP"] = Temporary;
            startInfo.Environment["MODKIT_INJECTION_MARKER"] = injectionMarker;
            if (ambientImport is not null)
            {
                foreach (string name in new[]
                {
                    "CustomBeforeDirectoryBuildProps",
                    "CustomAfterDirectoryBuildProps",
                    "CustomBeforeDirectoryBuildTargets",
                    "CustomAfterDirectoryBuildTargets",
                    "CustomBeforeMicrosoftCommonProps",
                    "CustomAfterMicrosoftCommonProps",
                    "CustomBeforeMicrosoftCommonTargets",
                    "CustomAfterMicrosoftCommonTargets",
                    "CustomBeforeMicrosoftCommonCrossTargetingTargets",
                    "CustomAfterMicrosoftCommonCrossTargetingTargets",
                    "CustomBeforeMicrosoftCSharpTargets",
                    "CustomAfterMicrosoftCSharpTargets"
                })
                {
                    startInfo.Environment[name] = ambientImport;
                }
            }
            return RunProcess(startInfo);
        }

        public void AssertNoBuildOutputOrTemporaryAttempt()
        {
            Assert.False(Directory.Exists(Path.Combine(ToolDirectory, "bin")));
            Assert.False(Directory.Exists(Path.Combine(ToolDirectory, "obj")));
            Assert.DoesNotContain(Directory.EnumerateDirectories(Temporary),
                path => Path.GetFileName(path).StartsWith("farmtogether2-modkit-tool-", StringComparison.Ordinal));
        }

        private ProcessResult Git(params string[] arguments) => RunProcess(
            CreateStartInfo("git", new[] { "-C", Checkout }.Concat(arguments)));

        public void Dispose()
        {
            TestFileSystem.DeleteDirectoryTree(DirectoryPath);
        }
    }

    private static ProcessStartInfo CreateStartInfo(string fileName, IEnumerable<string> arguments)
    {
        ProcessStartInfo startInfo = new(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    private static ProcessResult RunProcess(ProcessStartInfo startInfo)
    {
        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {startInfo.FileName}.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, stdout, stderr);
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
