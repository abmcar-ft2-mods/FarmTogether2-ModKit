using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace FarmTogether2.ModKit.Tests;

public sealed class VerifiedReleasePublisherTests
{
    private const string TagObject = "1111111111111111111111111111111111111111";
    private const string Commit = "2222222222222222222222222222222222222222";
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private static readonly string Publisher = Path.Combine(Root, "scripts", "Publish-VerifiedRelease.ps1");

    [Fact]
    public void NewReleaseIsPublishedByNumericIdAndAttestationsAreVerified()
    {
        using Fixture fixture = new();

        ProcessResult result = fixture.Run();

        result.AssertSuccess();
        JsonObject state = fixture.ReadState();
        Assert.True(state["Exists"]!.GetValue<bool>());
        Assert.False(state["Draft"]!.GetValue<bool>());
        Assert.True(state["Immutable"]!.GetValue<bool>());
        string log = fixture.ReadLog();
        Assert.Contains("api\t--method\tPATCH\trepos/owner/repository/releases/42\t-F\tdraft=false", log, StringComparison.Ordinal);
        Assert.DoesNotContain("release\tedit", log, StringComparison.Ordinal);
        Assert.Contains("release\tverify\tv1.2.3\t--repo\towner/repository", log, StringComparison.Ordinal);
        Assert.Equal(3, Count(log, "release\tverify-asset\tv1.2.3\t"));
    }

    [Fact]
    public void ReferenceReleaseUsesTheSameVerifiedPublisher()
    {
        using Fixture fixture = new("Reference");

        ProcessResult result = fixture.Run();

        result.AssertSuccess();
        JsonObject state = fixture.ReadState();
        Assert.False(state["Draft"]!.GetValue<bool>());
        Assert.True(state["Immutable"]!.GetValue<bool>());
        string log = fixture.ReadLog();
        Assert.Contains("\tPATCH\trepos/owner/repository/releases/42", log, StringComparison.Ordinal);
        Assert.Equal(2, Count(log, "release\tverify-asset\tv1.2.3\t"));
    }

    [Fact]
    public void ExistingPartialDraftUploadsOnlyMissingAssets()
    {
        using Fixture fixture = new();
        fixture.SeedPartialDraftRelease();

        ProcessResult result = fixture.Run();

        result.AssertSuccess();
        string log = fixture.ReadLog();
        Assert.DoesNotContain("release\tcreate", log, StringComparison.Ordinal);
        Assert.Equal(2, Count(log, "release\tupload\tv1.2.3\t"));
        Assert.Contains("\tPATCH\trepos/owner/repository/releases/42", log, StringComparison.Ordinal);
    }

    [Fact]
    public void PublishedRerunSkipsMutationsAndReverifiesAttestations()
    {
        using Fixture fixture = new();
        fixture.SeedPublishedRelease();

        ProcessResult result = fixture.Run();

        result.AssertSuccess();
        string log = fixture.ReadLog();
        Assert.DoesNotContain("release\tcreate", log, StringComparison.Ordinal);
        Assert.DoesNotContain("release\tupload", log, StringComparison.Ordinal);
        Assert.DoesNotContain("\tPATCH\t", log, StringComparison.Ordinal);
        Assert.Contains("release\tverify\tv1.2.3", log, StringComparison.Ordinal);
        Assert.Equal(3, Count(log, "release\tverify-asset\tv1.2.3\t"));
    }

    [Fact]
    public void MutablePublicationResponseIsRejected()
    {
        using Fixture fixture = new();
        fixture.SetState("ImmutableEnabled", false);

        ProcessResult result = fixture.Run();

        result.AssertFailure("immutability");
        string log = fixture.ReadLog();
        Assert.Contains("\tPATCH\trepos/owner/repository/releases/42", log, StringComparison.Ordinal);
        Assert.DoesNotContain("release\tverify\t", log, StringComparison.Ordinal);
    }

    [Fact]
    public void PublishedMutableReleaseIsRejectedWithoutMutation()
    {
        using Fixture fixture = new();
        fixture.SeedPublishedRelease();
        fixture.SetState("Immutable", false);

        ProcessResult result = fixture.Run();

        result.AssertFailure("immutability");
        string log = fixture.ReadLog();
        AssertNoReleaseMutation(log);
    }

