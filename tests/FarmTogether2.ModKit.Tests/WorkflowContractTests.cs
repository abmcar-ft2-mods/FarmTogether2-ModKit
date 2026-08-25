using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Text.Json;
using YamlDotNet.RepresentationModel;
using Xunit;

namespace FarmTogether2.ModKit.Tests;

public sealed class WorkflowContractTests
{
    private const string DownloadArtifact = "actions/download-artifact@d3f86a106a0bac45b974a628896c90dbdf5c8093";
    private const string CiPath = ".github/workflows/ci.yml";
    private const string ReferenceReleasePath = ".github/workflows/release-ref-package.yml";
    private const string BuildPath = ".github/workflows/reusable-mod-build.yml";
    private const string PublishPath = ".github/workflows/reusable-mod-publish.yml";
    private const string DependabotAutoMergePath = ".github/workflows/dependabot-auto-merge.yml";
    private const string ReleasePublisherPath = "scripts/Publish-VerifiedRelease.ps1";
    private const string DependabotPath = ".github/dependabot.yml";
    private const string CallerCiPath = "tests/fixtures/mod-repository/.github/workflows/ci.yml";
    private const string CallerReleasePath = "tests/fixtures/mod-repository/.github/workflows/release.yml";
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private static readonly string Checkout = ReadPinnedAction(CiPath, "actions/checkout");
    private static readonly string SetupDotNet = ReadPinnedAction(CiPath, "actions/setup-dotnet");
    private static readonly string UploadArtifact = ReadPinnedAction(CiPath, "actions/upload-artifact");
    private static readonly string DependabotMetadata = ReadPinnedAction(DependabotAutoMergePath, "dependabot/fetch-metadata");

