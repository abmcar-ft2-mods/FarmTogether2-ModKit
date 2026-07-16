using System.Diagnostics;
using System.Text;
using Xunit;

namespace FarmTogether2.ModKit.Tests;

[Trait("Category", "LongRunning")]
public sealed class RepositoryAuditTests
{
    private const string Pin = "0123456789abcdef0123456789abcdef01234567";
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private static readonly string Script = Path.Combine(Root, "scripts", "Test-Repository.ps1");

    public static TheoryData<string, string> PullRequestTargetDocuments => new()
    {
        { "plain scalar", "on: pull_request_target\njobs: {}\n" },
        { "quoted scalar", "\"on\": \"pull_request_target\"\njobs: {}\n" },
        { "flow sequence", "on: [push, \"pull_request_target\"]\njobs: {}\n" },
        { "block sequence", "on:\n  - push\n  - pull_request_target\njobs: {}\n" },
        { "block mapping", "on:\n  push:\n  \"pull_request_target\":\njobs: {}\n" },
        { "flow mapping", "on: { push: null, \"pull_request_target\": null }\njobs: {}\n" }
    };

    public static TheoryData<string, string> UnpinnedUsesDocuments => new()
    {
        {
            "plain key",
            "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - uses: actions/checkout@main\n"
        },
        {
            "quoted key and value",
            "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - \"uses\": \"actions/checkout@main\"\n"
        },
        {
            "flow mapping",
            "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - { \"uses\": \"actions/checkout@main\" }\n"
        }
    };

    public static TheoryData<string, string> InheritedSecretsDocuments => new()
    {
        {
            "plain key",
            $"on: push\njobs:\n  call:\n    uses: owner/repository/.github/workflows/reuse.yml@{Pin}\n    secrets: inherit\n"
        },
        {
            "quoted key and value",
            $"on: push\njobs:\n  call:\n    uses: owner/repository/.github/workflows/reuse.yml@{Pin}\n    \"secrets\": \"inherit\"\n"
        },
        {
            "flow mapping",
            $"on: push\njobs: {{ call: {{ uses: owner/repository/.github/workflows/reuse.yml@{Pin}, \"secrets\": \"inherit\" }} }}\n"
        },
        {
            "folded block scalar",
            $"on: push\njobs:\n  call:\n    uses: owner/repository/.github/workflows/reuse.yml@{Pin}\n    secrets: >-\n      inherit\n"
        }
    };

