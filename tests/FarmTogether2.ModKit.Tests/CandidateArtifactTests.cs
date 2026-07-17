using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace FarmTogether2.ModKit.Tests;

[Trait("Category", "LongRunning")]
public sealed class CandidateArtifactTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const long RunId = 123456789;
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private static readonly string ToolAssembly = Path.Combine(
        Root,
        "tools",
        "FarmTogether2.ModKit.Tool",
        "bin",
        TestBuildConfiguration.Current,
        "net8.0",
        "FarmTogether2.ModKit.Tool.dll");
    private static readonly string CandidateScript = Path.Combine(Root, "scripts", "Test-Candidate.ps1");
    private static readonly string PublishedScript = Path.Combine(Root, "scripts", "Test-PublishedAssets.ps1");

    [Fact]
    public void ModCandidateRecordsExactlyThreeReleaseAssetHashes()
    {
        using Fixture fixture = Fixture.CreateMod();
        fixture.WriteCandidate("Mod").AssertSuccess();
        fixture.Verify("Mod").AssertSuccess();
        fixture.VerifyWithScript("Mod").AssertSuccess();

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(fixture.Candidate));
        string[] names = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(
            new[]
            {
                "schemaVersion", "candidateKind", "artifactName", "assemblyName", "version",
                "commit", "runId", "playerAsset", "playerSha256", "symbolsAsset",
                "symbolsSha256", "checksumsAsset", "checksumsSha256"
            },
            names);
        Assert.DoesNotContain("artifactDigest", names);
        Assert.DoesNotContain("artifactId", names);
    }

    [Fact]
    public void ModPreviewMetadataBindsItsPreviewArtifactName()
    {
        using Fixture fixture = Fixture.CreateMod(preview: true);
        fixture.WriteCandidate("Mod").AssertSuccess();
        fixture.Verify("Mod").AssertSuccess();
        fixture.Verify("Mod", expectedArtifactName: $"FarmTogether2.FixtureMod-candidate-{Commit}").AssertFailure("artifactName");
    }

    [Theory]
    [InlineData("artifactDigest", "\"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"")]
    [InlineData("artifactId", "99")]
    public void ModCandidateRejectsSelfReferentialArtifactFields(string field, string value)
    {
        using Fixture fixture = Fixture.CreateMod();
        fixture.WriteCandidate("Mod").AssertSuccess();
        string json = File.ReadAllText(fixture.Candidate);
        json = json.TrimEnd().TrimEnd('}') + $",\n  \"{field}\": {value}\n}}\n";
        File.WriteAllText(fixture.Candidate, json, new UTF8Encoding(false));

        fixture.Verify("Mod").AssertFailure("closed");
    }

    [Fact]
    public void ModCandidateRejectsWrongIdentityChangedBytesAndExtraFiles()
    {
        using Fixture fixture = Fixture.CreateMod();
        fixture.WriteCandidate("Mod").AssertSuccess();
        fixture.Verify("Mod", expectedCommit: new string('a', 40)).AssertFailure("commit");

        File.AppendAllText(fixture.PrimaryAsset, "changed", new UTF8Encoding(false));
        fixture.Verify("Mod").AssertFailure("SHA-256");

        File.WriteAllText(Path.Combine(fixture.DirectoryPath, "extra.txt"), "extra", new UTF8Encoding(false));
        fixture.Verify("Mod").AssertFailure("exactly");
    }

    [Fact]
    public void ReferenceCandidateUsesItsClosedPackageSchema()
    {
        using Fixture fixture = Fixture.CreateReference();
        fixture.WriteCandidate("Reference").AssertSuccess();
        fixture.Verify("Reference").AssertSuccess();

        string json = File.ReadAllText(fixture.Candidate);
        Assert.Contains("\"packageAsset\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("playerAsset", json, StringComparison.Ordinal);
        Assert.DoesNotContain("artifactDigest", json, StringComparison.Ordinal);

        fixture.Verify("Mod").AssertFailure("kind");
    }

    [Theory]
    [InlineData("artifactDigest", "\"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"")]
    [InlineData("artifactId", "99")]
    [InlineData("playerAsset", "\"FarmTogether2.FixtureMod-v1.0.0.zip\"")]
    public void ReferenceCandidateRejectsGithubAndModOnlyFields(string field, string value)
    {
        using Fixture fixture = Fixture.CreateReference();
        fixture.WriteCandidate("Reference").AssertSuccess();
        string json = File.ReadAllText(fixture.Candidate);
        json = json.TrimEnd().TrimEnd('}') + $",\n  \"{field}\": {value}\n}}\n";
        File.WriteAllText(fixture.Candidate, json, new UTF8Encoding(false));

        fixture.Verify("Reference").AssertFailure("closed");
    }

    [Fact]
    public void PublishedVerificationRequiresExactCandidateBytesWithoutMetadata()
    {
        using Fixture fixture = Fixture.CreateMod();
        fixture.WriteCandidate("Mod").AssertSuccess();
        string published = Path.Combine(Path.GetDirectoryName(fixture.DirectoryPath)!, $"published-{Guid.NewGuid():N}");
        Directory.CreateDirectory(published);
        try
        {
            foreach (string source in Directory.EnumerateFiles(fixture.DirectoryPath).Where(path => Path.GetFileName(path) != "candidate.json"))
                File.Copy(source, Path.Combine(published, Path.GetFileName(source)));

            fixture.VerifyPublished("Mod", published).AssertSuccess();
            fixture.VerifyPublishedWithScript("Mod", published).AssertSuccess();
            File.Copy(fixture.Candidate, Path.Combine(published, "candidate.json"));
            fixture.VerifyPublished("Mod", published).AssertFailure("exactly");
        }
        finally
        {
            Directory.Delete(published, recursive: true);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(string kind, bool preview = false)
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), $"modkit-candidate-{kind}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(DirectoryPath);
            ArtifactName = kind == "Mod"
                ? $"FarmTogether2.FixtureMod-{(preview ? "preview" : "candidate")}-{Commit}"
                : $"FarmTogether2.GameApi.Ref-candidate-{Commit}";
            if (kind == "Mod")
            {
                PrimaryAsset = Path.Combine(DirectoryPath, "FarmTogether2.FixtureMod-v1.2.3.zip");
                SecondaryAsset = Path.Combine(DirectoryPath, "FarmTogether2.FixtureMod-v1.2.3-symbols.zip");
            }
            else
            {
                PrimaryAsset = Path.Combine(DirectoryPath, "FarmTogether2.GameApi.Ref.1.0.0.nupkg");
                SecondaryAsset = null;
            }
            File.WriteAllBytes(PrimaryAsset, [1, 2, 3, 4]);
            if (SecondaryAsset is not null)
                File.WriteAllBytes(SecondaryAsset, [5, 6, 7, 8]);
            WriteChecksums();
        }

        public string DirectoryPath { get; }
        public string ArtifactName { get; }
        public string PrimaryAsset { get; }
        public string? SecondaryAsset { get; }
        public string Candidate => Path.Combine(DirectoryPath, "candidate.json");

        public static Fixture CreateMod(bool preview = false) => new("Mod", preview);
        public static Fixture CreateReference() => new("Reference");

        public ProcessResult WriteCandidate(string kind)
        {
            List<string> args =
            [
                "candidate", kind == "Mod" ? "write-mod" : "write-reference",
                "--directory", DirectoryPath,
                "--artifact-name", ArtifactName,
                "--commit", Commit,
                "--run-id", RunId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ];
            if (kind == "Mod")
            {
                args.AddRange(["--assembly-name", "FarmTogether2.FixtureMod", "--version", "1.2.3"]);
            }
            else
            {
                args.AddRange(["--package-id", "FarmTogether2.GameApi.Ref", "--package-version", "1.0.0"]);
            }
            return Run(args);
        }

        public ProcessResult Verify(string kind, string expectedCommit = Commit, string? expectedArtifactName = null) => Run(
        [
            "candidate", "verify",
            "--directory", DirectoryPath,
            "--kind", kind,
            "--expected-commit", expectedCommit,
            "--expected-run-id", RunId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--expected-artifact-name", expectedArtifactName ?? ArtifactName
        ]);

        public ProcessResult VerifyPublished(string kind, string published) => Run(
        [
            "candidate", "verify-published",
            "--published-directory", published,
            "--candidate-directory", DirectoryPath,
            "--kind", kind
        ]);

        public ProcessResult VerifyWithScript(string kind) => RunProcess(
            "pwsh",
            [
                "-NoLogo", "-NoProfile", "-File", CandidateScript,
                "-CandidateDirectory", DirectoryPath,
                "-ExpectedCommit", Commit,
                "-ExpectedRunId", RunId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-ExpectedArtifactName", ArtifactName,
                "-CandidateKind", kind
            ]);

        public ProcessResult VerifyPublishedWithScript(string kind, string published) => RunProcess(
            "pwsh",
            [
                "-NoLogo", "-NoProfile", "-File", PublishedScript,
                "-PublishedDirectory", published,
                "-CandidateDirectory", DirectoryPath,
                "-CandidateKind", kind
            ]);

        private void WriteChecksums()
        {
            IEnumerable<string> paths = SecondaryAsset is null ? [PrimaryAsset] : [PrimaryAsset, SecondaryAsset];
            string content = string.Join(
                "\n",
                paths.OrderBy(Path.GetFileName, StringComparer.Ordinal)
                    .Select(path => $"{Sha256(path)}  {Path.GetFileName(path)}")) + "\n";
            File.WriteAllText(Path.Combine(DirectoryPath, "SHA256SUMS.txt"), content, new UTF8Encoding(false));
        }

        private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

        private static ProcessResult Run(IEnumerable<string> toolArguments)
        {
            return RunProcess(
                "dotnet",
                new[] { ToolAssembly }.Concat(toolArguments));
        }

        private static ProcessResult RunProcess(string fileName, IEnumerable<string> arguments)
        {
            ProcessStartInfo startInfo = new(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);
            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start dotnet.");
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
