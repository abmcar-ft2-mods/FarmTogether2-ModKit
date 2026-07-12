using System.Diagnostics;
using System.Text;
using Xunit;

namespace FarmTogether2.ModKit.Tests;

public sealed class CallerWorkflowContractTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private static readonly string Generator = Path.Combine(Root, "scripts", "New-ModRepositoryFiles.ps1");
    private static readonly string Contract = Path.Combine(Root, "tests", "WorkflowContract.Tests", "Test-CallerWorkflows.ps1");

    [Fact]
    public void GeneratedCallerWorkflowsSatisfyContractAndMovableRefFails()
    {
        using Fixture fixture = new();
        fixture.Run(Generator, ["-RepositoryRoot", fixture.Repository, "-IncludeCallerWorkflows"]).AssertSuccess();
        fixture.Run(Contract, ["-RepositoryRoot", fixture.Repository]).AssertSuccess();

        string ciPath = Path.Combine(fixture.Repository, ".github", "workflows", "ci.yml");
        File.WriteAllText(ciPath, File.ReadAllText(ciPath).Replace("@" + Commit, "@main", StringComparison.Ordinal), new UTF8Encoding(false));
        fixture.Run(Contract, ["-RepositoryRoot", fixture.Repository]).AssertFailure("pinned");
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), $"modkit-caller-contract-{Guid.NewGuid():N}");
            Repository = Path.Combine(DirectoryPath, "repository");
            Directory.CreateDirectory(Path.Combine(Repository, "src"));
            Directory.CreateDirectory(Path.Combine(Repository, "packaging"));
            File.WriteAllText(Path.Combine(Repository, "mod.json"), """
                {
                  "schemaVersion": 1,
                  "id": "com.abmcar.farmtogether2.fixture",
                  "displayName": "Fixture",
                  "assemblyName": "Fixture",
                  "version": "1.0.0",
                  "project": "src/Fixture.csproj",
                  "testProjects": [],
                  "guardScripts": [],
                  "installReadme": "packaging/README_安装说明.txt",
                  "supportedSteamBuild": "24069957"
                }
                """.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(Repository, "modkit.lock.json"), $$"""
                {
                  "schemaVersion": 1,
                  "repository": "abmcar/FarmTogether2-ModKit",
                  "workflowCommit": "{{Commit}}",
                  "packageId": "FarmTogether2.GameApi.Ref",
                  "packageVersion": "1.0.0",
                  "releaseTag": "v1.0.0",
                  "assetName": "FarmTogether2.GameApi.Ref.1.0.0.nupkg",
                  "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                }
                """.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n", new UTF8Encoding(false));
        }

        public string DirectoryPath { get; }
        public string Repository { get; }

        public ProcessResult Run(string script, IEnumerable<string> arguments)
        {
            ProcessStartInfo startInfo = new("pwsh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-File", script }.Concat(arguments))
                startInfo.ArgumentList.Add(argument);
            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start pwsh.");
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
    }
}