    [Fact]
    public void SafeRepositoryPassesWithCountOnlyOutput()
    {
        using Fixture fixture = new();
        ProcessResult result = fixture.Audit(nativeErrorPreference: true);
        result.AssertSuccess();
        Assert.Contains("trackedPaths=", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("historyBlobs=", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.DirectoryPath, result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void SafeQuotedAndFlowWorkflowPasses()
    {
        using Fixture fixture = new();
        fixture.CommitFile(
            ".github/workflows/ci.yml",
            Encoding.UTF8.GetBytes("""
                "on": [push, "pull_request"]
                jobs:
                  build:
                    runs-on: ubuntu-latest
                    steps:
                      - "uses": "actions/checkout@__PIN__"
                      - run: |
                          echo "uses: actions/checkout@main"
                  call:
                    uses: owner/repository/.github/workflows/reuse.yml@__PIN__
                    secrets:
                      token: ${{ secrets.TOKEN }}
                """.Replace("__PIN__", Pin, StringComparison.Ordinal).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n"),
            "safe workflow");

        fixture.Audit().AssertSuccess();
    }

    [Fact]
    public void DependabotAutoMergeAllowsOnlyTheReviewedWorkflowShape()
    {
        using Fixture fixture = new();
        string relativePath = ".github/workflows/dependabot-auto-merge.yml";
        string workflow = File.ReadAllText(Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        fixture.CommitFile(relativePath, Encoding.UTF8.GetBytes(workflow), "safe Dependabot auto-merge");
        fixture.Audit().AssertSuccess();

        fixture.CommitFile(
            relativePath,
            Encoding.UTF8.GetBytes(workflow.Replace("version-update:semver-minor", "version-update:semver-major", StringComparison.Ordinal)),
            "unsafe Dependabot auto-merge");
        fixture.Audit().AssertFailure(relativePath);
    }

    [Theory]
    [MemberData(nameof(PullRequestTargetDocuments))]
    public void PullRequestTargetIsRejectedInEverySupportedEventShape(string label, string workflow)
    {
        using Fixture fixture = new();
        fixture.CommitFile(".github/workflows/ci.yml", Encoding.UTF8.GetBytes(workflow), label);

        fixture.Audit().AssertFailure(".github/workflows/ci.yml");
    }

    [Theory]
    [MemberData(nameof(UnpinnedUsesDocuments))]
    public void UnpinnedUsesIsRejectedForQuotedAndUnquotedMappings(string label, string workflow)
    {
        using Fixture fixture = new();
        fixture.CommitFile(".github/workflows/ci.yml", Encoding.UTF8.GetBytes(workflow), label);

        fixture.Audit().AssertFailure(".github/workflows/ci.yml");
    }

    [Theory]
    [MemberData(nameof(InheritedSecretsDocuments))]
    public void InheritedSecretsIsRejectedForQuotedAndUnquotedMappings(string label, string workflow)
    {
        using Fixture fixture = new();
        fixture.CommitFile(".github/workflows/ci.yml", Encoding.UTF8.GetBytes(workflow), label);

        fixture.Audit().AssertFailure(".github/workflows/ci.yml");
    }

    [Fact]
    public void DeletedBinaryStillFailsThroughCompleteHistory()
    {
        using Fixture fixture = new();
        fixture.CommitFile("leaked/GameAssembly.dll", [1, 2, 3], "add binary");
        fixture.RemoveAndCommit("leaked/GameAssembly.dll", "remove binary");

        fixture.Audit().AssertFailure("leaked/GameAssembly.dll");
    }

    [Fact]
    public void ReplacementCommitCannotHideForbiddenOriginalHistory()
    {
        using Fixture fixture = new();
        fixture.InstallCleanReplacementHidingForbiddenBlob();

        fixture.Audit().AssertFailure("GameAssembly.dll");
    }

    [Fact]
    public void HistoricalLocalPathIsRejected()
    {
        using Fixture fixture = new();
        fixture.CommitFile("notes.txt", Encoding.UTF8.GetBytes("C:\\Users\\Abmcar\\SteamLibrary\\game\n"), "local path");

        ProcessResult result = fixture.Audit(nativeErrorPreference: true);
        result.AssertFailure("notes.txt");
    }

    [Fact]
    public void QqCommitIdentityIsRejectedEvenWhenTreeIsSafe()
    {
        using Fixture fixture = new();
        fixture.CommitFile("second.txt", Encoding.UTF8.GetBytes("safe\n"), "qq identity", email: "123456@qq.com");

        fixture.Audit().AssertFailure("identity");
    }

    [Fact]
    public void DirtyTrackedFileFailsWithoutPrintingItsContents()
    {
        using Fixture fixture = new();
        File.AppendAllText(Path.Combine(fixture.Repository, "README.md"), "private dirty content\n", new UTF8Encoding(false));

        ProcessResult result = fixture.Audit(nativeErrorPreference: true);
        result.AssertFailure("dirty");
        Assert.DoesNotContain("private dirty content", result.Stdout + result.Stderr, StringComparison.Ordinal);
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), $"modkit-repository-audit-{Guid.NewGuid():N}");
            Repository = Path.Combine(DirectoryPath, "repository");
            Directory.CreateDirectory(Repository);
            Git("init", "-b", "main").AssertSuccess();
            Git("config", "core.autocrlf", "false").AssertSuccess();
            Git("config", "user.name", "abmcar").AssertSuccess();
            Git("config", "user.email", "52450271+abmcar@users.noreply.github.com").AssertSuccess();
            CommitFile("README.md", Encoding.UTF8.GetBytes("safe\n"), "initial");
        }

        public string DirectoryPath { get; }
        public string Repository { get; }

        public void CommitFile(string relative, byte[] bytes, string message, string? email = null)
        {
            string path = Path.Combine(Repository, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            Git("add", "--", relative).AssertSuccess();
            List<string> args = ["commit", "-m", message];
            ProcessResult result = Git(args, email);
            result.AssertSuccess();
        }

        public void RemoveAndCommit(string relative, string message)
        {
            Git("rm", "--", relative).AssertSuccess();
            Git("commit", "-m", message).AssertSuccess();
        }

        public void InstallCleanReplacementHidingForbiddenBlob()
        {
            CommitFile("GameAssembly.dll", [1, 2, 3], "forbidden original");
            ProcessResult originalResult = Git("rev-parse", "HEAD");
            originalResult.AssertSuccess();
            string original = originalResult.Stdout.Trim();
            Git("rm", "--", "GameAssembly.dll").AssertSuccess();
            ProcessResult treeResult = Git("write-tree");
            treeResult.AssertSuccess();
            ProcessResult replacementResult = Git("commit-tree", treeResult.Stdout.Trim(), "-m", "safe replacement");
            replacementResult.AssertSuccess();
            string replacement = replacementResult.Stdout.Trim();
            Git("reset", "--hard", original).AssertSuccess();
            Git("replace", original, replacement).AssertSuccess();
            Git("reset", "--hard", "HEAD").AssertSuccess();

            ProcessResult head = Git("rev-parse", "HEAD");
            head.AssertSuccess();
            Assert.Equal(original, head.Stdout.Trim());
            ProcessResult status = Git("status", "--porcelain", "--untracked-files=all");
            status.AssertSuccess();
            Assert.Empty(status.Stdout);
        }

        public ProcessResult Audit(bool nativeErrorPreference = false)
        {
            if (!nativeErrorPreference)
                return RunProcess("pwsh", ["-NoLogo", "-NoProfile", "-File", Script, "-RepositoryRoot", Repository]);

            ProcessStartInfo startInfo = CreateStartInfo(
                "pwsh",
                ["-NoLogo", "-NoProfile", "-Command", "$PSNativeCommandUseErrorActionPreference = $true; & $env:MODKIT_AUDIT_SCRIPT -RepositoryRoot $env:MODKIT_AUDIT_REPOSITORY"]);
            startInfo.Environment["MODKIT_AUDIT_SCRIPT"] = Script;
            startInfo.Environment["MODKIT_AUDIT_REPOSITORY"] = Repository;
            return RunProcess(startInfo);
        }

        private ProcessResult Git(params string[] args) => Git((IEnumerable<string>)args, null);

        private ProcessResult Git(IEnumerable<string> args, string? email)
        {
            ProcessStartInfo startInfo = CreateStartInfo("git", new[] { "-C", Repository }.Concat(args));
            if (email is not null)
            {
                startInfo.Environment["GIT_AUTHOR_EMAIL"] = email;
                startInfo.Environment["GIT_COMMITTER_EMAIL"] = email;
                startInfo.Environment["GIT_AUTHOR_NAME"] = "fixture";
                startInfo.Environment["GIT_COMMITTER_NAME"] = "fixture";
            }
            return RunProcess(startInfo);
        }

        public void Dispose()
        {
            TestFileSystem.DeleteDirectoryTree(DirectoryPath);
        }
    }

    private static ProcessResult RunProcess(string fileName, IEnumerable<string> arguments) => RunProcess(CreateStartInfo(fileName, arguments));

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
