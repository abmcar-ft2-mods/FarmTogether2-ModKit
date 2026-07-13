using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Text.Json;
using YamlDotNet.RepresentationModel;
using Xunit;

namespace FarmTogether2.ModKit.Tests;

public sealed class WorkflowContractTests
{
    private const string Checkout = "actions/checkout@34e114876b0b11c390a56381ad16ebd13914f8d5";
    private const string SetupDotNet = "actions/setup-dotnet@67a3573c9a986a3f9c594539f4ab511d57bb3ce9";
    private const string UploadArtifact = "actions/upload-artifact@ea165f8d65b6e75b540449e92b4886f43607fa02";
    private const string DownloadArtifact = "actions/download-artifact@d3f86a106a0bac45b974a628896c90dbdf5c8093";
    private const string CiPath = ".github/workflows/ci.yml";
    private const string ReferenceReleasePath = ".github/workflows/release-ref-package.yml";
    private const string BuildPath = ".github/workflows/reusable-mod-build.yml";
    private const string PublishPath = ".github/workflows/reusable-mod-publish.yml";
    private const string DependabotPath = ".github/dependabot.yml";
    private const string CallerCiPath = "tests/fixtures/mod-repository/.github/workflows/ci.yml";
    private const string CallerReleasePath = "tests/fixtures/mod-repository/.github/workflows/release.yml";
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));

    public static TheoryData<string, string, string, string> RejectedMutations => new()
    {
        { CiPath, Checkout, "actions/checkout@main", "movable official action" },
        { CiPath, "runs-on: windows-2025", "runs-on: windows-latest", "mutable runner" },
        { CiPath, "global-json-file: global.json", "global-json-file: missing.json", "different global.json" },
        { CiPath, "'8.0.421'", "'8.0.422'", "different SDK assertion" },
        { CiPath, "cancel-in-progress: false", "cancel-in-progress: true", "cancelling concurrency" },
        { CiPath, "  pull_request:", "  pull_request_target:", "privileged pull request trigger" },
        { CallerReleasePath, "permissions: {}", "permissions:\n  contents: write", "caller top-level write permission" },
        { CallerReleasePath, "    with:", "    secrets: inherit\n    with:", "inherited secrets" },
        { PublishPath, "      contents: read", "      contents: write", "write permission in verify" },
        { PublishPath, "          GH_TOKEN: ${{ github.token }}", "          GH_TOKEN: ${{ secrets.RELEASE_TOKEN }}", "different gh token" },
        { ReferenceReleasePath, "          GH_TOKEN: ${{ github.token }}", "          GH_TOKEN: missing", "missing gh authentication" },
        { ReferenceReleasePath, "& gh api", "& gh run download 1\n          & gh api", "gh run download" },
        { ReferenceReleasePath, "      - name: Set up locked .NET SDK", "      - uses: softprops/action-gh-release@main\n      - name: Set up locked .NET SDK", "third-party Release action" },
        { BuildPath, "          path: .modkit/bootstrap", "          path: .modkit/tooling", "bootstrap and resolver target collision" },
        { BuildPath, "'.modkit/bootstrap/scripts/Resolve-ModKit.ps1'", "'.modkit/tooling/scripts/Resolve-ModKit.ps1'", "untrusted resolver" },
        { BuildPath, "-Destination '.modkit/packages'", "-Destination '.modkit/bootstrap'", "resolver overwrites bootstrap" },
        { BuildPath, "(Get-FileHash -LiteralPath $packages[0].FullName -Algorithm SHA256)", "('unchecked')", "missing package hash verification" },
        { BuildPath, "rev-parse --abbrev-ref HEAD", "rev-parse HEAD", "missing detached HEAD verification" },
        { BuildPath, "$env:CALLER_REF -ceq 'refs/heads/main'", "$env:CALLER_REF -like 'refs/heads/*'", "non-main candidate ref" },
        { BuildPath, "$env:CANDIDATE_KIND -ceq 'preview'", "$env:CANDIDATE_KIND -ceq 'candidate'", "manual candidate relabel" },
        { BuildPath, "          retention-days: ${{ inputs.retention-days }}", "          retention-days: 91", "unbound retention" },
        { CiPath, "          retention-days: 7", "          retention-days: 8", "reference candidate retention" },
        { PublishPath, "            -ArtifactId $env:EXPECTED_ARTIFACT_ID `", "            -ArtifactId 1 `", "unbound artifact id" },
        { PublishPath, "            -ExpectedRunId $env:EXPECTED_RUN_ID `", "            -ExpectedRunId 1 `", "unbound run id" },
        { PublishPath, "            -ExpectedArtifactName $env:EXPECTED_ARTIFACT_NAME `", "            -ExpectedArtifactName replacement `", "unbound artifact name" },
        { PublishPath, "            -ExpectedCommit $env:EXPECTED_COMMIT `", "            -ExpectedCommit 0123456789abcdef0123456789abcdef01234567 `", "unbound candidate commit" },
        { PublishPath, "            -ExpectedDigest $env:EXPECTED_ARTIFACT_DIGEST `", "            -ExpectedDigest sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa `", "unbound artifact digest" },
        { PublishPath, "candidate.zip", "candidate-extracted", "raw archive not retained" },
        { PublishPath, "if ($LASTEXITCODE -ne 0) { throw \"Candidate receiver failed", "Write-Output 'masked'\n          if ($LASTEXITCODE -ne 0) { throw \"Candidate receiver failed", "masked receiver failure" },
        { PublishPath, "if ($LASTEXITCODE -ne 0) { throw \"Candidate verification failed", "Write-Output 'masked'\n          if ($LASTEXITCODE -ne 0) { throw \"Candidate verification failed", "masked candidate failure" },
        { PublishPath, "          & '.modkit/tooling/scripts/Test-Candidate.ps1'", "          Expand-Archive 'artifacts/publish-evidence/candidate.zip'\n          & '.modkit/tooling/scripts/Test-Candidate.ps1'", "manual extraction" },
        { PublishPath, "      - name: Receive and validate frozen candidate again", "      - name: Premature release mutation\n        shell: pwsh\n        env:\n          GH_TOKEN: ${{ github.token }}\n        run: gh release create invalid\n      - name: Receive and validate frozen candidate again", "Release mutation before second receiver" },
        { PublishPath, "--draft --verify-tag --generate-notes", "--verify-tag --generate-notes", "non-draft first release" },
        { PublishPath, "--draft --verify-tag --generate-notes", "--draft --generate-notes", "missing tag verification" },
        { PublishPath, "--draft --verify-tag --generate-notes", "--draft --verify-tag", "missing generated notes" },
        { PublishPath, "[string]$run.head_branch -cne 'main'", "[string]$run.head_branch -cne 'develop'", "candidate not from main" },
        { PublishPath, "$tagType[0].Trim() -cne 'tag'", "$tagType[0].Trim() -cne 'commit'", "lightweight tag accepted" },
        { PublishPath, "$lines.Count -ne 4", "$lines.Count -lt 4", "duplicate annotated field accepted" },
        { PublishPath, "^Candidate-Run-Id: ([1-9][0-9]*)$", "^Candidate-Run-Id: (.*)$", "malformed run id" },
        { PublishPath, "Candidate-Artifact-Digest: (sha256:[0-9a-f]{64})", "Candidate-Artifact-Digest: (sha256:.*)", "malformed tag digest" },
        { PublishPath, "Test-PublishedAssets.ps1", "Test-Candidate.ps1", "missing deterministic rebuild comparison" },
        { ReferenceReleasePath, "-CandidateKind Reference", "-CandidateKind Mod", "wrong reference candidate kind" },
        { ReferenceReleasePath, "scripts/Pack-GameApiRef.ps1", "dotnet pack", "noncanonical reference writer" },
        { CiPath, "dotnet restore 'FarmTogether2-ModKit.sln' --locked-mode", "dotnet restore 'FarmTogether2-ModKit.sln'", "unlocked restore" },
        { CiPath, "-warnaserror", "", "warnings not errors" },
        { CiPath, "scripts/Test-Repository.ps1", "scripts/Test-Candidate.ps1", "missing leakage scan" },
        { DependabotPath, "interval: weekly", "interval: daily", "non-weekly Dependabot" },
        { DependabotPath, "package-ecosystem: nuget", "package-ecosystem: npm", "extra Dependabot ecosystem" },
        { CallerCiPath, "@0123456789abcdef0123456789abcdef01234567", "@main", "movable caller workflow" },
        { CallerCiPath, "github.ref == 'refs/heads/main'", "github.ref != ''", "caller candidate ref weakened" },
        { CallerReleasePath, "modkit-commit: 0123456789abcdef0123456789abcdef01234567", "modkit-commit: fedcba9876543210fedcba9876543210fedcba98", "caller lock mismatch" }
    };

    [Fact]
    public void WorkflowsSatisfyCompleteContract()
    {
        IReadOnlyDictionary<string, string> files = LoadFiles();
        ValidateAll(files);
    }

    [Fact]
    public void HostedBootstrapContractStartsFromFixtureWithoutModKitDirectory()
    {
        string fixture = Path.Combine(Path.GetTempPath(), $"workflow-clean-runner-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixture);
        try
        {
            Assert.False(Directory.Exists(Path.Combine(fixture, ".modkit")));
            ValidateBootstrapWorkflow(LoadFiles()[BuildPath]);
            ValidateModPublish(LoadFiles()[PublishPath]);
        }
        finally
        {
            Directory.Delete(fixture, recursive: true);
        }
    }

    [Fact]
    public void EveryPowerShellRunBlockParses()
    {
        IReadOnlyDictionary<string, string> files = LoadFiles();
        foreach (string path in new[] { CiPath, ReferenceReleasePath, BuildPath, PublishPath })
        {
            YamlMappingNode jobs = Mapping(ParseYaml(files[path], path), "jobs", path);
            foreach ((YamlNode jobKey, YamlNode jobValue) in jobs.Children)
            {
                string jobName = ((YamlScalarNode)jobKey).Value ?? string.Empty;
                YamlMappingNode job = AsMapping(jobValue, $"{path} job {jobName}");
                YamlSequenceNode steps = Sequence(job, "steps", $"{path} job {jobName}");
                for (int index = 0; index < steps.Children.Count; index++)
                {
                    YamlMappingNode step = AsMapping(steps.Children[index], $"{path} job {jobName} step {index}");
                    string? script = OptionalScalar(step, "run");
                    if (script is null)
                        continue;
                    ProcessStartInfo startInfo = new("pwsh")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false
                    };
                    startInfo.ArgumentList.Add("-NoLogo");
                    startInfo.ArgumentList.Add("-NoProfile");
                    startInfo.ArgumentList.Add("-Command");
                    startInfo.ArgumentList.Add("$tokens = $null; $errors = $null; [void][System.Management.Automation.Language.Parser]::ParseInput($env:WORKFLOW_SCRIPT, [ref]$tokens, [ref]$errors); if ($errors.Count -ne 0) { $errors | ForEach-Object { [Console]::Error.WriteLine($_.Message) }; exit 1 }");
                    startInfo.Environment["WORKFLOW_SCRIPT"] = script;
                    using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start pwsh parser.");
                    string stdout = process.StandardOutput.ReadToEnd();
                    string stderr = process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    Assert.True(process.ExitCode == 0, $"PowerShell parse failed for {path} job {jobName} step {index}.\nstdout:\n{stdout}\nstderr:\n{stderr}");
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(RejectedMutations))]
    public void EachContractViolationIsRejected(string relativePath, string before, string after, string label)
    {
        Dictionary<string, string> files = new(LoadFiles(), StringComparer.Ordinal);
        Assert.True(files.TryGetValue(relativePath, out string? original), $"Unknown mutation file: {relativePath}");
        int index = original.IndexOf(before, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Mutation anchor was not found for {label}: {before}");
        files[relativePath] = string.Concat(original.AsSpan(0, index), after, original.AsSpan(index + before.Length));

        Assert.Throws<InvalidDataException>(() => ValidateAll(files));
    }

    private static IReadOnlyDictionary<string, string> LoadFiles()
    {
        string[] paths = [CiPath, ReferenceReleasePath, BuildPath, PublishPath, DependabotPath, CallerCiPath, CallerReleasePath];
        Dictionary<string, string> files = new(StringComparer.Ordinal);
        foreach (string relativePath in paths)
        {
            string path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"Required workflow file is missing: {relativePath}");
            files.Add(relativePath, File.ReadAllText(path));
        }
        return files;
    }

    private static void ValidateAll(IReadOnlyDictionary<string, string> files)
    {
        Dictionary<string, YamlMappingNode> documents = new(StringComparer.Ordinal);
        foreach ((string path, string text) in files)
        {
            Require(!text.Contains('\r'), $"{path} must use LF line endings.");
            documents[path] = ParseYaml(text, path);
        }

        ValidateGlobalSafety(files, documents);
        ValidateDependabot(documents[DependabotPath]);
        ValidateCi(files[CiPath], documents[CiPath]);
        ValidateBootstrapWorkflow(files[BuildPath]);
        ValidateModPublish(files[PublishPath]);
        ValidateReferencePublish(files[ReferenceReleasePath]);
        ValidateCallers(files[CallerCiPath], files[CallerReleasePath]);
    }

    private static void ValidateGlobalSafety(
        IReadOnlyDictionary<string, string> files,
        IReadOnlyDictionary<string, YamlMappingNode> documents)
    {
        HashSet<string> officialActions = [Checkout, SetupDotNet, UploadArtifact, DownloadArtifact];
        foreach ((string path, string text) in files)
        {
            Require(!Regex.IsMatch(text, @"(?im)^\s*pull_request_target\s*:"), $"{path} uses pull_request_target.");
            Require(!Regex.IsMatch(text, @"(?im)^\s*secrets\s*:\s*inherit\s*$"), $"{path} inherits secrets.");
            Require(!Regex.IsMatch(text, @"(?i)\bgh\s+run\s+download\b"), $"{path} uses gh run download.");
            Require(!Regex.IsMatch(text, @"(?i)Expand-Archive|\b7z(?:\.exe)?\b"), $"{path} manually extracts candidate evidence.");
            Require(!Regex.IsMatch(text, @"(?im)^\s*(?:-\s*)?uses\s*:\s*(?:softprops/|ncipollo/|marvinpinto/)", RegexOptions.CultureInvariant), $"{path} uses a third-party Release action.");

            foreach (Match match in Regex.Matches(text, @"(?im)^\s*(?:-\s*)?uses\s*:\s*([^\s#]+)\s*$"))
            {
                string value = match.Groups[1].Value;
                if (path == CallerCiPath || path == CallerReleasePath)
                {
                    Require(Regex.IsMatch(value, @"^abmcar/FarmTogether2-ModKit/\.github/workflows/reusable-mod-(?:build|publish)\.yml@[0-9a-f]{40}$"), $"{path} has a movable caller use.");
                }
                else
                {
                    Require(officialActions.Contains(value), $"{path} has an unapproved or incorrectly pinned action: {value}");
                }
            }
        }

        foreach (string path in new[] { CiPath, ReferenceReleasePath, BuildPath, PublishPath })
        {
            YamlMappingNode root = documents[path];
            YamlMappingNode concurrency = Mapping(root, "concurrency", path);
            Require(Scalar(concurrency, "cancel-in-progress", path) == "false", $"{path} must not cancel in-progress work.");
            YamlMappingNode jobs = Mapping(root, "jobs", path);
            foreach ((YamlNode jobKey, YamlNode jobValue) in jobs.Children)
            {
                string jobName = ((YamlScalarNode)jobKey).Value ?? string.Empty;
                YamlMappingNode job = AsMapping(jobValue, $"{path} job {jobName}");
                Require(Scalar(job, "runs-on", path) == "windows-2025", $"{path} job {jobName} must use windows-2025.");
                ValidateSdkAndGhSteps(job, $"{path} job {jobName}");
            }
        }
    }

    private static void ValidateSdkAndGhSteps(YamlMappingNode job, string label)
    {
        YamlSequenceNode steps = Sequence(job, "steps", label);
        int setupIndex = -1;
        for (int index = 0; index < steps.Children.Count; index++)
        {
            YamlMappingNode step = AsMapping(steps.Children[index], $"{label} step {index}");
            string? uses = OptionalScalar(step, "uses");
            string? run = OptionalScalar(step, "run");
            if (uses == SetupDotNet)
            {
                Require(setupIndex < 0, $"{label} has duplicate setup-dotnet steps.");
                setupIndex = index;
                YamlMappingNode with = Mapping(step, "with", label);
                Require(Scalar(with, "global-json-file", label) == "global.json", $"{label} setup-dotnet must use global.json.");
            }
            if (run is not null &&
                (Regex.IsMatch(run, @"(?i)\bgh\s") || run.Contains("Resolve-ModKit.ps1", StringComparison.Ordinal) || run.Contains("Receive-VerifiedArtifact.ps1", StringComparison.Ordinal)))
            {
                YamlMappingNode env = Mapping(step, "env", label);
                Require(Scalar(env, "GH_TOKEN", label) == "${{ github.token }}", $"{label} gh boundary lacks the workflow token.");
            }
        }
        Require(setupIndex >= 0 && setupIndex + 1 < steps.Children.Count, $"{label} lacks setup-dotnet and an immediate assertion.");
        YamlMappingNode assertion = AsMapping(steps.Children[setupIndex + 1], $"{label} SDK assertion");
        string assertionScript = Scalar(assertion, "run", label);
        Require(Regex.IsMatch(assertionScript, @"\(dotnet --version\)\.Trim\(\) -cne '8\.0\.421'", RegexOptions.CultureInvariant), $"{label} lacks exact SDK assertion.");
    }

    private static void ValidateDependabot(YamlMappingNode root)
    {
        Require(Scalar(root, "version", DependabotPath) == "2", "Dependabot version must be 2.");
        YamlSequenceNode updates = Sequence(root, "updates", DependabotPath);
        Require(updates.Children.Count == 2, "Dependabot must contain exactly two ecosystems.");
        string[] expected = ["github-actions", "nuget"];
        for (int index = 0; index < updates.Children.Count; index++)
        {
            YamlMappingNode update = AsMapping(updates.Children[index], "Dependabot update");
            Require(Scalar(update, "package-ecosystem", DependabotPath) == expected[index], "Dependabot ecosystem or order differs.");
            Require(Scalar(update, "directory", DependabotPath) == "/", "Dependabot directory must be repository root.");
            Require(Scalar(Mapping(update, "schedule", DependabotPath), "interval", DependabotPath) == "weekly", "Dependabot must run weekly.");
        }
    }

    private static void ValidateCi(string text, YamlMappingNode root)
    {
        RequirePermissions(root, new Dictionary<string, string> { ["contents"] = "read" }, CiPath);
        Require(text.Contains("dotnet restore 'FarmTogether2-ModKit.sln' --locked-mode", StringComparison.Ordinal), "CI restore must be locked.");
        Require(text.Contains("-warnaserror", StringComparison.Ordinal), "CI build must treat warnings as errors.");
        Require(text.Contains("dotnet test 'FarmTogether2-ModKit.sln' -c Release --no-build --no-restore", StringComparison.Ordinal), "CI must run all tests.");
        Require(text.Contains("scripts/Pack-GameApiRef.ps1", StringComparison.Ordinal) && text.Contains("ref-package verify", StringComparison.Ordinal), "CI must write and inspect the reference package.");
        Require(text.Contains("tests/WorkflowContract.Tests/Test-CallerWorkflows.ps1", StringComparison.Ordinal), "CI must validate caller workflows.");
        Require(text.Contains("git diff --check", StringComparison.Ordinal) && text.Contains("scripts/Test-Repository.ps1", StringComparison.Ordinal), "CI must run diff and leakage checks.");
        Require(text.Contains("id: candidate", StringComparison.Ordinal) && text.Contains("FarmTogether2.GameApi.Ref-candidate-${{ github.sha }}", StringComparison.Ordinal), "CI main artifact identity is missing.");
        Require(text.Contains("retention-days: 7", StringComparison.Ordinal), "CI candidate retention must be seven days.");
        Require(text.Contains("steps.candidate.outputs.artifact-id", StringComparison.Ordinal) && text.Contains("steps.candidate.outputs.artifact-digest", StringComparison.Ordinal), "CI must summarize immutable artifact evidence.");
    }

    private static void ValidateBootstrapWorkflow(string text)
    {
        YamlMappingNode root = ParseYaml(text, BuildPath);
        RequirePermissions(root, new Dictionary<string, string> { ["contents"] = "read" }, BuildPath);
        YamlMappingNode on = Mapping(root, "on", BuildPath);
        YamlMappingNode call = Mapping(on, "workflow_call", BuildPath);
        YamlMappingNode inputs = Mapping(call, "inputs", BuildPath);
        RequireInputs(inputs, new Dictionary<string, string>
        {
            ["modkit-commit"] = "string",
            ["game-api-mode"] = "string",
            ["configuration"] = "string",
            ["candidate-kind"] = "string",
            ["retention-days"] = "number"
        }, BuildPath);
        YamlMappingNode build = Mapping(Mapping(root, "jobs", BuildPath), "build", BuildPath);
        RequirePermissions(build, new Dictionary<string, string> { ["contents"] = "read" }, BuildPath + " build");
        string scripts = JoinScripts(build, BuildPath + " build");
        Require(text.Contains("path: .modkit/bootstrap", StringComparison.Ordinal) && text.Contains("ref: ${{ inputs.modkit-commit }}", StringComparison.Ordinal), "Build must check out exact bootstrap commit.");
        Require(text.Contains("$env:CALLER_EVENT -ceq 'push'", StringComparison.Ordinal) && text.Contains("$env:CALLER_REF -ceq 'refs/heads/main'", StringComparison.Ordinal) &&
                text.Contains("$env:CANDIDATE_KIND -ceq 'candidate'", StringComparison.Ordinal) && text.Contains("@('pull_request', 'workflow_dispatch')", StringComparison.Ordinal) &&
                text.Contains("$env:CANDIDATE_KIND -ceq 'preview'", StringComparison.Ordinal), "Called build workflow must enforce event/ref/kind pairs.");
        Require(text.Contains("workflowCommit -cne $env:MODKIT_COMMIT", StringComparison.Ordinal), "Build must bind caller lock to modkit-commit.");
        AssertOrder(scripts,
            ".modkit/bootstrap/scripts/Resolve-ModKit.ps1",
            "-Destination '.modkit/packages'",
            "Get-FileHash -LiteralPath $packages[0].FullName -Algorithm SHA256",
            "rev-parse --abbrev-ref HEAD",
            ".modkit/tooling/scripts/Invoke-ModBuild.ps1",
            ".modkit/tooling/scripts/Pack-Mod.ps1");
        Require(!scripts.Contains(".modkit/tooling/scripts/Resolve-ModKit.ps1", StringComparison.Ordinal), "Build may not run the resolver from its replacement target.");
        Require(text.Contains("id: candidate", StringComparison.Ordinal) && text.Contains("retention-days: ${{ inputs.retention-days }}", StringComparison.Ordinal), "Build upload contract is incomplete.");
        Require(text.Contains("steps.candidate.outputs.artifact-id", StringComparison.Ordinal) && text.Contains("steps.candidate.outputs.artifact-digest", StringComparison.Ordinal), "Build must summarize upload evidence.");
    }

    private static void ValidateModPublish(string text)
    {
        YamlMappingNode root = ParseYaml(text, PublishPath);
        RequirePermissions(root, new Dictionary<string, string>(), PublishPath);
        YamlMappingNode call = Mapping(Mapping(root, "on", PublishPath), "workflow_call", PublishPath);
        RequireInputs(Mapping(call, "inputs", PublishPath), new Dictionary<string, string>
        {
            ["tag"] = "string",
            ["modkit-commit"] = "string"
        }, PublishPath);
        YamlMappingNode jobs = Mapping(root, "jobs", PublishPath);
        YamlMappingNode verify = Mapping(jobs, "verify", PublishPath);
        YamlMappingNode publish = Mapping(jobs, "publish", PublishPath);
        RequirePermissions(verify, new Dictionary<string, string> { ["actions"] = "read", ["contents"] = "read" }, "mod verify");
        RequirePermissions(publish, new Dictionary<string, string> { ["actions"] = "read", ["contents"] = "write" }, "mod publish");
        Require(Scalar(publish, "needs", PublishPath) == "verify", "Mod publish must depend on verify.");
        ValidateAnnotatedEvidence(text, "Mod");
        ValidatePublishJob(text, verify, "verify", isReference: false);
        ValidatePublishJob(text, publish, "publish", isReference: false);
        string verifyScripts = JoinScripts(verify, "mod verify");
        AssertOrder(verifyScripts, "Receive-VerifiedArtifact.ps1", "Test-Candidate.ps1", "Invoke-ModBuild.ps1", "Pack-Mod.ps1", "Test-PublishedAssets.ps1");
        string publishScripts = JoinScripts(publish, "mod publish");
        int receiver = publishScripts.IndexOf("Receive-VerifiedArtifact.ps1", StringComparison.Ordinal);
        int releaseMutation = FirstReleaseMutation(publishScripts);
        Require(receiver >= 0 && releaseMutation > receiver, "Mod Release mutation must follow the publish job receiver.");
        ValidateDraftRelease(publishScripts);
    }

    private static void ValidateReferencePublish(string text)
    {
        YamlMappingNode root = ParseYaml(text, ReferenceReleasePath);
        RequirePermissions(root, new Dictionary<string, string>(), ReferenceReleasePath);
        YamlMappingNode jobs = Mapping(root, "jobs", ReferenceReleasePath);
        YamlMappingNode verify = Mapping(jobs, "verify", ReferenceReleasePath);
        YamlMappingNode publish = Mapping(jobs, "publish", ReferenceReleasePath);
        RequirePermissions(verify, new Dictionary<string, string> { ["actions"] = "read", ["contents"] = "read" }, "reference verify");
        RequirePermissions(publish, new Dictionary<string, string> { ["actions"] = "read", ["contents"] = "write" }, "reference publish");
        Require(Scalar(publish, "needs", ReferenceReleasePath) == "verify", "Reference publish must depend on verify.");
        ValidateAnnotatedEvidence(text, "Reference");
        ValidatePublishJob(text, verify, "verify", isReference: true);
        ValidatePublishJob(text, publish, "publish", isReference: true);
        string verifyScripts = JoinScripts(verify, "reference verify");
        AssertOrder(verifyScripts, "Receive-VerifiedArtifact.ps1", "Test-Candidate.ps1", "Pack-GameApiRef.ps1", "Test-PublishedAssets.ps1");
        Require(verifyScripts.Contains("dotnet restore 'FarmTogether2-ModKit.sln' --locked-mode", StringComparison.Ordinal) &&
                verifyScripts.Contains("dotnet test 'FarmTogether2-ModKit.sln'", StringComparison.Ordinal), "Reference verify must independently rebuild and test the tag commit.");
        string publishScripts = JoinScripts(publish, "reference publish");
        int receiver = publishScripts.IndexOf("Receive-VerifiedArtifact.ps1", StringComparison.Ordinal);
        int releaseMutation = FirstReleaseMutation(publishScripts);
        Require(receiver >= 0 && releaseMutation > receiver, "Reference Release mutation must follow the publish job receiver.");
        ValidateDraftRelease(publishScripts);
    }

    private static void ValidateAnnotatedEvidence(string text, string kind)
    {
        Require(text.Contains("cat-file -t", StringComparison.Ordinal) && text.Contains("-cne 'tag'", StringComparison.Ordinal), $"{kind} publish must reject lightweight tags.");
        Require(text.Contains("git ls-remote --tags origin", StringComparison.Ordinal), $"{kind} publish must verify the remote tag.");
        string artifactNamePattern = kind == "Reference"
            ? "^Candidate-Artifact-Name: (FarmTogether2\\.GameApi\\.Ref-candidate-[0-9a-f]{40})$"
            : "^Candidate-Artifact-Name: ([A-Za-z_][A-Za-z0-9_.]*-candidate-[0-9a-f]{40})$";
        string[] patterns =
        [
            "^Candidate-Run-Id: ([1-9][0-9]*)$",
            "^Candidate-Artifact-Id: ([1-9][0-9]*)$",
            artifactNamePattern,
            "^Candidate-Artifact-Digest: (sha256:[0-9a-f]{64})$"
        ];
        foreach (string pattern in patterns)
            Require(text.Contains(pattern, StringComparison.Ordinal), $"{kind} publish lacks exact annotated evidence parsing: {pattern}");
        Require(text.Contains("$lines.Count -ne 4", StringComparison.Ordinal), $"{kind} publish must require exactly four evidence fields.");
        Require(text.Contains("head_branch -cne 'main'", StringComparison.Ordinal) && text.Contains("conclusion -cne 'success'", StringComparison.Ordinal), $"{kind} publish must bind a successful main push.");
        Require(text.Contains("actions/artifacts/$artifactId", StringComparison.Ordinal), $"{kind} verify must query the numeric artifact.");
        Require(text.Contains("actions/artifacts/$env:EXPECTED_ARTIFACT_ID", StringComparison.Ordinal), $"{kind} publish must re-query the same numeric artifact.");
        Require(text.Contains("artifact.workflow_run.id", StringComparison.Ordinal) && text.Contains("artifact.workflow_run.head_sha", StringComparison.Ordinal), $"{kind} publish must bind artifact run and commit.");
    }

    private static void ValidatePublishJob(string workflowText, YamlMappingNode job, string jobName, bool isReference)
    {
        string label = $"{(isReference ? "reference" : "mod")} {jobName}";
        string scripts = JoinScripts(job, label);
        Require(workflowText.Contains("fetch-depth: 0", StringComparison.Ordinal), $"{label} must fetch full tag history.");
        if (!isReference)
        {
            Require(CountInJob(job, step => OptionalScalar(step, "uses") == Checkout &&
                OptionalScalar(Mapping(step, "with", label, required: false), "path") == ".modkit/bootstrap") == 1, $"{label} needs one bootstrap checkout.");
            AssertOrder(scripts,
                ".modkit/bootstrap/scripts/Resolve-ModKit.ps1",
                "Get-FileHash -LiteralPath $packages[0].FullName -Algorithm SHA256",
                "rev-parse --abbrev-ref HEAD",
                "Receive-VerifiedArtifact.ps1");
            Require(scripts.Contains("workflowCommit", StringComparison.Ordinal), $"{label} must bind the caller lock.");
            Require(scripts.Contains("mod.json", StringComparison.Ordinal) && scripts.Contains("$env:REQUESTED_TAG -cne \"v$([string]$mod.version)\"", StringComparison.Ordinal), $"{label} must bind tag to mod version.");
        }
        else
        {
            Require(scripts.Contains("FarmTogether2.GameApi.Ref.csproj", StringComparison.Ordinal) && scripts.Contains("$env:REQUESTED_TAG -cne \"v$([string]$versions[0])\"", StringComparison.Ordinal), $"{label} must bind tag to reference package version.");
        }
        string[] receiverSteps = Sequence(job, "steps", label).Children
            .Select((step, index) => AsMapping(step, $"{label} step {index}"))
            .Select(step => OptionalScalar(step, "run"))
            .Where(run => run is not null && run.Contains("Receive-VerifiedArtifact.ps1", StringComparison.Ordinal))
            .Cast<string>()
            .ToArray();
        Require(receiverSteps.Length == 1, $"{label} needs exactly one receiver step.");
        string receiverScript = receiverSteps[0];
        string expectedKind = isReference ? "Reference" : "Mod";
        Match boundary = Regex.Match(
            receiverScript,
            @"(?s)&\s+'[^']*Receive-VerifiedArtifact\.ps1'(?<receiver>.*?)-DestinationDirectory\s+'[^']+'\s*\n\s*if \(\$LASTEXITCODE -ne 0\)[^\n]*\n\s*&\s+'[^']*Test-Candidate\.ps1'(?<candidate>.*?)-CandidateKind\s+(?<kind>Mod|Reference)\s*\n\s*if \(\$LASTEXITCODE -ne 0\)[^\n]*",
            RegexOptions.CultureInvariant);
        Require(boundary.Success, $"{label} must retain raw archive and immediately check receiver and candidate exits.");
        string receiverBlock = boundary.Groups["receiver"].Value;
        Require(receiverBlock.Contains("-ArtifactId $env:EXPECTED_ARTIFACT_ID", StringComparison.Ordinal) &&
                receiverBlock.Contains("-ExpectedArtifactName $env:EXPECTED_ARTIFACT_NAME", StringComparison.Ordinal) &&
                receiverBlock.Contains("-ExpectedRunId $env:EXPECTED_RUN_ID", StringComparison.Ordinal) &&
                receiverBlock.Contains("-ExpectedCommit $env:EXPECTED_COMMIT", StringComparison.Ordinal) &&
                receiverBlock.Contains("-ExpectedDigest $env:EXPECTED_ARTIFACT_DIGEST", StringComparison.Ordinal) &&
                Regex.IsMatch(receiverBlock, @"-ArchivePath\s+'[^']+\.zip'", RegexOptions.CultureInvariant), $"{label} receiver is not bound to frozen evidence.");
        string candidateBlock = boundary.Groups["candidate"].Value;
        Require(candidateBlock.Contains("-ExpectedArtifactName $env:EXPECTED_ARTIFACT_NAME", StringComparison.Ordinal) &&
                candidateBlock.Contains("-ExpectedRunId $env:EXPECTED_RUN_ID", StringComparison.Ordinal) &&
                candidateBlock.Contains("-ExpectedCommit $env:EXPECTED_COMMIT", StringComparison.Ordinal), $"{label} candidate verifier is not bound to frozen evidence.");
        Require(boundary.Groups["kind"].Value == expectedKind, $"{label} uses the wrong candidate kind.");
        Require(scripts.Contains("$tagType[0].Trim() -cne 'tag'", StringComparison.Ordinal), $"{label} must reject lightweight tags.");
        Require(scripts.Contains("[string]$run.head_branch -cne 'main'", StringComparison.Ordinal), $"{label} must require a main-push candidate run.");
        Require(!scripts.Contains("actions/download-artifact", StringComparison.Ordinal), $"{label} may not substitute actions/download-artifact for the receiver.");
    }

    private static void ValidateDraftRelease(string scripts)
    {
        Require(scripts.Contains("gh release create", StringComparison.Ordinal) &&
                scripts.Contains("--draft --verify-tag --generate-notes", StringComparison.Ordinal), "Release must be created draft-first with tag verification and generated notes.");
        Require(scripts.Contains("gh release download", StringComparison.Ordinal) && scripts.Contains("Test-PublishedAssets.ps1", StringComparison.Ordinal), "Remote Release assets must be verified before publication.");
        Require(scripts.Contains("gh release edit", StringComparison.Ordinal) && scripts.Contains("--draft=false --verify-tag", StringComparison.Ordinal), "Verified draft must be explicitly published with tag verification.");
        Require(!scripts.Contains("artifacts/rebuild", StringComparison.Ordinal), "Publish may upload only the original frozen candidate.");
    }

    private static int FirstReleaseMutation(string scripts)
    {
        int[] indexes = [
            scripts.IndexOf("gh release create", StringComparison.Ordinal),
            scripts.IndexOf("gh release upload", StringComparison.Ordinal),
            scripts.IndexOf("gh release edit", StringComparison.Ordinal)
        ];
        return indexes.Where(index => index >= 0).DefaultIfEmpty(-1).Min();
    }

    private static void ValidateCallers(string ci, string release)
    {
        string lockPath = Path.Combine(Root, "tests", "fixtures", "mod-repository", "modkit.lock.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(lockPath));
        string commit = document.RootElement.GetProperty("workflowCommit").GetString() ?? string.Empty;
        Require(Regex.IsMatch(commit, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant), "Fixture workflowCommit is invalid.");
        Require(ci.Contains($"reusable-mod-build.yml@{commit}", StringComparison.Ordinal) && ci.Contains($"modkit-commit: {commit}", StringComparison.Ordinal), "Caller CI is not pinned to its lock.");
        Require(ci.Contains("github.event_name == 'push' && github.ref == 'refs/heads/main' && 'candidate' || 'preview'", StringComparison.Ordinal), "Caller CI does not enforce candidate event/ref.");
        Require(ci.Contains("retention-days: 7", StringComparison.Ordinal), "Caller CI retention must be seven days.");
        Require(release.Contains($"reusable-mod-publish.yml@{commit}", StringComparison.Ordinal) && release.Contains($"modkit-commit: {commit}", StringComparison.Ordinal), "Caller release is not pinned to its lock.");
        Require(release.Contains("tag: ${{ github.event_name == 'workflow_dispatch' && inputs.tag || github.ref_name }}", StringComparison.Ordinal), "Caller release does not bind push/dispatch tag.");
        Require(Regex.IsMatch(release, @"(?s)permissions:\s*\{\}.*?publish:\s*permissions:\s*actions:\s*read\s*contents:\s*write"), "Caller release permission boundary is wrong.");
    }

    private static void RequireInputs(YamlMappingNode inputs, IReadOnlyDictionary<string, string> expected, string label)
    {
        Require(inputs.Children.Count == expected.Count, $"{label} has missing or extra workflow_call inputs.");
        foreach ((string name, string type) in expected)
        {
            YamlMappingNode input = Mapping(inputs, name, label);
            Require(Scalar(input, "required", label) == "true" && Scalar(input, "type", label) == type, $"{label} input {name} must be required {type}.");
        }
    }

    private static void RequirePermissions(YamlMappingNode node, IReadOnlyDictionary<string, string> expected, string label)
    {
        YamlMappingNode permissions = Mapping(node, "permissions", label);
        Require(permissions.Children.Count == expected.Count, $"{label} permissions contain missing or extra scopes.");
        foreach ((string name, string value) in expected)
            Require(Scalar(permissions, name, label) == value, $"{label} permission {name} must be {value}.");
    }

    private static int CountInJob(YamlMappingNode job, Func<YamlMappingNode, bool> predicate)
    {
        return Sequence(job, "steps", "job").Children.Select((step, index) => AsMapping(step, $"step {index}")).Count(predicate);
    }

    private static string JoinScripts(YamlMappingNode job, string label)
    {
        return string.Join("\n", Sequence(job, "steps", label).Children
            .Select((step, index) => AsMapping(step, $"{label} step {index}"))
            .Select(step => OptionalScalar(step, "run"))
            .Where(run => run is not null));
    }

    private static void AssertOrder(string text, params string[] tokens)
    {
        int cursor = -1;
        foreach (string token in tokens)
        {
            int index = text.IndexOf(token, cursor + 1, StringComparison.Ordinal);
            Require(index >= 0, $"Required ordered workflow token is missing or out of order: {token}");
            cursor = index;
        }
    }

    private static YamlMappingNode ParseYaml(string text, string label)
    {
        try
        {
            using StringReader reader = new(text);
            YamlStream yaml = new();
            yaml.Load(reader);
            Require(yaml.Documents.Count == 1, $"{label} must contain one YAML document.");
            return AsMapping(yaml.Documents[0].RootNode, label);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidDataException($"{label} is not valid YAML.", exception);
        }
    }

    private static YamlMappingNode Mapping(YamlMappingNode parent, string key, string label, bool required = true)
    {
        if (!TryGet(parent, key, out YamlNode? value))
        {
            if (!required)
                return new YamlMappingNode();
            throw new InvalidDataException($"{label} is missing mapping {key}.");
        }
        return AsMapping(value!, $"{label}.{key}");
    }

    private static YamlSequenceNode Sequence(YamlMappingNode parent, string key, string label)
    {
        if (!TryGet(parent, key, out YamlNode? value) || value is not YamlSequenceNode sequence)
            throw new InvalidDataException($"{label} is missing sequence {key}.");
        return sequence;
    }

    private static string Scalar(YamlMappingNode parent, string key, string label)
    {
        string? value = OptionalScalar(parent, key);
        if (value is null)
            throw new InvalidDataException($"{label} is missing scalar {key}.");
        return value;
    }

    private static string? OptionalScalar(YamlMappingNode parent, string key)
    {
        if (!TryGet(parent, key, out YamlNode? value))
            return null;
        if (value is not YamlScalarNode scalar)
            throw new InvalidDataException($"{key} must be a scalar.");
        return scalar.Value;
    }

    private static bool TryGet(YamlMappingNode parent, string key, out YamlNode? value)
    {
        foreach ((YamlNode candidateKey, YamlNode candidateValue) in parent.Children)
        {
            if (candidateKey is YamlScalarNode scalar && scalar.Value == key)
            {
                value = candidateValue;
                return true;
            }
        }
        value = null;
        return false;
    }

    private static YamlMappingNode AsMapping(YamlNode node, string label)
    {
        if (node is not YamlMappingNode mapping)
            throw new InvalidDataException($"{label} must be a mapping.");
        return mapping;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }
}