    public static TheoryData<string, string, string, string> RejectedMutations => new()
    {
        { CiPath, Checkout, "actions/checkout@main", "movable official action" },
        { CiPath, "runs-on: windows-2025", "runs-on: windows-latest", "mutable runner" },
        // Intentionally reintroduce the retired .NET 8 auxiliary SDK to prove the workflow contract rejects it.
        { CiPath, "          global-json-file: global.json", "          dotnet-version: 8.0.x\n          global-json-file: global.json", "redundant auxiliary SDK download" },
        { ReferenceReleasePath, "          global-json-file: global.json", "          dotnet-version: 8.0.x\n          global-json-file: global.json", "redundant release SDK download" },
        { CiPath, "global-json-file: global.json", "global-json-file: missing.json", "different global.json" },
        { CiPath, "Get-Content -LiteralPath 'global.json'", "Get-Content -LiteralPath 'missing.json'", "different SDK assertion source" },
        { CiPath, "cancel-in-progress: false", "cancel-in-progress: true", "cancelling concurrency" },
        { CiPath, "  pull_request:", "  pull_request_target:", "privileged pull request trigger" },
        { CiPath, "  pull_request:", "  \"pull_request_target\":", "quoted privileged pull request trigger" },
        { CiPath, "on:\n  push:\n    branches: [main]\n  pull_request:\n  workflow_dispatch:", "on: [push, pull_request_target]", "flow privileged pull request trigger" },
        { CiPath, "on:\n  push:\n    branches: [main]\n  pull_request:\n  workflow_dispatch:", "on: >-\n  pull_request_target", "folded privileged pull request trigger" },
        { CiPath, "on:\n  push:\n    branches: [main]\n  pull_request:\n  workflow_dispatch:", "on: |-\n  pull_request_target", "literal privileged pull request trigger" },
        { CallerReleasePath, "permissions: {}", "permissions:\n  contents: write", "caller top-level write permission" },
        { CallerReleasePath, "    with:", "    secrets: inherit\n    with:", "inherited secrets" },
        { CallerReleasePath, "    with:", "    \"secrets\": \"inherit\"\n    with:", "quoted inherited secrets" },
        { CallerReleasePath, "permissions: {}", "permissions: {}\nx-probe: { \"secrets\": \"inherit\" }", "flow inherited secrets" },
        { CallerReleasePath, "    with:", "    secrets: >-\n      inherit\n    with:", "folded inherited secrets" },
        { CallerReleasePath, "    with:", "    secrets: |-\n      inherit\n    with:", "literal inherited secrets" },
        { CallerCiPath, "modkit_read_token: ${{ secrets.MODKIT_READ_TOKEN }}", "modkit_read_token: ${{ secrets.OTHER_TOKEN }}", "wrong caller CI read secret" },
        { CallerReleasePath, "modkit_read_token: ${{ secrets.MODKIT_READ_TOKEN }}", "modkit_read_token: ${{ secrets.OTHER_TOKEN }}", "wrong caller release read secret" },
        { CallerReleasePath, "      attestations: read\n", "", "missing caller attestation permission" },
        { PublishPath, "      contents: read", "      contents: write", "write permission in verify" },
        { PublishPath, "          GH_TOKEN: ${{ github.token }}", "          GH_TOKEN: ${{ secrets.RELEASE_TOKEN }}", "different gh token" },
        { BuildPath, "          token: ${{ secrets.modkit_read_token }}", "          token: ${{ github.token }}", "build bootstrap uses caller token" },
        { BuildPath, "          GH_TOKEN: ${{ secrets.modkit_read_token }}", "          GH_TOKEN: ${{ github.token }}", "build resolver uses caller token" },
        { BuildPath, "      modkit_read_token:\n        required: true", "      modkit_read_token:\n        required: false", "optional build read secret" },
        { BuildPath, "& gh auth setup-git --hostname github.com", "& Write-Output 'authentication skipped'", "missing build Git authentication" },
        { PublishPath, "          token: ${{ secrets.modkit_read_token }}", "          token: ${{ github.token }}", "publish bootstrap uses caller token" },
        { PublishPath, "          GH_TOKEN: ${{ secrets.modkit_read_token }}", "          GH_TOKEN: ${{ github.token }}", "publish resolver uses caller token" },
        { PublishPath, "      modkit_read_token:\n        required: true", "      modkit_read_token:\n        required: false", "optional publish read secret" },
        { PublishPath, "      attestations: read\n", "", "missing mod attestation permission" },
        { ReferenceReleasePath, "      attestations: read\n", "", "missing reference attestation permission" },
        { ReferenceReleasePath, "          GH_TOKEN: ${{ github.token }}", "          GH_TOKEN: missing", "missing gh authentication" },
        { ReferenceReleasePath, "& gh api", "& gh run download 1\n          & gh api", "gh run download" },
        { ReferenceReleasePath, "      - name: Set up locked .NET SDK", "      - uses: softprops/action-gh-release@main\n      - name: Set up locked .NET SDK", "third-party Release action" },
        { PublishPath, "      - name: Receive and validate frozen candidate", "      - name: Download candidate through generic artifact action\n        uses: " + DownloadArtifact + "\n      - name: Receive and validate frozen candidate", "download-artifact used in mod verify" },
        { PublishPath, "      - name: Receive and validate frozen candidate again", "      - name: Download candidate through generic artifact action\n        uses: " + DownloadArtifact + "\n      - name: Receive and validate frozen candidate again", "download-artifact used in mod publish" },
        { ReferenceReleasePath, "      - name: Receive and validate frozen reference candidate", "      - name: Download candidate through generic artifact action\n        uses: " + DownloadArtifact + "\n      - name: Receive and validate frozen reference candidate", "download-artifact used in reference verify" },
        { ReferenceReleasePath, "      - name: Receive and validate frozen reference candidate again", "      - name: Download candidate through generic artifact action\n        uses: " + DownloadArtifact + "\n      - name: Receive and validate frozen reference candidate again", "download-artifact used in reference publish" },
        { BuildPath, "          path: .modkit/bootstrap", "          path: .modkit/tooling", "bootstrap and resolver target collision" },
        { BuildPath, "'.modkit/bootstrap/scripts/Resolve-ModKit.ps1'", "'.modkit/tooling/scripts/Resolve-ModKit.ps1'", "untrusted resolver" },
        { BuildPath, "-Destination '.modkit/packages'", "-Destination '.modkit/bootstrap'", "resolver overwrites bootstrap" },
        { PublishPath, "      - name: Check out caller at requested tag\n        uses: " + Checkout + "\n        with:\n          ref: ${{ steps.request.outputs.tag }}\n          fetch-depth: 0", "      - name: Check out caller at requested tag\n        uses: " + Checkout + "\n        with:\n          ref: ${{ steps.request.outputs.tag }}\n          fetch-depth: 1", "mod verify shallow tag checkout" },
        { PublishPath, "      - name: Check out caller at verified tag\n        uses: " + Checkout + "\n        with:\n          ref: ${{ steps.request.outputs.tag }}\n          fetch-depth: 0", "      - name: Check out caller at verified tag\n        uses: " + Checkout + "\n        with:\n          ref: ${{ steps.request.outputs.tag }}\n          fetch-depth: 1", "mod publish shallow tag checkout" },
        { ReferenceReleasePath, "      - name: Check out exact reference tag\n        uses: " + Checkout + "\n        with:\n          ref: ${{ steps.request.outputs.tag }}\n          fetch-depth: 0", "      - name: Check out exact reference tag\n        uses: " + Checkout + "\n        with:\n          ref: ${{ steps.request.outputs.tag }}\n          fetch-depth: 1", "reference prepare shallow tag checkout" },
        { ReferenceReleasePath, "      - name: Check out verified reference commit\n        uses: " + Checkout + "\n        with:\n          ref: ${{ needs.prepare.outputs.commit }}\n          fetch-depth: 0", "      - name: Check out verified reference commit\n        uses: " + Checkout + "\n        with:\n          ref: ${{ needs.prepare.outputs.tag }}\n          fetch-depth: 0", "reference verification uses mutable tag" },
        { ReferenceReleasePath, "      - name: Check out exact verified reference tag\n        uses: " + Checkout + "\n        with:\n          ref: ${{ steps.request.outputs.tag }}\n          fetch-depth: 0", "      - name: Check out exact verified reference tag\n        uses: " + Checkout + "\n        with:\n          ref: ${{ steps.request.outputs.tag }}\n          fetch-depth: 1", "reference publish shallow tag checkout" },
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
        { PublishPath, "'.modkit/tooling/scripts/Publish-VerifiedRelease.ps1'", "'.modkit/tooling/scripts/Test-Candidate.ps1'", "missing shared mod Release publisher" },
        { ReferenceReleasePath, "'scripts/Publish-VerifiedRelease.ps1'", "'scripts/Test-Candidate.ps1'", "missing shared reference Release publisher" },
        { PublishPath, "-ExpectedTagObject $env:EXPECTED_TAG_OBJECT", "-ExpectedTagObject 1111111111111111111111111111111111111111", "mod publisher tag object not bound" },
        { ReferenceReleasePath, "-ExpectedTagObject $env:EXPECTED_TAG_OBJECT", "-ExpectedTagObject 1111111111111111111111111111111111111111", "reference publisher tag object not bound" },
        { PublishPath, "-ExpectedCommit $env:EXPECTED_COMMIT", "-ExpectedCommit 2222222222222222222222222222222222222222", "mod publisher commit not bound" },
        { ReferenceReleasePath, "-ExpectedCommit $env:EXPECTED_COMMIT", "-ExpectedCommit 2222222222222222222222222222222222222222", "reference publisher commit not bound" },
        { PublishPath, "[string]$run.head_branch -cne 'main'", "[string]$run.head_branch -cne 'develop'", "candidate not from main" },
        { PublishPath, "[string]$run.path -cne '.github/workflows/ci.yml'", "[string]$run.path -cne '.github/workflows/release.yml'", "candidate from a different workflow" },
        { PublishPath, "[long]$run.workflow_id -ne [long]$workflow.id", "[long]$run.workflow_id -le 0", "candidate workflow id not bound to registered CI" },
        { PublishPath, "[string]$remoteRef.object.sha -cne $tagObject", "[string]$remoteRef.object.sha -cnotmatch '^[0-9a-f]{40}$'", "remote tag object not bound to local tag object" },
        { PublishPath, "$tagType[0].Trim() -cne 'tag'", "$tagType[0].Trim() -cne 'commit'", "lightweight tag accepted" },
        { PublishPath, "$lines.Count -ne 4", "$lines.Count -lt 4", "duplicate annotated field accepted" },
        { PublishPath, "^Candidate-Run-Id: ([1-9][0-9]*)$", "^Candidate-Run-Id: (.*)$", "malformed run id" },
        { PublishPath, "Candidate-Artifact-Digest: (sha256:[0-9a-f]{64})", "Candidate-Artifact-Digest: (sha256:.*)", "malformed tag digest" },
        { PublishPath, "\"tag-object=$tagObject\" >> $env:GITHUB_OUTPUT", "\"tag-object=\" >> $env:GITHUB_OUTPUT", "empty mod tag object output" },
        { PublishPath, "\"workflow-id=$([long]$workflow.id)\" >> $env:GITHUB_OUTPUT", "\"workflow-id=\" >> $env:GITHUB_OUTPUT", "empty mod workflow id output" },
        { ReferenceReleasePath, "\"tag-object=$tagObject\" >> $env:GITHUB_OUTPUT", "\"tag-object=\" >> $env:GITHUB_OUTPUT", "empty reference tag object output" },
        { ReferenceReleasePath, "\"workflow-id=$([long]$workflow.id)\" >> $env:GITHUB_OUTPUT", "\"workflow-id=\" >> $env:GITHUB_OUTPUT", "empty reference workflow id output" },
        { ReferenceReleasePath, "-CandidateKind Reference", "-CandidateKind Mod", "wrong reference candidate kind" },
        { PublishPath, "-CandidateKind Mod", "-CandidateKind Reference", "wrong mod candidate kind" },
        { ReferenceReleasePath, "scripts/Pack-GameApiRef.ps1", "dotnet pack", "noncanonical reference writer" },
        { ReferenceReleasePath, "    needs: [prepare, verify, full-tests]", "    needs: [prepare, verify]", "release publication ignores full tests" },
        { ReferenceReleasePath, "          - shard: 6\n            filter: ReleaseShard=6", "          - shard: missing\n            filter: ReleaseShard=missing", "release test shard missing" },
        { ReferenceReleasePath, "filter: ReleaseShard!=1&ReleaseShard!=2&ReleaseShard!=3&ReleaseShard!=4&ReleaseShard!=5&ReleaseShard!=6", "filter: ReleaseShard!=1&ReleaseShard!=2&ReleaseShard!=3&ReleaseShard!=4&ReleaseShard!=5", "release remainder overlaps a shard" },
        { ReferenceReleasePath, "--filter $env:TEST_FILTER", "--filter 'Category!=LongRunning'", "release skipped long-running tests" },
        { ReferenceReleasePath, "    timeout-minutes: 30", "    timeout-minutes: 31", "release job timeout weakened" },
        { ReferenceReleasePath, "      - name: Set up locked .NET SDK\n        uses: " + SetupDotNet + "\n        with:\n          global-json-file: global.json\n", "", "release job lacks locked SDK" },
        { ReferenceReleasePath, "      - name: Receive and validate frozen reference candidate again\n        shell: pwsh\n        env:\n          GH_TOKEN: ${{ github.token }}\n          EXPECTED_COMMIT: ${{ needs.prepare.outputs.commit }}", "      - name: Receive and validate frozen reference candidate again\n        shell: pwsh\n        env:\n          GH_TOKEN: ${{ github.token }}\n          EXPECTED_COMMIT: 2222222222222222222222222222222222222222", "reference publish receiver uses hardcoded commit" },
        { CiPath, "dotnet restore 'FarmTogether2-ModKit.sln' --locked-mode", "dotnet restore 'FarmTogether2-ModKit.sln'", "unlocked restore" },
        { CiPath, "-warnaserror", "", "warnings not errors" },
        { CiPath, "--filter 'Category!=LongRunning'", "--filter 'Category=LongRunning'", "long-running tests required on every change" },
        { CiPath, "dotnet 'tools/FarmTogether2.ModKit.Tool/bin/Release/net10.0/FarmTogether2.ModKit.Tool.dll'", "dotnet run --project 'tools/FarmTogether2.ModKit.Tool/FarmTogether2.ModKit.Tool.csproj'", "CI restarts MSBuild to inspect the package" },
        { CiPath, "          & 'tests/WorkflowContract.Tests/Test-CallerWorkflows.ps1' `\n            -RepositoryRoot 'tests/fixtures/mod-repository'", "          & 'tests/WorkflowContract.Tests/Test-CallerWorkflows.ps1' `\n            -RepositoryRoot 'tests/fixtures/mod-repository'\n          if ($LASTEXITCODE -ne 0) { throw 'Caller workflow validation failed.' }", "PowerShell caller validation checked an undefined native exit code" },
        { CiPath, "      - name: Validate generated caller workflows\n        shell: pwsh", "      - name: Validate generated caller workflows\n        shell: bash", "caller workflow validation shell" },
        { CiPath, "      - name: Check repository diff and leakage policy", "      - name: Duplicate caller workflow validation\n        shell: pwsh\n        run: |\n          & 'tests/WorkflowContract.Tests/Test-CallerWorkflows.ps1' `\n            -RepositoryRoot 'tests/fixtures/mod-repository'\n      - name: Check repository diff and leakage policy", "duplicate caller workflow validation" },
        { CiPath, "scripts/Test-Repository.ps1", "scripts/Test-Candidate.ps1", "missing leakage scan" },
        { DependabotPath, "interval: daily", "interval: weekly", "non-daily Dependabot" },
        { DependabotPath, "package-ecosystem: nuget", "package-ecosystem: npm", "extra Dependabot ecosystem" },
        { DependabotPath, "      - /tests/fixtures/mod-repository", "      - /tests/fixtures/missing", "missing fixture SDK directory" },
        { DependabotPath, "        group-by: dependency-name", "        group-by: update-type", "different SDK grouping" },
        { DependabotAutoMergePath, DependabotMetadata, "dependabot/fetch-metadata@main", "movable Dependabot metadata action" },
        { DependabotAutoMergePath, "user.login == 'dependabot[bot]'", "user.login == 'attacker'", "different auto-merge actor" },
        { DependabotAutoMergePath, "version-update:semver-minor", "version-update:semver-major", "major auto-merge" },
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
        List<object> scripts = [];
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
                    scripts.Add(new { Label = $"{path} job {jobName} step {index}", Script = script });
                }
            }
        }
        scripts.Add(new
        {
            Label = ReleasePublisherPath,
            Script = File.ReadAllText(Path.Combine(Root, ReleasePublisherPath.Replace('/', Path.DirectorySeparatorChar)))
        });

        string manifest = Path.Combine(Path.GetTempPath(), $"workflow-powershell-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(manifest, JsonSerializer.Serialize(scripts));
            ProcessStartInfo startInfo = new("pwsh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (string argument in new[]
            {
                "-NoLogo", "-NoProfile", "-Command",
                "$failed = $false; foreach ($item in (Get-Content -LiteralPath $env:WORKFLOW_SCRIPTS -Raw | ConvertFrom-Json)) { $tokens = $null; $errors = $null; [void][System.Management.Automation.Language.Parser]::ParseInput($item.Script, [ref]$tokens, [ref]$errors); if ($errors.Count -ne 0) { [Console]::Error.WriteLine($item.Label); $errors | ForEach-Object { [Console]::Error.WriteLine($_.Message) }; $failed = $true } }; if ($failed) { exit 1 }"
            }) startInfo.ArgumentList.Add(argument);
            startInfo.Environment["WORKFLOW_SCRIPTS"] = manifest;
            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start pwsh parser.");
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, $"PowerShell parse failed.\nstdout:\n{stdout}\nstderr:\n{stderr}");
        }
        finally
        {
            File.Delete(manifest);
        }
    }

    [Fact]
    public void CiCallerWorkflowValidationSucceedsInFreshPowerShell()
    {
        YamlMappingNode root = ParseYaml(LoadFiles()[CiPath], CiPath);
        YamlMappingNode verify = Mapping(Mapping(root, "jobs", CiPath), "verify", CiPath);
        YamlMappingNode[] steps = Sequence(verify, "steps", CiPath).Children
            .Select((step, index) => AsMapping(step, $"{CiPath} verify step {index}"))
            .Where(step => OptionalScalar(step, "name") == "Validate generated caller workflows")
            .ToArray();
        YamlMappingNode step = Assert.Single(steps);
        string script = Scalar(step, "run", CiPath);

        ProcessStartInfo startInfo = new("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Root
        };
        foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script })
            startInfo.ArgumentList.Add(argument);
        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start pwsh.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, $"Caller workflow validation failed in a fresh PowerShell process.\nstdout:\n{stdout}\nstderr:\n{stderr}");
        Assert.Contains("[caller-workflows] OK", stdout, StringComparison.Ordinal);
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
        string[] paths = [CiPath, ReferenceReleasePath, BuildPath, PublishPath, DependabotAutoMergePath, DependabotPath, CallerCiPath, CallerReleasePath];
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
        ValidateDependabotAutoMerge(documents[DependabotAutoMergePath]);
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
        HashSet<string> officialActions = [Checkout, SetupDotNet, UploadArtifact, DownloadArtifact, DependabotMetadata];
        foreach ((string path, string text) in files)
        {
            YamlMappingNode document = documents[path];
            if (TryGet(document, "on", out YamlNode? triggers))
                Require(!ContainsScalar(triggers!, "pull_request_target") || path == DependabotAutoMergePath, $"{path} uses pull_request_target.");
            Require(!ContainsMappingEntry(document, "secrets", "inherit"), $"{path} inherits secrets.");
            Require(!Regex.IsMatch(text, @"(?i)\bgh\s+run\s+download\b"), $"{path} uses gh run download.");
            Require(!Regex.IsMatch(text, @"(?i)Expand-Archive|\b7z(?:\.exe)?\b"), $"{path} manually extracts candidate evidence.");
            Require(!Regex.IsMatch(text, @"(?im)^\s*(?:-\s*)?uses\s*:\s*(?:softprops/|ncipollo/|marvinpinto/)", RegexOptions.CultureInvariant), $"{path} uses a third-party Release action.");

            foreach (Match match in Regex.Matches(text, @"(?im)^\s*(?:-\s*)?uses\s*:\s*([^\s#]+)\s*$"))
            {
                string value = match.Groups[1].Value;
                if (path == CallerCiPath || path == CallerReleasePath)
                {
                    Require(Regex.IsMatch(value, @"^abmcar-ft2-mods/FarmTogether2-ModKit/\.github/workflows/reusable-mod-(?:build|publish)\.yml@[0-9a-f]{40}$"), $"{path} has a movable caller use.");
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
                bool requiresSdk = path != ReferenceReleasePath || jobName != "prepare";
                ValidateSdkAndGhSteps(job, $"{path} job {jobName}", requiresSdk);
                ValidateCheckoutTokens(job, $"{path} job {jobName}");
            }
        }
    }

    private static void ValidateCheckoutTokens(YamlMappingNode job, string label)
    {
        foreach (YamlMappingNode step in Sequence(job, "steps", label).Children
            .Select((node, index) => AsMapping(node, $"{label} step {index}"))
            .Where(step => OptionalScalar(step, "uses") == Checkout))
        {
            YamlMappingNode with = Mapping(step, "with", label, required: false);
            string? token = OptionalScalar(with, "token");
            if (token is null)
                continue;
            Require(OptionalScalar(with, "repository") == "abmcar-ft2-mods/FarmTogether2-ModKit" &&
                    token == "${{ secrets.modkit_read_token }}",
                $"{label} may pass the private ModKit read token only to the private bootstrap checkout.");
        }
    }

    private static void ValidateSdkAndGhSteps(
        YamlMappingNode job,
        string label,
        bool requiresSdk)
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
                Require(Scalar(with, "global-json-file", label) == "global.json",
                    $"{label} setup-dotnet must use global.json.");
                Require(with.Children.Count == 1,
                    $"{label} setup-dotnet must not download a redundant auxiliary SDK.");
            }
            if (run is not null &&
                (Regex.IsMatch(run, @"(?i)\bgh\s") || run.Contains("Resolve-ModKit.ps1", StringComparison.Ordinal) ||
                    run.Contains("Receive-VerifiedArtifact.ps1", StringComparison.Ordinal) || run.Contains("Publish-VerifiedRelease.ps1", StringComparison.Ordinal)))
            {
                YamlMappingNode env = Mapping(step, "env", label);
                bool resolvesPrivateModKit = run.Contains("Resolve-ModKit.ps1", StringComparison.Ordinal);
                string expectedToken = resolvesPrivateModKit ? "${{ secrets.modkit_read_token }}" : "${{ github.token }}";
                Require(Scalar(env, "GH_TOKEN", label) == expectedToken,
                    $"{label} gh boundary uses the wrong repository token.");
                if (resolvesPrivateModKit)
                {
                    Require(Regex.IsMatch(run,
                            @"(?m)^\s*& gh auth setup-git --hostname github\.com\s*\n\s*if \(\$LASTEXITCODE -ne 0\) \{",
                            RegexOptions.CultureInvariant),
                        $"{label} resolver must configure private Git authentication and immediately check its exit.");
                    Require(!run.Contains("Receive-VerifiedArtifact.ps1", StringComparison.Ordinal) &&
                            !run.Contains("Publish-VerifiedRelease.ps1", StringComparison.Ordinal),
                        $"{label} may not mix private ModKit resolution with caller-repository operations.");
                }
            }
        }
        if (!requiresSdk)
        {
            Require(setupIndex < 0, $"{label} must not install an unused .NET SDK.");
            return;
        }

        Require(setupIndex >= 0 && setupIndex + 1 < steps.Children.Count, $"{label} lacks setup-dotnet and an immediate assertion.");
        YamlMappingNode assertion = AsMapping(steps.Children[setupIndex + 1], $"{label} SDK assertion");
        string assertionScript = Scalar(assertion, "run", label);
        Require(assertionScript.Contains("$requiredVersion = (Get-Content -LiteralPath 'global.json' -Raw | ConvertFrom-Json).sdk.version", StringComparison.Ordinal) &&
                Regex.IsMatch(assertionScript, @"\(dotnet --version\)\.Trim\(\) -cne \$requiredVersion", RegexOptions.CultureInvariant) &&
                assertionScript.Contains("Exact .NET SDK $requiredVersion is required.", StringComparison.Ordinal),
            $"{label} lacks a global.json-bound SDK assertion.");
    }

    private static void ValidateDependabot(YamlMappingNode root)
    {
        Require(Scalar(root, "version", DependabotPath) == "2", "Dependabot version must be 2.");
        YamlSequenceNode updates = Sequence(root, "updates", DependabotPath);
        Require(updates.Children.Count == 3, "Dependabot must contain exactly three ecosystems.");
        string[] expected = ["github-actions", "nuget", "dotnet-sdk"];
        for (int index = 0; index < updates.Children.Count; index++)
        {
            YamlMappingNode update = AsMapping(updates.Children[index], "Dependabot update");
            string ecosystem = Scalar(update, "package-ecosystem", DependabotPath);
            Require(ecosystem == expected[index], "Dependabot ecosystem or order differs.");
            Require(Scalar(Mapping(update, "schedule", DependabotPath), "interval", DependabotPath) == "daily", "Dependabot must run daily.");

            if (ecosystem == "dotnet-sdk")
            {
                Require(!TryGet(update, "directory", out _), "Dependabot SDK may not use a single directory.");
                YamlSequenceNode directories = Sequence(update, "directories", DependabotPath);
                Require(directories.Children.Count == 2 &&
                        ScalarEquals(directories.Children[0], "/") &&
                        ScalarEquals(directories.Children[1], "/tests/fixtures/mod-repository"),
                    "Dependabot SDK directories differ.");
                YamlMappingNode groups = Mapping(update, "groups", DependabotPath);
                Require(groups.Children.Count == 1, "Dependabot SDK must contain exactly one group.");
                YamlMappingNode group = Mapping(groups, "dotnet-sdk", DependabotPath);
                Require(group.Children.Count == 1 && Scalar(group, "group-by", DependabotPath) == "dependency-name",
                    "Dependabot SDK updates must group by dependency name.");
            }
            else
            {
                Require(!TryGet(update, "directories", out _), "Non-SDK Dependabot updates may not use multiple directories.");
                Require(Scalar(update, "directory", DependabotPath) == "/", "Dependabot directory must be repository root.");
            }

            if (ecosystem == "github-actions")
            {
                YamlSequenceNode patterns = Sequence(Mapping(Mapping(update, "groups", DependabotPath), "github-actions", DependabotPath), "patterns", DependabotPath);
                Require(patterns.Children.Count == 1 && ScalarEquals(patterns.Children[0], "*"), "GitHub Actions must update as one group.");
            }
            else if (ecosystem == "nuget")
            {
                YamlSequenceNode patterns = Sequence(Mapping(Mapping(update, "groups", DependabotPath), "test-tooling", DependabotPath), "patterns", DependabotPath);
                HashSet<string?> expectedPatterns = ["Microsoft.NET.Test.Sdk", "xunit*", "YamlDotNet"];
                Require(patterns.Children.Count == expectedPatterns.Count &&
                        patterns.Children.All(pattern => pattern is YamlScalarNode scalar && expectedPatterns.Contains(scalar.Value)),
                    "NuGet test tooling group differs.");

                YamlSequenceNode ignored = Sequence(update, "ignore", DependabotPath);
                HashSet<string> ignoredNames = ignored.Children
                    .Select((entry, ignoredIndex) => Scalar(AsMapping(entry, $"Dependabot ignore {ignoredIndex}"), "dependency-name", DependabotPath))
                    .ToHashSet(StringComparer.Ordinal);
                Require(ignoredNames.SetEquals(["Il2CppInterop.Common", "Il2CppInterop.Runtime"]), "Dependabot must preserve the host-matched Il2CppInterop pins.");
            }
        }
    }

    private static void ValidateDependabotAutoMerge(YamlMappingNode root)
    {
        RequirePermissions(root, new Dictionary<string, string>
        {
            ["contents"] = "write",
            ["pull-requests"] = "write"
        }, DependabotAutoMergePath);
        YamlMappingNode triggers = Mapping(root, "on", DependabotAutoMergePath);
        Require(triggers.Children.Count == 1, "Dependabot auto-merge must have one trigger.");
        YamlSequenceNode types = Sequence(Mapping(triggers, "pull_request_target", DependabotAutoMergePath), "types", DependabotAutoMergePath);
        Require(types.Children.Count == 3 && new[] { "opened", "synchronize", "reopened" }.All(expected => types.Children.Any(type => ScalarEquals(type, expected))),
            "Dependabot auto-merge trigger types differ.");

        YamlMappingNode jobs = Mapping(root, "jobs", DependabotAutoMergePath);
        Require(jobs.Children.Count == 1, "Dependabot auto-merge must have one job.");
        YamlMappingNode job = Mapping(jobs, "enable-auto-merge", DependabotAutoMergePath);
        Require(Scalar(job, "runs-on", DependabotAutoMergePath) == "ubuntu-latest", "Dependabot auto-merge runner differs.");
        Require(Scalar(job, "if", DependabotAutoMergePath) == "github.event.pull_request.user.login == 'dependabot[bot]' && github.repository == 'abmcar-ft2-mods/FarmTogether2-ModKit'",
            "Dependabot auto-merge actor or repository guard differs.");

        YamlSequenceNode steps = Sequence(job, "steps", DependabotAutoMergePath);
        Require(steps.Children.Count == 2, "Dependabot auto-merge must have two steps.");
        YamlMappingNode metadata = AsMapping(steps.Children[0], "Dependabot metadata step");
        Require(Scalar(metadata, "id", DependabotAutoMergePath) == "dependabot" && Scalar(metadata, "uses", DependabotAutoMergePath) == DependabotMetadata,
            "Dependabot metadata step differs.");
        YamlMappingNode merge = AsMapping(steps.Children[1], "Dependabot merge step");
        Require(Scalar(merge, "if", DependabotAutoMergePath) ==
                "steps.dependabot.outputs.update-type == 'version-update:semver-patch' || steps.dependabot.outputs.update-type == 'version-update:semver-minor'",
            "Dependabot auto-merge must accept only patch and minor updates.");
        Require(Scalar(merge, "run", DependabotAutoMergePath) == "gh pr merge --auto --squash \"$PR_URL\"", "Dependabot auto-merge command differs.");
        YamlMappingNode env = Mapping(merge, "env", DependabotAutoMergePath);
        Require(env.Children.Count == 2 && Scalar(env, "GH_TOKEN", DependabotAutoMergePath) == "${{ github.token }}" &&
                Scalar(env, "PR_URL", DependabotAutoMergePath) == "${{ github.event.pull_request.html_url }}",
            "Dependabot auto-merge environment differs.");
    }

    private static void ValidateCi(string text, YamlMappingNode root)
    {
        RequirePermissions(root, new Dictionary<string, string> { ["contents"] = "read" }, CiPath);
        YamlMappingNode verify = Mapping(Mapping(root, "jobs", CiPath), "verify", CiPath);
        string verifyScripts = JoinScripts(verify, "CI verify");
        Require(verifyScripts.Contains("dotnet restore 'FarmTogether2-ModKit.sln' --locked-mode", StringComparison.Ordinal), "CI restore must be locked.");
        Require(verifyScripts.Contains("-warnaserror", StringComparison.Ordinal), "CI build must treat warnings as errors.");
        Require(verifyScripts.Contains("dotnet test --project 'tests/FarmTogether2.ModKit.Tests/FarmTogether2.ModKit.Tests.csproj' -c Release --no-build --no-restore --filter 'Category!=LongRunning'", StringComparison.Ordinal),
            "CI must run the required test profile.");
        Require(verifyScripts.Contains("dotnet 'tools/FarmTogether2.ModKit.Tool/bin/Release/net10.0/FarmTogether2.ModKit.Tool.dll'", StringComparison.Ordinal) &&
                !Regex.IsMatch(verifyScripts, @"(?i)\bdotnet\s+run\b", RegexOptions.CultureInvariant),
            "CI must inspect the reference package with the already-built tool.");
        Require(verifyScripts.Contains("scripts/Pack-GameApiRef.ps1", StringComparison.Ordinal) &&
                verifyScripts.Contains("ref-package verify", StringComparison.Ordinal),
            "CI must write and inspect the reference package.");
        YamlMappingNode[] callerValidationSteps = Sequence(verify, "steps", CiPath).Children
            .Select((step, index) => AsMapping(step, $"{CiPath} verify step {index}"))
            .Where(step => OptionalScalar(step, "run")?.Contains(
                "tests/WorkflowContract.Tests/Test-CallerWorkflows.ps1",
                StringComparison.Ordinal) == true)
            .ToArray();
        Require(callerValidationSteps.Length == 1, "CI must contain exactly one caller workflow validation step.");
        Require(OptionalScalar(callerValidationSteps[0], "name") == "Validate generated caller workflows", "CI caller workflow validation step has the wrong name.");
        Require(OptionalScalar(callerValidationSteps[0], "shell") == "pwsh", "CI caller workflow validation must use pwsh.");
        string callerValidation = Scalar(callerValidationSteps[0], "run", CiPath);
        Require(!callerValidation.Contains("$LASTEXITCODE", StringComparison.Ordinal), "CI must not inspect native exit state after a PowerShell script.");
        Require(verifyScripts.Contains("git diff --check", StringComparison.Ordinal) &&
                verifyScripts.Contains("scripts/Test-Repository.ps1", StringComparison.Ordinal),
            "CI must run diff and leakage checks.");
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
        RequireWorkflowCallSecret(call, BuildPath);
        YamlMappingNode build = Mapping(Mapping(root, "jobs", BuildPath), "build", BuildPath);
        RequirePermissions(build, new Dictionary<string, string> { ["contents"] = "read" }, BuildPath + " build");
        ValidatePrivateBootstrapCheckout(build, BuildPath + " build");
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
        RequireWorkflowCallSecret(call, PublishPath);
        YamlMappingNode jobs = Mapping(root, "jobs", PublishPath);
        YamlMappingNode verify = Mapping(jobs, "verify", PublishPath);
        YamlMappingNode publish = Mapping(jobs, "publish", PublishPath);
        RequirePermissions(verify, new Dictionary<string, string> { ["actions"] = "read", ["contents"] = "read" }, "mod verify");
        RequirePermissions(publish, new Dictionary<string, string> { ["actions"] = "read", ["attestations"] = "read", ["contents"] = "write" }, "mod publish");
        Require(Scalar(publish, "needs", PublishPath) == "verify", "Mod publish must depend on verify.");
        ValidateAnnotatedEvidence(text, "Mod", "verify");
        ValidatePublishJob(verify, "verify", isReference: false);
        ValidatePublishJob(publish, "publish", isReference: false);
        string verifyScripts = JoinScripts(verify, "mod verify");
        AssertOrder(verifyScripts, "Receive-VerifiedArtifact.ps1", "Test-Candidate.ps1", "Invoke-ModBuild.ps1", "Pack-Mod.ps1", "Test-PublishedAssets.ps1");
        string publishScripts = JoinScripts(publish, "mod publish");
        int receiver = publishScripts.IndexOf("Receive-VerifiedArtifact.ps1", StringComparison.Ordinal);
        int publisher = publishScripts.IndexOf(".modkit/tooling/scripts/Publish-VerifiedRelease.ps1", StringComparison.Ordinal);
        Require(receiver >= 0 && publisher > receiver, "Mod Release publication must follow the publish job receiver.");
        ValidateDraftRelease(publishScripts, ".modkit/tooling/scripts/Publish-VerifiedRelease.ps1", "Mod");
    }

    private static void ValidateReferencePublish(string text)
    {
        YamlMappingNode root = ParseYaml(text, ReferenceReleasePath);
        RequirePermissions(root, new Dictionary<string, string>(), ReferenceReleasePath);
        YamlMappingNode jobs = Mapping(root, "jobs", ReferenceReleasePath);
        YamlMappingNode prepare = Mapping(jobs, "prepare", ReferenceReleasePath);
        YamlMappingNode verify = Mapping(jobs, "verify", ReferenceReleasePath);
        YamlMappingNode fullTests = Mapping(jobs, "full-tests", ReferenceReleasePath);
        YamlMappingNode publish = Mapping(jobs, "publish", ReferenceReleasePath);
        RequirePermissions(prepare, new Dictionary<string, string> { ["actions"] = "read", ["contents"] = "read" }, "reference prepare");
        RequirePermissions(verify, new Dictionary<string, string> { ["actions"] = "read", ["contents"] = "read" }, "reference verify");
        RequirePermissions(fullTests, new Dictionary<string, string> { ["contents"] = "read" }, "reference full tests");
        RequirePermissions(publish, new Dictionary<string, string> { ["actions"] = "read", ["attestations"] = "read", ["contents"] = "write" }, "reference publish");
        Require(Scalar(verify, "timeout-minutes", ReferenceReleasePath) == "30" &&
                Scalar(fullTests, "timeout-minutes", ReferenceReleasePath) == "30" &&
                Scalar(publish, "timeout-minutes", ReferenceReleasePath) == "30",
            "Reference rebuild, full-test shards, and publication must fail closed after 30 minutes.");
        Require(Scalar(verify, "needs", ReferenceReleasePath) == "prepare", "Reference verify must depend on prepare.");
        Require(Scalar(fullTests, "needs", ReferenceReleasePath) == "prepare", "Reference full tests must depend on prepare.");
        HashSet<string> publishNeeds = Sequence(publish, "needs", ReferenceReleasePath).Children
            .Select(node => ((YamlScalarNode)node).Value ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);
        Require(publishNeeds.SetEquals(["prepare", "verify", "full-tests"]), "Reference publish must depend on prepare, verify, and every full-test shard.");
        ValidateAnnotatedEvidence(text, "Reference", "prepare");
        ValidateReferencePrepare(prepare);
        ValidateReferenceVerification(verify);
        ValidateReferenceFullTests(fullTests);
        ValidateReleaseShardAssignments();
        ValidatePublishJob(publish, "publish", isReference: true);
        string verifyScripts = JoinScripts(verify, "reference verify");
        AssertOrder(verifyScripts, "Receive-VerifiedArtifact.ps1", "Test-Candidate.ps1", "Pack-GameApiRef.ps1", "Test-PublishedAssets.ps1");
        Require(verifyScripts.Contains("dotnet restore 'FarmTogether2-ModKit.sln' --locked-mode", StringComparison.Ordinal) &&
                verifyScripts.Contains("dotnet build 'FarmTogether2-ModKit.sln' -c Release --no-restore -warnaserror", StringComparison.Ordinal) &&
                !Regex.IsMatch(verifyScripts, @"(?i)\bdotnet\s+test\b", RegexOptions.CultureInvariant),
            "Reference verify must independently rebuild the tag commit without serializing the full tests behind it.");
        string publishScripts = JoinScripts(publish, "reference publish");
        int receiver = publishScripts.IndexOf("Receive-VerifiedArtifact.ps1", StringComparison.Ordinal);
        int publisher = publishScripts.IndexOf("scripts/Publish-VerifiedRelease.ps1", StringComparison.Ordinal);
        Require(receiver >= 0 && publisher > receiver, "Reference Release publication must follow the publish job receiver.");
        ValidateDraftRelease(publishScripts, "scripts/Publish-VerifiedRelease.ps1", "Reference");
    }

    private static void ValidateReferencePrepare(YamlMappingNode prepare)
    {
        const string label = "reference prepare";
        YamlMappingNode outputs = Mapping(prepare, "outputs", label);
        Dictionary<string, string> expectedOutputs = new(StringComparer.Ordinal)
        {
            ["tag"] = "${{ steps.evidence.outputs.tag }}",
            ["tag-object"] = "${{ steps.evidence.outputs.tag-object }}",
            ["commit"] = "${{ steps.evidence.outputs.commit }}",
            ["workflow-id"] = "${{ steps.evidence.outputs.workflow-id }}",
            ["run-id"] = "${{ steps.evidence.outputs.run-id }}",
            ["artifact-id"] = "${{ steps.evidence.outputs.artifact-id }}",
            ["artifact-name"] = "${{ steps.evidence.outputs.artifact-name }}",
            ["artifact-digest"] = "${{ steps.evidence.outputs.artifact-digest }}"
        };
        Require(outputs.Children.Count == expectedOutputs.Count, "Reference prepare outputs differ.");
        foreach ((string name, string value) in expectedOutputs)
            Require(Scalar(outputs, name, label) == value, $"Reference prepare output differs: {name}.");

        YamlMappingNode[] steps = Sequence(prepare, "steps", label).Children
            .Select((step, index) => AsMapping(step, $"{label} step {index}"))
            .ToArray();
        YamlMappingNode[] checkouts = steps.Where(step => OptionalScalar(step, "uses") == Checkout).ToArray();
        Require(checkouts.Length == 1, "Reference prepare needs exactly one tag checkout.");
        YamlMappingNode with = Mapping(checkouts[0], "with", label);
        Require(Scalar(with, "ref", label) == "${{ steps.request.outputs.tag }}" &&
                Scalar(with, "fetch-depth", label) == "0" &&
                Scalar(with, "persist-credentials", label) == "false",
            "Reference prepare must check out the requested tag with full history.");
        Require(steps.Count(step => OptionalScalar(step, "id") == "evidence") == 1, "Reference prepare needs exactly one evidence step.");
        string scripts = JoinScripts(prepare, label);
        Require(scripts.Contains("actions/workflows/ci.yml", StringComparison.Ordinal) &&
                scripts.Contains("actions/artifacts/$artifactId", StringComparison.Ordinal) &&
                !scripts.Contains("Receive-VerifiedArtifact.ps1", StringComparison.Ordinal) &&
                !Regex.IsMatch(scripts, @"(?i)\bdotnet\b", RegexOptions.CultureInvariant),
            "Reference prepare must only freeze tag and candidate evidence.");
    }

    private static void ValidateReferenceVerification(YamlMappingNode verify)
    {
        const string label = "reference verify";
        ValidateVerifiedCommitCheckout(verify, label);
        YamlMappingNode[] steps = Sequence(verify, "steps", label).Children
            .Select((step, index) => AsMapping(step, $"{label} step {index}"))
            .ToArray();
        YamlMappingNode receiver = steps.SingleOrDefault(step => OptionalScalar(step, "name") == "Receive and validate frozen reference candidate")
            ?? throw new InvalidDataException("Reference verify needs one candidate receiver.");
        Require(!steps.Any(step => OptionalScalar(step, "uses") is string action &&
            action.StartsWith("actions/download-artifact@", StringComparison.Ordinal)),
            "Reference verify may not use actions/download-artifact.");
        YamlMappingNode receiverEnvironment = Mapping(receiver, "env", label);
        Dictionary<string, string> expectedEnvironment = new(StringComparer.Ordinal)
        {
            ["EXPECTED_COMMIT"] = "${{ needs.prepare.outputs.commit }}",
            ["EXPECTED_RUN_ID"] = "${{ needs.prepare.outputs.run-id }}",
            ["EXPECTED_ARTIFACT_ID"] = "${{ needs.prepare.outputs.artifact-id }}",
            ["EXPECTED_ARTIFACT_NAME"] = "${{ needs.prepare.outputs.artifact-name }}",
            ["EXPECTED_ARTIFACT_DIGEST"] = "${{ needs.prepare.outputs.artifact-digest }}"
        };
        foreach ((string name, string value) in expectedEnvironment)
            Require(Scalar(receiverEnvironment, name, label) == value, $"Reference verify environment differs: {name}.");
        string receiverScript = Scalar(receiver, "run", label);
        string scripts = JoinScripts(verify, label);
        Dictionary<string, int> bindings = new(StringComparer.Ordinal)
        {
            ["-ArtifactId $env:EXPECTED_ARTIFACT_ID"] = 1,
            ["-ExpectedArtifactName $env:EXPECTED_ARTIFACT_NAME"] = 2,
            ["-ExpectedRunId $env:EXPECTED_RUN_ID"] = 2,
            ["-ExpectedCommit $env:EXPECTED_COMMIT"] = 2,
            ["-ExpectedDigest $env:EXPECTED_ARTIFACT_DIGEST"] = 1,
            ["-CandidateKind Reference"] = 1
        };
        foreach ((string binding, int count) in bindings)
            Require(Regex.Matches(receiverScript, Regex.Escape(binding), RegexOptions.CultureInvariant).Count == count,
                $"Reference verify frozen evidence binding count differs: {binding}");
        Require(scripts.Contains("dotnet restore 'FarmTogether2-ModKit.sln' --locked-mode", StringComparison.Ordinal) &&
                scripts.Contains("dotnet build 'FarmTogether2-ModKit.sln' -c Release --no-restore -warnaserror", StringComparison.Ordinal) &&
                scripts.Contains("scripts/Pack-GameApiRef.ps1", StringComparison.Ordinal) &&
                scripts.Contains("scripts/Test-PublishedAssets.ps1", StringComparison.Ordinal),
            "Reference verify must rebuild and compare the reference package.");
    }

    private static void ValidateReferenceFullTests(YamlMappingNode fullTests)
    {
        const string label = "reference full tests";
        ValidateVerifiedCommitCheckout(fullTests, label);
        Require(Scalar(fullTests, "name", label) == "Full tests (${{ matrix.shard }})", "Reference full-test job name must expose its shard.");
        YamlMappingNode strategy = Mapping(fullTests, "strategy", label);
        Require(Scalar(strategy, "fail-fast", label) == "false", "Reference full-test shards must all finish.");
        YamlSequenceNode include = Sequence(Mapping(strategy, "matrix", label), "include", label);
        Require(include.Children.Count == 7, "Reference full-test matrix must have seven shards.");
        Dictionary<string, string> shards = include.Children
            .Select((entry, index) => AsMapping(entry, $"{label} shard {index}"))
            .ToDictionary(
                entry => Scalar(entry, "shard", label),
                entry => Scalar(entry, "filter", label),
                StringComparer.Ordinal);
        Dictionary<string, string> expected = new(StringComparer.Ordinal)
        {
            ["1"] = "ReleaseShard=1",
            ["2"] = "ReleaseShard=2",
            ["3"] = "ReleaseShard=3",
            ["4"] = "ReleaseShard=4",
            ["5"] = "ReleaseShard=5",
            ["6"] = "ReleaseShard=6",
            ["remainder"] = "ReleaseShard!=1&ReleaseShard!=2&ReleaseShard!=3&ReleaseShard!=4&ReleaseShard!=5&ReleaseShard!=6"
        };
        Require(shards.Count == expected.Count && expected.All(pair =>
                shards.TryGetValue(pair.Key, out string? filter) && filter == pair.Value),
            "Reference full-test shard filters differ.");

        YamlMappingNode[] steps = Sequence(fullTests, "steps", label).Children
            .Select((step, index) => AsMapping(step, $"{label} step {index}"))
            .ToArray();
        YamlMappingNode testStep = steps.SingleOrDefault(step => OptionalScalar(step, "name") == "Build and run full test shard")
            ?? throw new InvalidDataException("Reference full tests need one build-and-test step.");
        Require(Scalar(Mapping(testStep, "env", label), "TEST_FILTER", label) == "${{ matrix.filter }}",
            "Reference full tests must bind the matrix filter through the environment.");
        string script = Scalar(testStep, "run", label);
        Require(script.Contains("dotnet restore 'FarmTogether2-ModKit.sln' --locked-mode", StringComparison.Ordinal) &&
                script.Contains("dotnet build 'FarmTogether2-ModKit.sln' -c Release --no-restore -warnaserror", StringComparison.Ordinal) &&
                script.Contains("dotnet test --project 'tests/FarmTogether2.ModKit.Tests/FarmTogether2.ModKit.Tests.csproj'", StringComparison.Ordinal) &&
                Regex.Matches(script, @"(?i)\bdotnet\s+test\b", RegexOptions.CultureInvariant).Count == 1 &&
                script.Contains("--filter $env:TEST_FILTER", StringComparison.Ordinal) &&
                !script.Contains("Category!=LongRunning", StringComparison.Ordinal),
            "Reference full-test matrix must run every test through its assigned filter.");
    }

    private static void ValidateVerifiedCommitCheckout(YamlMappingNode job, string label)
    {
        YamlMappingNode[] checkouts = Sequence(job, "steps", label).Children
            .Select((step, index) => AsMapping(step, $"{label} step {index}"))
            .Where(step => OptionalScalar(step, "uses") == Checkout)
            .ToArray();
        Require(checkouts.Length == 1, $"{label} needs exactly one verified commit checkout.");
        YamlMappingNode with = Mapping(checkouts[0], "with", label);
        Require(Scalar(with, "ref", label) == "${{ needs.prepare.outputs.commit }}" &&
                Scalar(with, "fetch-depth", label) == "0" &&
                Scalar(with, "persist-credentials", label) == "false",
            $"{label} must check out the prepared commit with full history and no persisted credentials.");
    }

    private static void ValidateReleaseShardAssignments()
    {
        Dictionary<Type, string> expectedClasses = new()
        {
            [typeof(ModKitResolverTests)] = "5",
            [typeof(PackModScriptTests)] = "6"
        };
        Dictionary<(Type Type, string Method), string> expected = new()
        {
            [(typeof(GameSwitchTests), nameof(GameSwitchTests.CaptureRestoresAfterEveryOperationFailureThenRerunsToSuccess))] = "1",
            [(typeof(VerifiedReleasePublisherTests), nameof(VerifiedReleasePublisherTests.AttestationFailureCannotReturnSuccess))] = "1",
            [(typeof(VerifiedReleasePublisherTests), nameof(VerifiedReleasePublisherTests.NewReleaseIsPublishedByNumericIdAndAttestationsAreVerified))] = "1",
            [(typeof(GameSwitchTests), nameof(GameSwitchTests.EveryPersistedPhaseCanReenterRestoreAndConverge))] = "2",
            [(typeof(GameSwitchTests), nameof(GameSwitchTests.ActivateOldResumesEveryPhysicalAndJournalCrash))] = "2",
            [(typeof(VerifiedReleasePublisherTests), nameof(VerifiedReleasePublisherTests.ExistingPartialDraftUploadsOnlyMissingAssets))] = "2",
            [(typeof(VerifiedReleasePublisherTests), nameof(VerifiedReleasePublisherTests.ReferenceReleaseUsesTheSameVerifiedPublisher))] = "2",
            [(typeof(VerifiedReleasePublisherTests), nameof(VerifiedReleasePublisherTests.PublishedAssetMetadataMutationAfterPublicationIsRejected))] = "2",
            [(typeof(GameSwitchTests), nameof(GameSwitchTests.ActivateCurrentResumesEveryPhysicalAndJournalCrash))] = "3",
            [(typeof(GameSwitchTests), nameof(GameSwitchTests.RestoreResumesEveryPhysicalAndJournalCrash))] = "3",
            [(typeof(VerifiedReleasePublisherTests), nameof(VerifiedReleasePublisherTests.ExactTagOnSecondPageIsFoundWithoutCreatingAnotherRelease))] = "3",
            [(typeof(VerifiedReleasePublisherTests), nameof(VerifiedReleasePublisherTests.PublishedAssetByteMutationAfterPublicationIsRejected))] = "3",
            [(typeof(VerifiedReleasePublisherTests), nameof(VerifiedReleasePublisherTests.PublishedRerunSkipsMutationsAndReverifiesAttestations))] = "3",
            [(typeof(GameSwitchTests), nameof(GameSwitchTests.RestoreRejectsEveryOtherAppManifestIdentityOrByteChange))] = "4",
            [(typeof(GameSwitchTests), nameof(GameSwitchTests.LegacyActiveStateMigrationIsPersistedEvenWhenTheActionHasNoPhaseTransition))] = "4",
            [(typeof(GameSwitchTests), nameof(GameSwitchTests.ClosedJournalRejectsDuplicateJsonPropertiesAtEveryRelevantDepth))] = "4",
            [(typeof(GameSwitchTests), nameof(GameSwitchTests.ClosedJournalRejectsJsonValuesWithWrongTypesBeforeMutation))] = "4",
            [(typeof(GameSwitchTests), nameof(GameSwitchTests.CompletedSnapshotRejectsDuplicateOrWrongJsonTypes))] = "4"
        };
        HashSet<Type> seenClasses = [];
        HashSet<(Type Type, string Method)> seen = [];
        foreach (Type type in typeof(WorkflowContractTests).Assembly.GetTypes())
        {
            string[] classAssignments = GetReleaseShards(type);
            Require(classAssignments.Length <= 1, $"Test class has multiple release shards: {type.FullName}.");
            if (expectedClasses.TryGetValue(type, out string? expectedClassShard))
            {
                Require(classAssignments.Length == 1 && classAssignments[0] == expectedClassShard,
                    $"Release shard changed for test class {type.FullName}.");
                seenClasses.Add(type);
            }
            else
            {
                Require(classAssignments.Length == 0, $"Unexpected test class release shard: {type.FullName}.");
            }
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                string[] assignments = GetReleaseShards(method);
                Require(assignments.Length <= 1, $"Test method has multiple release shards: {type.FullName}.{method.Name}.");
                if (assignments.Length == 0)
                    continue;
                Require(classAssignments.Length == 0, $"Test cannot have class and method release shards: {type.FullName}.{method.Name}.");
                Require(assignments[0] is "1" or "2" or "3" or "4" or "5" or "6", $"Test method has an unknown release shard: {type.FullName}.{method.Name}.");
                (Type Type, string Method) key = (type, method.Name);
                Require(expected.TryGetValue(key, out string? shard), $"Unexpected test method release shard: {type.FullName}.{method.Name}.");
                Require(assignments[0] == shard, $"Release shard changed for {type.FullName}.{method.Name}.");
                seen.Add(key);
            }
        }
        Require(seenClasses.SetEquals(expectedClasses.Keys), "One or more release test classes lost their shard assignment.");
        Require(seen.SetEquals(expected.Keys), "One or more release hotspot methods lost their shard assignment.");
    }

    private static string[] GetReleaseShards(MemberInfo member) =>
        member.GetCustomAttributesData()
            .Where(attribute => attribute.AttributeType == typeof(TraitAttribute) &&
                attribute.ConstructorArguments.Count == 2 &&
                attribute.ConstructorArguments[0].Value is string name &&
                name == "ReleaseShard")
            .Select(attribute => attribute.ConstructorArguments[1].Value as string ?? string.Empty)
            .ToArray();

    private static void ValidateAnnotatedEvidence(string text, string kind, string evidenceJob)
    {
        string expectedTagObject = "${{ needs." + evidenceJob + ".outputs.tag-object }}";
        string expectedWorkflowId = "${{ needs." + evidenceJob + ".outputs.workflow-id }}";
        Require(text.Contains("cat-file -t", StringComparison.Ordinal) && text.Contains("-cne 'tag'", StringComparison.Ordinal), $"{kind} publish must reject lightweight tags.");
        Require(text.Contains("git/ref/tags/$env:REQUESTED_TAG", StringComparison.Ordinal) &&
                !text.Contains("git ls-remote", StringComparison.Ordinal), $"{kind} publish must verify private-repository tags through the authenticated API.");
        Require(text.Contains("tag-object: ${{ steps.evidence.outputs.tag-object }}", StringComparison.Ordinal) &&
                text.Contains($"EXPECTED_TAG_OBJECT: {expectedTagObject}", StringComparison.Ordinal) &&
                text.Contains("\"tag-object=$tagObject\" >> $env:GITHUB_OUTPUT", StringComparison.Ordinal), $"{kind} publish must carry the verified tag object across jobs.");
        Require(text.Contains("workflow-id: ${{ steps.evidence.outputs.workflow-id }}", StringComparison.Ordinal) &&
                text.Contains($"EXPECTED_WORKFLOW_ID: {expectedWorkflowId}", StringComparison.Ordinal) &&
                text.Contains("\"workflow-id=$([long]$workflow.id)\" >> $env:GITHUB_OUTPUT", StringComparison.Ordinal), $"{kind} publish must carry the verified CI workflow id across jobs.");
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
        Require(text.Contains("head_branch -cne 'main'", StringComparison.Ordinal) && text.Contains("status -cne 'completed'", StringComparison.Ordinal) &&
                text.Contains("conclusion -cne 'success'", StringComparison.Ordinal), $"{kind} publish must bind a completed successful main push.");
        Require(text.Contains("[string]$run.path -cne '.github/workflows/ci.yml'", StringComparison.Ordinal), $"{kind} publish must bind the candidate to the CI workflow path.");
        Require(text.Contains("actions/artifacts/$artifactId", StringComparison.Ordinal), $"{kind} verify must query the numeric artifact.");
        Require(text.Contains("actions/artifacts/$env:EXPECTED_ARTIFACT_ID", StringComparison.Ordinal), $"{kind} publish must re-query the same numeric artifact.");
        Require(text.Contains("artifact.workflow_run.id", StringComparison.Ordinal) && text.Contains("artifact.workflow_run.head_sha", StringComparison.Ordinal), $"{kind} publish must bind artifact run and commit.");
    }

    private static void ValidatePublishJob(YamlMappingNode job, string jobName, bool isReference)
    {
        string label = $"{(isReference ? "reference" : "mod")} {jobName}";
        YamlMappingNode[] steps = Sequence(job, "steps", label).Children
            .Select((step, index) => AsMapping(step, $"{label} step {index}"))
            .ToArray();
        string scripts = JoinScripts(job, label);
        Require(!steps.Any(step => OptionalScalar(step, "uses") is string action &&
            action.StartsWith("actions/download-artifact@", StringComparison.Ordinal)), $"{label} may not use actions/download-artifact.");
        YamlMappingNode[] tagCheckouts = steps
            .Where(step => OptionalScalar(step, "uses") == Checkout &&
                OptionalScalar(Mapping(step, "with", label, required: false), "path") is null)
            .ToArray();
        Require(tagCheckouts.Length == 1, $"{label} needs exactly one tag checkout.");
        YamlMappingNode tagCheckoutInputs = Mapping(tagCheckouts[0], "with", label);
        Require(!string.IsNullOrWhiteSpace(OptionalScalar(tagCheckoutInputs, "ref")), $"{label} tag checkout must select a ref.");
        Require(Scalar(tagCheckoutInputs, "fetch-depth", label) == "0", $"{label} tag checkout must fetch full history.");
        if (!isReference)
        {
            Require(CountInJob(job, step => OptionalScalar(step, "uses") == Checkout &&
                OptionalScalar(Mapping(step, "with", label, required: false), "path") == ".modkit/bootstrap") == 1, $"{label} needs one bootstrap checkout.");
            ValidatePrivateBootstrapCheckout(job, label);
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
        YamlMappingNode[] receiverSteps = steps
            .Where(step => OptionalScalar(step, "run")?.Contains(
                "Receive-VerifiedArtifact.ps1",
                StringComparison.Ordinal) == true)
            .ToArray();
        Require(receiverSteps.Length == 1, $"{label} needs exactly one receiver step.");
        string receiverScript = Scalar(receiverSteps[0], "run", label);
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
        if (jobName == "publish")
        {
            string evidenceJob = isReference ? "prepare" : "verify";
            Dictionary<string, string> expectedReceiverEnvironment = new(StringComparer.Ordinal)
            {
                ["EXPECTED_COMMIT"] = $"${{{{ needs.{evidenceJob}.outputs.commit }}}}",
                ["EXPECTED_RUN_ID"] = $"${{{{ needs.{evidenceJob}.outputs.run-id }}}}",
                ["EXPECTED_ARTIFACT_ID"] = $"${{{{ needs.{evidenceJob}.outputs.artifact-id }}}}",
                ["EXPECTED_ARTIFACT_NAME"] = $"${{{{ needs.{evidenceJob}.outputs.artifact-name }}}}",
                ["EXPECTED_ARTIFACT_DIGEST"] = $"${{{{ needs.{evidenceJob}.outputs.artifact-digest }}}}"
            };
            YamlMappingNode receiverEnvironment = Mapping(receiverSteps[0], "env", label);
            foreach ((string name, string value) in expectedReceiverEnvironment)
                Require(Scalar(receiverEnvironment, name, label) == value,
                    $"{label} receiver environment differs: {name}.");

            YamlMappingNode[] evidenceSteps = steps
                .Where(step => OptionalScalar(step, "run")?.Contains(
                    "actions/workflows/$expectedWorkflowId",
                    StringComparison.Ordinal) == true)
                .ToArray();
            Require(evidenceSteps.Length == 1, $"{label} needs exactly one frozen-evidence re-query step.");
            Dictionary<string, string> expectedEvidenceEnvironment = new(expectedReceiverEnvironment, StringComparer.Ordinal)
            {
                ["EXPECTED_TAG_OBJECT"] = $"${{{{ needs.{evidenceJob}.outputs.tag-object }}}}",
                ["EXPECTED_WORKFLOW_ID"] = $"${{{{ needs.{evidenceJob}.outputs.workflow-id }}}}"
            };
            YamlMappingNode evidenceEnvironment = Mapping(evidenceSteps[0], "env", label);
            foreach ((string name, string value) in expectedEvidenceEnvironment)
                Require(Scalar(evidenceEnvironment, name, label) == value,
                    $"{label} frozen-evidence environment differs: {name}.");

            YamlMappingNode[] publisherSteps = steps
                .Where(step => OptionalScalar(step, "run")?.Contains(
                    "Publish-VerifiedRelease.ps1",
                    StringComparison.Ordinal) == true)
                .ToArray();
            Require(publisherSteps.Length == 1, $"{label} needs exactly one verified publisher step.");
            YamlMappingNode publisherEnvironment = Mapping(publisherSteps[0], "env", label);
            Require(Scalar(publisherEnvironment, "RELEASE_TAG", label) ==
                    $"${{{{ needs.{evidenceJob}.outputs.tag }}}}" &&
                    Scalar(publisherEnvironment, "EXPECTED_TAG_OBJECT", label) ==
                    $"${{{{ needs.{evidenceJob}.outputs.tag-object }}}}" &&
                    Scalar(publisherEnvironment, "EXPECTED_COMMIT", label) ==
                    $"${{{{ needs.{evidenceJob}.outputs.commit }}}}",
                $"{label} publisher environment is not bound to frozen evidence.");
        }
        Require(scripts.Contains("$tagType[0].Trim() -cne 'tag'", StringComparison.Ordinal), $"{label} must reject lightweight tags.");
        Require(scripts.Contains("[string]$run.head_branch -cne 'main'", StringComparison.Ordinal), $"{label} must require a main-push candidate run.");
        Require(scripts.Contains("[string]$run.path -cne '.github/workflows/ci.yml'", StringComparison.Ordinal), $"{label} must require the repository CI workflow path.");
        if (jobName == "verify")
        {
            Require(scripts.Contains("$tagObject = @(& git rev-parse $tagRef)", StringComparison.Ordinal) &&
                    scripts.Contains("[string]$remoteRef.object.sha -cne $tagObject", StringComparison.Ordinal), $"{label} must bind the remote tag ref to the checked-out annotated tag object.");
            Require(scripts.Contains("actions/workflows/ci.yml", StringComparison.Ordinal) &&
                    scripts.Contains("[long]$run.workflow_id -ne [long]$workflow.id", StringComparison.Ordinal), $"{label} must bind the candidate run to the registered CI workflow id.");
        }
        else
        {
            Require(scripts.Contains("$tagObject[0].Trim() -cne $env:EXPECTED_TAG_OBJECT", StringComparison.Ordinal) &&
                    scripts.Contains("[string]$remoteRef.object.sha -cne $env:EXPECTED_TAG_OBJECT", StringComparison.Ordinal), $"{label} must retain the verified annotated tag object.");
            Require(scripts.Contains("actions/workflows/$expectedWorkflowId", StringComparison.Ordinal) &&
                    scripts.Contains("[long]$run.workflow_id -ne $expectedWorkflowId", StringComparison.Ordinal), $"{label} must re-query and retain the verified CI workflow id.");
        }
    }

    private static void ValidateDraftRelease(string workflowScripts, string expectedPublisher, string expectedKind)
    {
        Require(Regex.Matches(workflowScripts, "Publish-VerifiedRelease\\.ps1", RegexOptions.CultureInvariant).Count == 1 &&
                workflowScripts.Contains($"& '{expectedPublisher}'", StringComparison.Ordinal),
            "Publish job must invoke the shared verified Release publisher exactly once.");
        foreach (string binding in new[]
        {
            "-Repository $env:REPOSITORY", "-ReleaseTag $env:RELEASE_TAG",
            "-ExpectedTagObject $env:EXPECTED_TAG_OBJECT", "-ExpectedCommit $env:EXPECTED_COMMIT",
            "-CandidateDirectory 'artifacts/publish-candidate'", $"-CandidateKind {expectedKind}",
            "-WorkingDirectory 'artifacts/release-publication'"
        }) Require(workflowScripts.Contains(binding, StringComparison.Ordinal), $"Shared Release publisher invocation lacks binding: {binding}");
        Require(!Regex.IsMatch(workflowScripts, @"(?i)\bgh\s+release\s+(?:create|upload|edit|download|verify)", RegexOptions.CultureInvariant),
            "Workflow must not duplicate Release lifecycle commands outside the shared publisher.");

        string publisherPath = Path.Combine(Root, ReleasePublisherPath.Replace('/', Path.DirectorySeparatorChar));
        Require(File.Exists(publisherPath), "Shared verified Release publisher is missing.");
        string publisher = File.ReadAllText(publisherPath);
        Require(!publisher.Contains('\r'), "Shared verified Release publisher must use LF line endings.");
        Require(publisher.Contains("gh release create", StringComparison.Ordinal) &&
                publisher.Contains("--draft --verify-tag --generate-notes", StringComparison.Ordinal),
            "Release publisher must create missing Releases as verified drafts with generated notes.");
        Require(!publisher.Contains("gh release edit", StringComparison.Ordinal),
            "Release publisher must not publish by resolving a mutable tag name.");
        Require(publisher.Contains("Get-ReleaseByTagIncludingDrafts", StringComparison.Ordinal) &&
                publisher.Contains("--paginate --slurp", StringComparison.Ordinal) &&
                publisher.Contains("GetArrayLength() -eq 0", StringComparison.Ordinal) &&
                publisher.Contains("if ($null -eq $release)", StringComparison.Ordinal) &&
                publisher.Contains("Multiple Releases match the exact tag", StringComparison.Ordinal),
            "Release publisher must discover draft and published Releases from the complete authenticated list and create only after no exact tag match.");
        Require(publisher.Contains("Invoke-GitOneLine @('cat-file', '-t', $tagRef)", StringComparison.Ordinal) &&
                publisher.Contains("Invoke-GitOneLine @('rev-parse', '--verify', $tagRef)", StringComparison.Ordinal) &&
                publisher.Contains("Invoke-GitOneLine @('rev-parse', '--verify', \"$tagRef^{}\")", StringComparison.Ordinal) &&
                publisher.Contains("repos/$Repository/git/ref/tags/$ReleaseTag", StringComparison.Ordinal) &&
                publisher.Contains("repos/$Repository/git/tags/$ExpectedTagObject", StringComparison.Ordinal),
            "Release publisher must bind local and remote annotated tag objects to the verified commit.");
        Require(publisher.Contains("$exitCode = $LASTEXITCODE", StringComparison.Ordinal) &&
                publisher.IndexOf("$exitCode = $LASTEXITCODE", StringComparison.Ordinal) < publisher.IndexOf("$exitCode -ne 0", StringComparison.Ordinal),
            "Release publisher must capture native command exits immediately.");
        Require(publisher.Contains("Candidate metadata does not match its closed publication schema", StringComparison.Ordinal) &&
                publisher.Contains("Candidate directory does not contain exactly its frozen publication files", StringComparison.Ordinal) &&
                publisher.Contains("Candidate asset SHA-256 differs from metadata", StringComparison.Ordinal),
            "Release publisher must enforce candidate kind, closed asset set, and hashes.");
        Require(publisher.Contains("repos/$Repository/releases/$ReleaseId", StringComparison.Ordinal) &&
                publisher.Contains("[long]$release.id -ne $ReleaseId", StringComparison.Ordinal) &&
                publisher.Contains("[bool]$Release.immutable -ne $ExpectedImmutable", StringComparison.Ordinal),
            "Release snapshots must bind the numeric Release ID and immutable state.");
        Require(publisher.Contains("$([string]$_.url)`t$([string]$_.name)`t$([string]$_.state)`t$([long]$_.size)`t$([string]$_.digest)", StringComparison.Ordinal) &&
                publisher.Contains("Compare-Object @($Expected.AssetMetadata) @($Actual.AssetMetadata) -CaseSensitive", StringComparison.Ordinal),
            "Release snapshots must retain and compare URL, name, state, size, and digest.");
        Require(publisher.Contains("'api', '--method', 'PATCH', \"repos/$Repository/releases/$releaseId\"", StringComparison.Ordinal) &&
                publisher.Contains("Assert-ReleaseSnapshotEqual $beforePublication $publishedSnapshot", StringComparison.Ordinal),
            "Publication must PATCH the numeric Release and bind the prepublication snapshot across the state change.");
        Require(Regex.Matches(publisher, @"(?m)^Receive-And-VerifyReleaseAssets ", RegexOptions.CultureInvariant).Count == 2 &&
                publisher.Contains("Test-PublishedAssets.ps1", StringComparison.Ordinal) &&
                publisher.Contains("$publishedAfterDownload = Get-VerifiedReleaseSnapshot", StringComparison.Ordinal),
            "Publisher must download and compare candidate bytes before and after publication.");
        Require(publisher.Contains("gh release verify $ReleaseTag --repo $Repository", StringComparison.Ordinal) &&
                publisher.Contains("gh release verify-asset $ReleaseTag $publishedPath --repo $Repository", StringComparison.Ordinal) &&
                publisher.Contains("$finalSnapshot = Get-VerifiedReleaseSnapshot", StringComparison.Ordinal) &&
                publisher.LastIndexOf("Assert-TagIdentity", StringComparison.Ordinal) > publisher.IndexOf("$finalSnapshot", StringComparison.Ordinal),
            "Publisher must verify immutable Release attestations, every asset, the final snapshot, and the tag.");
        Require(!publisher.Contains("artifacts/rebuild", StringComparison.Ordinal),
            "Release publisher may upload only the original frozen candidate.");
        Require(!publisher.Contains("immutable-releases", StringComparison.Ordinal),
            "Release publisher may not require repository-administration access to inspect immutable-release settings.");
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

        YamlMappingNode ciRoot = ParseYaml(ci, CallerCiPath);
        YamlMappingNode releaseRoot = ParseYaml(release, CallerReleasePath);
        RequirePermissions(ciRoot, new Dictionary<string, string>(), CallerCiPath);
        RequirePermissions(releaseRoot, new Dictionary<string, string>(), CallerReleasePath);
        YamlMappingNode ciJob = Mapping(Mapping(ciRoot, "jobs", CallerCiPath), "build", CallerCiPath);
        YamlMappingNode releaseJob = Mapping(Mapping(releaseRoot, "jobs", CallerReleasePath), "publish", CallerReleasePath);
        RequirePermissions(ciJob, new Dictionary<string, string> { ["contents"] = "read" }, "caller CI build");
        RequirePermissions(releaseJob,
            new Dictionary<string, string> { ["actions"] = "read", ["attestations"] = "read", ["contents"] = "write" },
            "caller release publish");
        RequireCallerSecretMapping(ciJob, "caller CI build");
        RequireCallerSecretMapping(releaseJob, "caller release publish");
    }

    private static void RequireWorkflowCallSecret(YamlMappingNode call, string label)
    {
        YamlMappingNode secrets = Mapping(call, "secrets", label);
        Require(secrets.Children.Count == 1, $"{label} must declare exactly one workflow_call secret.");
        YamlMappingNode readToken = Mapping(secrets, "modkit_read_token", label);
        Require(readToken.Children.Count == 1 && Scalar(readToken, "required", label) == "true",
            $"{label} private ModKit read token must be required.");
    }

    private static void RequireCallerSecretMapping(YamlMappingNode job, string label)
    {
        YamlMappingNode secrets = Mapping(job, "secrets", label);
        Require(secrets.Children.Count == 1 &&
                Scalar(secrets, "modkit_read_token", label) == "${{ secrets.MODKIT_READ_TOKEN }}",
            $"{label} must pass only the named private ModKit read token.");
    }

    private static void ValidatePrivateBootstrapCheckout(YamlMappingNode job, string label)
    {
        YamlMappingNode[] checkouts = Sequence(job, "steps", label).Children
            .Select((step, index) => AsMapping(step, $"{label} step {index}"))
            .Where(step => OptionalScalar(step, "uses") == Checkout &&
                OptionalScalar(Mapping(step, "with", label, required: false), "repository") == "abmcar-ft2-mods/FarmTogether2-ModKit")
            .ToArray();
        Require(checkouts.Length == 1, $"{label} needs exactly one private ModKit bootstrap checkout.");
        YamlMappingNode with = Mapping(checkouts[0], "with", label);
        Require(with.Children.Count == 5 &&
                Scalar(with, "repository", label) == "abmcar-ft2-mods/FarmTogether2-ModKit" &&
                Scalar(with, "ref", label) == "${{ inputs.modkit-commit }}" &&
                Scalar(with, "token", label) == "${{ secrets.modkit_read_token }}" &&
                Scalar(with, "path", label) == ".modkit/bootstrap" &&
                Scalar(with, "persist-credentials", label) == "false",
            $"{label} private ModKit checkout is not locked to the named read secret and commit.");
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

    private static string ReadPinnedAction(string workflowPath, string action)
    {
        string path = Path.Combine(Root, workflowPath.Replace('/', Path.DirectorySeparatorChar));
        string pattern = $@"(?im)^\s*(?:-\s*)?uses\s*:\s*({Regex.Escape(action)}@[0-9a-f]{{40}})\s*$";
        string[] matches = Regex.Matches(File.ReadAllText(path), pattern, RegexOptions.CultureInvariant)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException($"{workflowPath} must contain one pinned {action} version.");
        return matches[0];
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

    private static bool ContainsScalar(YamlNode node, string expected)
    {
        return node switch
        {
            YamlScalarNode scalar => string.Equals(scalar.Value?.Trim(), expected, StringComparison.Ordinal),
            YamlSequenceNode sequence => sequence.Children.Any(child => ContainsScalar(child, expected)),
            YamlMappingNode mapping => mapping.Children.Any(pair =>
                ContainsScalar(pair.Key, expected) || ContainsScalar(pair.Value, expected)),
            _ => false
        };
    }

    private static bool ContainsMappingEntry(YamlNode node, string expectedKey, string expectedValue)
    {
        return node switch
        {
            YamlSequenceNode sequence => sequence.Children.Any(child => ContainsMappingEntry(child, expectedKey, expectedValue)),
            YamlMappingNode mapping => mapping.Children.Any(pair =>
                ScalarEquals(pair.Key, expectedKey) && ScalarEquals(pair.Value, expectedValue) ||
                ContainsMappingEntry(pair.Key, expectedKey, expectedValue) ||
                ContainsMappingEntry(pair.Value, expectedKey, expectedValue)),
            _ => false
        };
    }

    private static bool ScalarEquals(YamlNode node, string expected)
    {
        return node is YamlScalarNode scalar &&
            string.Equals(scalar.Value?.Trim(), expected, StringComparison.Ordinal);
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