    [Fact]
    public void NonNotFoundLookupFailureCannotEnterCreatePath()
    {
        using Fixture fixture = new();
        fixture.SetState("LookupStatus", 500);

        ProcessResult result = fixture.Run();

        result.AssertFailure("HTTP 500");
        AssertNoReleaseMutation(fixture.ReadLog());
    }

    [Fact]
    public void NumericPublicationRejectsAChangedReleaseIdentity()
    {
        using Fixture fixture = new();
        fixture.SetState("ReplaceIdOnPublish", true);

        ProcessResult result = fixture.Run();

        result.AssertFailure("different Release ID");
        string log = fixture.ReadLog();
        Assert.Contains("\tPATCH\trepos/owner/repository/releases/42", log, StringComparison.Ordinal);
        Assert.DoesNotContain("release\tverify\t", log, StringComparison.Ordinal);
    }

    [Fact]
    public void AssetMetadataMutationDuringNumericPublicationIsRejected()
    {
        using Fixture fixture = new();
        fixture.SetState("MutateMetadataOnPublish", true);

        ProcessResult result = fixture.Run();

        result.AssertFailure("changed during publication");
        string log = fixture.ReadLog();
        Assert.Contains("\tPATCH\trepos/owner/repository/releases/42", log, StringComparison.Ordinal);
        Assert.DoesNotContain("release\tverify\t", log, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("state")]
    [InlineData("size")]
    [InlineData("digest")]
    public void CandidateBoundAssetMetadataFaultIsRejected(string fault)
    {
        using Fixture fixture = new();
        fixture.SetState("AssetMetadataFault", fault);

        ProcessResult result = fixture.Run();

        result.AssertFailure("asset metadata differs from the frozen candidate");
        string log = fixture.ReadLog();
        Assert.DoesNotContain("\tPATCH\t", log, StringComparison.Ordinal);
        Assert.DoesNotContain("release\tverify\t", log, StringComparison.Ordinal);
    }

    [Fact]
    public void DraftAssetMetadataMutationBeforePublicationIsRejected()
    {
        using Fixture fixture = new();
        fixture.SetState("MutateDraftMetadataAfterDownload", true);

        ProcessResult result = fixture.Run();

        result.AssertFailure("changed during prepublication byte verification");
        string log = fixture.ReadLog();
        Assert.DoesNotContain("\tPATCH\t", log, StringComparison.Ordinal);
        Assert.DoesNotContain("release\tverify\t", log, StringComparison.Ordinal);
    }

    [Fact]
    public void DraftAssetByteMutationBeforePublicationIsRejected()
    {
        using Fixture fixture = new();
        fixture.SetState("TamperDraftDownload", true);

        ProcessResult result = fixture.Run();

        result.AssertFailure("not byte-identical to the candidate");
        string log = fixture.ReadLog();
        Assert.DoesNotContain("\tPATCH\t", log, StringComparison.Ordinal);
        Assert.DoesNotContain("release\tverify\t", log, StringComparison.Ordinal);
    }

    [Fact]
    public void PublishedAssetMetadataMutationAfterPublicationIsRejected()
    {
        using Fixture fixture = new();
        fixture.SetState("MutatePublishedMetadataAfterDownload", true);

        ProcessResult result = fixture.Run();

        result.AssertFailure("changed during final byte verification");
        string log = fixture.ReadLog();
        Assert.Contains("\tPATCH\trepos/owner/repository/releases/42", log, StringComparison.Ordinal);
        Assert.DoesNotContain("release\tverify\t", log, StringComparison.Ordinal);
    }

    [Fact]
    public void PublishedAssetByteMutationAfterPublicationIsRejected()
    {
        using Fixture fixture = new();
        fixture.SetState("TamperPublishedDownload", true);

        ProcessResult result = fixture.Run();

        result.AssertFailure("not byte-identical to the candidate");
        string log = fixture.ReadLog();
        Assert.Contains("\tPATCH\trepos/owner/repository/releases/42", log, StringComparison.Ordinal);
        Assert.DoesNotContain("release\tverify\t", log, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidGitOutputWithFailingExitCannotBypassTagVerification()
    {
        using Fixture fixture = new();
        fixture.SetState("FailTagObjectGit", true);

        ProcessResult result = fixture.Run();

        result.AssertFailure("exit code 17");
        AssertNoReleaseMutation(fixture.ReadLog());
    }

    [Theory]
    [InlineData("FailReleaseVerify", "Release attestation")]
    [InlineData("FailAssetVerify", "asset attestation")]
    public void AttestationFailureCannotReturnSuccess(string failure, string expectedError)
    {
        using Fixture fixture = new();
        fixture.SetState(failure, true);

        ProcessResult result = fixture.Run();

        result.AssertFailure(expectedError);
    }

    private static int Count(string value, string fragment)
    {
        int count = 0;
        int offset = 0;
        while ((offset = value.IndexOf(fragment, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += fragment.Length;
        }
        return count;
    }

    private static void AssertNoReleaseMutation(string log)
    {
        Assert.DoesNotContain("release\tcreate", log, StringComparison.Ordinal);
        Assert.DoesNotContain("release\tupload", log, StringComparison.Ordinal);
        Assert.DoesNotContain("\tPATCH\t", log, StringComparison.Ordinal);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"modkit-release-publisher-{Guid.NewGuid():N}");
        private readonly string _candidate;
        private readonly string _remote;
        private readonly string _shim;
        private readonly string _state;
        private readonly string _log;
        private readonly string _working;
        private readonly string _candidateKind;

        public Fixture(string candidateKind = "Mod")
        {
            if (candidateKind is not ("Mod" or "Reference"))
                throw new ArgumentOutOfRangeException(nameof(candidateKind));
            _candidateKind = candidateKind;
            _candidate = Path.Combine(_root, "candidate");
            _remote = Path.Combine(_root, "remote");
            _shim = Path.Combine(_root, "shim");
            _state = Path.Combine(_root, "state.json");
            _log = Path.Combine(_root, "commands.log");
            _working = Path.Combine(_root, "working");
            Directory.CreateDirectory(_candidate);
            Directory.CreateDirectory(_remote);
            Directory.CreateDirectory(_shim);
            WriteCandidate();
            WriteState();
            WriteShims();
        }

        public ProcessResult Run()
        {
            ProcessStartInfo startInfo = new("pwsh")
            {
                WorkingDirectory = Root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            string[] arguments =
            [
                "-NoLogo", "-NoProfile", "-File", Publisher,
                "-Repository", "owner/repository",
                "-ReleaseTag", "v1.2.3",
                "-ExpectedTagObject", TagObject,
                "-ExpectedCommit", Commit,
                "-CandidateDirectory", _candidate,
                "-CandidateKind", _candidateKind,
                "-WorkingDirectory", _working
            ];
            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);
            startInfo.Environment["PATH"] = _shim + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
            startInfo.Environment["FAKE_RELEASE_STATE"] = _state;
            startInfo.Environment["FAKE_RELEASE_REMOTE"] = _remote;
            startInfo.Environment["FAKE_RELEASE_LOG"] = _log;
            startInfo.Environment["FAKE_TAG_OBJECT"] = TagObject;
            startInfo.Environment["FAKE_COMMIT"] = Commit;
            startInfo.Environment["FAKE_REAL_GIT"] = ResolveCommand("git");
            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start PowerShell.");
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return new ProcessResult(process.ExitCode, output, error);
        }

        public void SeedPublishedRelease()
        {
            foreach (string source in Directory.EnumerateFiles(_candidate).Where(path => Path.GetFileName(path) != "candidate.json"))
                File.Copy(source, Path.Combine(_remote, Path.GetFileName(source)));
            SetState("Exists", true);
            SetState("Draft", false);
            SetState("Immutable", true);
        }

        public void SeedPartialDraftRelease()
        {
            string source = Directory.EnumerateFiles(_candidate)
                .Where(path => Path.GetFileName(path) != "candidate.json")
                .Order(StringComparer.Ordinal)
                .First();
            File.Copy(source, Path.Combine(_remote, Path.GetFileName(source)));
            SetState("Exists", true);
            SetState("Draft", true);
            SetState("Immutable", false);
        }

        public JsonObject ReadState() => JsonNode.Parse(File.ReadAllText(_state))!.AsObject();

        public void SetState(string name, bool value)
        {
            JsonObject state = ReadState();
            state[name] = value;
            File.WriteAllText(_state, state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        }

        public void SetState(string name, int value)
        {
            JsonObject state = ReadState();
            state[name] = value;
            File.WriteAllText(_state, state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        }

        public void SetState(string name, string value)
        {
            JsonObject state = ReadState();
            state[name] = value;
            File.WriteAllText(_state, state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        }

        public string ReadLog() => File.Exists(_log) ? File.ReadAllText(_log) : string.Empty;

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        private void WriteCandidate()
        {
            string checksums = "SHA256SUMS.txt";
            if (_candidateKind == "Reference")
            {
                string package = "FarmTogether2.GameApi.Ref.1.2.3.nupkg";
                File.WriteAllBytes(Path.Combine(_candidate, package), [1, 2, 3, 4]);
                string referenceChecksums = $"{Sha256(Path.Combine(_candidate, package))}  {package}\n";
                File.WriteAllText(Path.Combine(_candidate, checksums), referenceChecksums, new UTF8Encoding(false));
                var referenceCandidate = new
                {
                    schemaVersion = 1,
                    candidateKind = "Reference",
                    artifactName = $"FarmTogether2.GameApi.Ref-candidate-{Commit}",
                    packageId = "FarmTogether2.GameApi.Ref",
                    packageVersion = "1.2.3",
                    commit = Commit,
                    runId = 123456789,
                    packageAsset = package,
                    packageSha256 = Sha256(Path.Combine(_candidate, package)),
                    checksumsAsset = checksums,
                    checksumsSha256 = Sha256(Path.Combine(_candidate, checksums))
                };
                File.WriteAllText(
                    Path.Combine(_candidate, "candidate.json"),
                    JsonSerializer.Serialize(referenceCandidate, new JsonSerializerOptions { WriteIndented = true }) + "\n",
                    new UTF8Encoding(false));
                return;
            }

            string player = "FarmTogether2.FixtureMod-v1.2.3.zip";
            string symbols = "FarmTogether2.FixtureMod-v1.2.3-symbols.zip";
            File.WriteAllBytes(Path.Combine(_candidate, player), [1, 2, 3, 4]);
            File.WriteAllBytes(Path.Combine(_candidate, symbols), [5, 6, 7, 8]);
            string checksumContent = string.Join("\n", new[] { player, symbols }.Order(StringComparer.Ordinal)
                .Select(name => $"{Sha256(Path.Combine(_candidate, name))}  {name}")) + "\n";
            File.WriteAllText(Path.Combine(_candidate, checksums), checksumContent, new UTF8Encoding(false));
            var candidate = new
            {
                schemaVersion = 1,
                candidateKind = "Mod",
                artifactName = $"FarmTogether2.FixtureMod-candidate-{Commit}",
                assemblyName = "FarmTogether2.FixtureMod",
                version = "1.2.3",
                commit = Commit,
                runId = 123456789,
                playerAsset = player,
                playerSha256 = Sha256(Path.Combine(_candidate, player)),
                symbolsAsset = symbols,
                symbolsSha256 = Sha256(Path.Combine(_candidate, symbols)),
                checksumsAsset = checksums,
                checksumsSha256 = Sha256(Path.Combine(_candidate, checksums))
            };
            File.WriteAllText(
                Path.Combine(_candidate, "candidate.json"),
                JsonSerializer.Serialize(candidate, new JsonSerializerOptions { WriteIndented = true }) + "\n",
                new UTF8Encoding(false));
        }

        private void WriteState()
        {
            var state = new
            {
                Exists = false,
                Draft = true,
                Immutable = false,
                ImmutableEnabled = true,
                ReleaseId = 42,
                LookupStatus = 0,
                ReplaceIdOnPublish = false,
                FailTagObjectGit = false,
                FailReleaseVerify = false,
                FailAssetVerify = false,
                MutateMetadataOnPublish = false,
                MutateDraftMetadataAfterDownload = false,
                MutatePublishedMetadataAfterDownload = false,
                DraftMetadataMutated = false,
                PublishedMetadataMutated = false,
                TamperDraftDownload = false,
                TamperPublishedDownload = false,
                DraftDownloadCount = 0,
                PublishedDownloadCount = 0,
                ExpectedAssetCount = _candidateKind == "Mod" ? 3 : 2,
                AssetMetadataFault = ""
            };
            File.WriteAllText(_state, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        }

        private void WriteShims()
        {
            string fakeGh = Path.Combine(_root, "FakeGh.ps1");
            string fakeGit = Path.Combine(_root, "FakeGit.ps1");
            File.WriteAllText(fakeGh, FakeGhScript, new UTF8Encoding(false));
            File.WriteAllText(fakeGit, FakeGitScript, new UTF8Encoding(false));
            WriteShim("gh", fakeGh);
            WriteShim("git", fakeGit);
        }

        private void WriteShim(string name, string script)
        {
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(
                    Path.Combine(_shim, name + ".cmd"),
                    $"@pwsh -NoLogo -NoProfile -File \"{script}\" %*\r\n",
                    new UTF8Encoding(false));
            }
            else
            {
                string path = Path.Combine(_shim, name);
                string escaped = script.Replace("'", "'\\''", StringComparison.Ordinal);
                File.WriteAllText(path, $"#!/bin/sh\nexec pwsh -NoLogo -NoProfile -File '{escaped}' \"$@\"\n", new UTF8Encoding(false));
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        private static string ResolveCommand(string name)
        {
            ProcessStartInfo startInfo = new(OperatingSystem.IsWindows() ? "where.exe" : "which")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(name);
            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Unable to locate {name}.");
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException($"Unable to locate {name}.");
            return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0];
        }

        private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    }

    private readonly record struct ProcessResult(int ExitCode, string Output, string Error)
    {
        public void AssertSuccess() => Assert.True(ExitCode == 0, $"pwsh exited {ExitCode}\nstdout:\n{Output}\nstderr:\n{Error}");

        public void AssertFailure(string fragment)
        {
            Assert.NotEqual(0, ExitCode);
            Assert.Contains(fragment, Output + Error, StringComparison.OrdinalIgnoreCase);
        }
    }

    private const string FakeGitScript = """
$Arguments = @($args)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$state = Get-Content -LiteralPath $env:FAKE_RELEASE_STATE -Raw | ConvertFrom-Json
Add-Content -LiteralPath $env:FAKE_RELEASE_LOG -Value ("git`t" + ($Arguments -join "`t"))
if ($Arguments.Count -eq 3 -and $Arguments[0] -ceq 'cat-file' -and $Arguments[1] -ceq '-t' -and $Arguments[2] -like 'refs/tags/*') {
    'tag'
    exit 0
}
$isPeeledTag = $Arguments.Count -eq 3 -and
    $Arguments[0] -ceq 'rev-parse' -and
    $Arguments[1] -ceq '--verify' -and
    $Arguments[2].StartsWith('refs/tags/', [StringComparison]::Ordinal) -and
    ($Arguments[2].EndsWith('^{}', [StringComparison]::Ordinal) -or
        ($IsWindows -and $Arguments[2].EndsWith('{}', [StringComparison]::Ordinal)))
if ($isPeeledTag) {
    $env:FAKE_COMMIT
    exit 0
}
if ($Arguments.Count -eq 3 -and $Arguments[0] -ceq 'rev-parse' -and $Arguments[1] -ceq '--verify' -and $Arguments[2] -like 'refs/tags/*') {
    $env:FAKE_TAG_OBJECT
    if ([bool]$state.FailTagObjectGit) { exit 17 }
    exit 0
}
if ($Arguments.Count -eq 3 -and $Arguments[0] -ceq 'rev-parse' -and $Arguments[1] -ceq '--verify' -and $Arguments[2] -ceq 'HEAD') {
    $env:FAKE_COMMIT
    exit 0
}
& $env:FAKE_REAL_GIT @Arguments
exit $LASTEXITCODE
""";

    private const string FakeGhScript = """
$Arguments = @($args)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$statePath = $env:FAKE_RELEASE_STATE
$remote = $env:FAKE_RELEASE_REMOTE
$state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
Add-Content -LiteralPath $env:FAKE_RELEASE_LOG -Value ($Arguments -join "`t")

function Save-State {
    $state | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $statePath -Encoding utf8NoBOM
}

function Get-ReleaseObject {
    $index = 0
    $assets = @(Get-ChildItem -LiteralPath $remote -File | Sort-Object Name | ForEach-Object {
        $index++
        $url = "https://api.github.test/repos/owner/repository/releases/assets/$index"
        $assetState = 'uploaded'
        $size = [long]$_.Length
        $digest = "sha256:$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())"
        if ($index -eq 1 -and ([bool]$state.DraftMetadataMutated -or [bool]$state.PublishedMetadataMutated)) {
            $url += '?mutated=true'
        }
        if ($index -eq 1) {
            switch ([string]$state.AssetMetadataFault) {
                'state' { $assetState = 'processing' }
                'size' { $size++ }
                'digest' { $digest = 'sha256:' + ('0' * 64) }
                '' { }
                default { throw "Unsupported asset metadata fault: $($state.AssetMetadataFault)" }
            }
        }
        [ordered]@{
            url = $url
            id = $index
            name = $_.Name
            state = $assetState
            size = $size
            digest = $digest
        }
    })
    return [ordered]@{
        id = [long]$state.ReleaseId
        tag_name = 'v1.2.3'
        draft = [bool]$state.Draft
        prerelease = $false
        immutable = [bool]$state.Immutable
        assets = $assets
    }
}

function Write-ReleaseJson {
    Get-ReleaseObject | ConvertTo-Json -Depth 10
}

function Write-Http([int]$Status, [string]$Body) {
    $reason = if ($Status -eq 200) { 'OK' } elseif ($Status -eq 404) { 'Not Found' } else { 'Server Error' }
    "HTTP/2.0 $Status $reason"
    'Content-Type: application/json'
    ''
    $Body
    if ($Status -lt 200 -or $Status -ge 300) {
        [Console]::Error.WriteLine("gh: HTTP $Status")
        exit 1
    }
    exit 0
}

if ($Arguments.Count -ge 1 -and $Arguments[0] -ceq 'api') {
    $endpoint = @($Arguments | Where-Object { $_ -like 'repos/*' })[-1]
    $method = 'GET'
    $methodIndex = [Array]::IndexOf([string[]]$Arguments, '--method')
    if ($methodIndex -ge 0) { $method = $Arguments[$methodIndex + 1] }
    $include = $Arguments -ccontains '--include'
    if ($endpoint -ceq 'repos/owner/repository/immutable-releases') {
        [ordered]@{ enabled = [bool]$state.ImmutableEnabled; enforced_by_owner = $false } | ConvertTo-Json -Compress
        exit 0
    }
    if ($endpoint -ceq 'repos/owner/repository/git/ref/tags/v1.2.3') {
        [ordered]@{ ref = 'refs/tags/v1.2.3'; object = [ordered]@{ type = 'tag'; sha = $env:FAKE_TAG_OBJECT } } | ConvertTo-Json -Depth 5 -Compress
        exit 0
    }
    if ($endpoint -ceq "repos/owner/repository/git/tags/$env:FAKE_TAG_OBJECT") {
        [ordered]@{ sha = $env:FAKE_TAG_OBJECT; object = [ordered]@{ type = 'commit'; sha = $env:FAKE_COMMIT } } | ConvertTo-Json -Depth 5 -Compress
        exit 0
    }
    if ($endpoint -ceq 'repos/owner/repository/releases/tags/v1.2.3') {
        if (-not $include) { throw 'Tag lookup must request the HTTP status.' }
        if ([int]$state.LookupStatus -ne 0) {
            Write-Http ([int]$state.LookupStatus) '{"message":"injected"}'
        }
        if (-not [bool]$state.Exists) { Write-Http 404 '{"message":"Not Found"}' }
        Write-Http 200 ((Get-ReleaseObject | ConvertTo-Json -Depth 10 -Compress))
    }
    if ($endpoint -like 'repos/owner/repository/releases/*' -and $method -ceq 'PATCH') {
        $requestedId = [long]($endpoint -replace '^.*/', '')
        if ($requestedId -ne [long]$state.ReleaseId) { [Console]::Error.WriteLine('wrong release id'); exit 1 }
        $state.Draft = $false
        $state.Immutable = [bool]$state.ImmutableEnabled
        if ([bool]$state.ReplaceIdOnPublish) { $state.ReleaseId = [long]$state.ReleaseId + 1 }
        if ([bool]$state.MutateMetadataOnPublish) { $state.PublishedMetadataMutated = $true }
        Save-State
        Write-ReleaseJson
        exit 0
    }
    if ($endpoint -like 'repos/owner/repository/releases/*' -and $method -ceq 'GET') {
        Write-ReleaseJson
        exit 0
    }
    [Console]::Error.WriteLine("unsupported api call: $($Arguments -join ' ')")
    exit 9
}

if ($Arguments.Count -ge 2 -and $Arguments[0] -ceq 'release') {
    switch ($Arguments[1]) {
        'create' {
            foreach ($path in @($Arguments | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })) {
                Copy-Item -LiteralPath $path -Destination (Join-Path $remote ([IO.Path]::GetFileName($path)))
            }
            $state.Exists = $true
            $state.Draft = $true
            $state.Immutable = $false
            $state.LookupStatus = 0
            Save-State
            exit 0
        }
        'upload' {
            $path = @($Arguments | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })[-1]
            Copy-Item -LiteralPath $path -Destination (Join-Path $remote ([IO.Path]::GetFileName($path)))
            exit 0
        }
        'download' {
            $patternIndex = [Array]::IndexOf([string[]]$Arguments, '--pattern')
            $directoryIndex = [Array]::IndexOf([string[]]$Arguments, '--dir')
            $name = $Arguments[$patternIndex + 1]
            $directory = $Arguments[$directoryIndex + 1]
            $destination = Join-Path $directory $name
            Copy-Item -LiteralPath (Join-Path $remote $name) -Destination $destination
            if ([bool]$state.Draft) {
                $state.DraftDownloadCount = [int]$state.DraftDownloadCount + 1
                if ([bool]$state.TamperDraftDownload) {
                    Add-Content -LiteralPath $destination -Value 'tampered-draft' -NoNewline
                }
                if ([bool]$state.MutateDraftMetadataAfterDownload -and
                    [int]$state.DraftDownloadCount -ge [int]$state.ExpectedAssetCount) {
                    $state.DraftMetadataMutated = $true
                }
            } else {
                $state.PublishedDownloadCount = [int]$state.PublishedDownloadCount + 1
                if ([bool]$state.TamperPublishedDownload) {
                    Add-Content -LiteralPath $destination -Value 'tampered-published' -NoNewline
                }
                if ([bool]$state.MutatePublishedMetadataAfterDownload -and
                    [int]$state.PublishedDownloadCount -ge [int]$state.ExpectedAssetCount) {
                    $state.PublishedMetadataMutated = $true
                }
            }
            Save-State
            exit 0
        }
        'verify' {
            if ([bool]$state.FailReleaseVerify) { exit 31 }
            if (-not [bool]$state.Immutable) { exit 32 }
            '{}'
            exit 0
        }
        'verify-asset' {
            if ([bool]$state.FailAssetVerify) { exit 33 }
            $path = $Arguments[3]
            $remotePath = Join-Path $remote ([IO.Path]::GetFileName($path))
            if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
                (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne (Get-FileHash -LiteralPath $remotePath -Algorithm SHA256).Hash) {
                exit 34
            }
            '{}'
            exit 0
        }
    }
}
[Console]::Error.WriteLine("unsupported gh call: $($Arguments -join ' ')")
exit 9
""";
}
