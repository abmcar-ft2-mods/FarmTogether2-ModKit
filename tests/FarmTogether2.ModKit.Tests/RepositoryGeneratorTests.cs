using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using NuGet.Packaging;
using Xunit;

namespace FarmTogether2.ModKit.Tests;

public sealed class RepositoryGeneratorTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private static readonly string Script = Path.Combine(Root, "scripts", "New-ModRepositoryFiles.ps1");

    [Fact]
    public void GeneratorCreatesIdempotentRootAnchoredBuildFiles()
    {
        using Fixture fixture = new();
        Dictionary<string, string> protectedBefore = fixture.ProtectedHashes();

        fixture.Run(includeCallerWorkflows: false).AssertSuccess();
        Dictionary<string, string> first = Snapshot(fixture.Repository);
        fixture.Run(includeCallerWorkflows: false).AssertSuccess();
        Assert.Equal(first, Snapshot(fixture.Repository));
        Assert.Equal(protectedBefore, fixture.ProtectedHashes());

        string props = File.ReadAllText(Path.Combine(fixture.Repository, "Directory.Build.props"));
        Assert.Contains("$(MSBuildThisFileDirectory).modkit", props, StringComparison.Ordinal);
        Assert.DoesNotContain("$(MSBuildProjectDirectory).modkit", props, StringComparison.Ordinal);
        string targets = File.ReadAllText(Path.Combine(fixture.Repository, "Directory.Build.targets"));
        Assert.Contains("RejectDirectFarmTogether2Deployment", targets, StringComparison.Ordinal);
        Assert.Contains("DeployToGame", targets, StringComparison.Ordinal);
        Assert.DoesNotContain("<Copy", targets, StringComparison.Ordinal);

        string nuget = File.ReadAllText(Path.Combine(fixture.Repository, "NuGet.config"));
        Assert.Contains("FarmTogether2.GameApi.Ref", nuget, StringComparison.Ordinal);
        Assert.Contains("FarmTogether2-ModKit", nuget, StringComparison.Ordinal);
        Assert.DoesNotContain("pattern=\"*\"", nuget, StringComparison.Ordinal);
    }

    [Fact]
    public void CallerWorkflowGenerationAddsOnlyOwnedCallerFiles()
    {
        using Fixture fixture = new();
        fixture.Run(includeCallerWorkflows: false).AssertSuccess();
        Dictionary<string, string> core = fixture.GeneratedCoreHashes();
        fixture.Run(includeCallerWorkflows: true).AssertSuccess();

        Assert.Equal(core, fixture.GeneratedCoreHashes());
        string ci = File.ReadAllText(Path.Combine(fixture.Repository, ".github", "workflows", "ci.yml"));
        string release = File.ReadAllText(Path.Combine(fixture.Repository, ".github", "workflows", "release.yml"));
        Assert.Contains("reusable-mod-build.yml@" + Commit, ci, StringComparison.Ordinal);
        Assert.Contains("modkit-commit: " + Commit, ci, StringComparison.Ordinal);
        Assert.Contains("reusable-mod-publish.yml@" + Commit, release, StringComparison.Ordinal);
        Assert.Contains("modkit_read_token: ${{ secrets.MODKIT_READ_TOKEN }}", ci, StringComparison.Ordinal);
        Assert.Contains("modkit_read_token: ${{ secrets.MODKIT_READ_TOKEN }}", release, StringComparison.Ordinal);
        Assert.Contains("attestations: read", release, StringComparison.Ordinal);
        Assert.DoesNotContain("secrets: inherit", ci + release, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pull_request_target", ci + release, StringComparison.Ordinal);
        foreach (string line in (ci + "\n" + release).Split('\n').Where(line => line.TrimStart().StartsWith("uses:", StringComparison.Ordinal)))
            Assert.Matches("@[0-9a-f]{40}$", line.Trim());

        string dependabot = File.ReadAllText(Path.Combine(fixture.Repository, ".github", "dependabot.yml"));
        Assert.Contains("package-ecosystem: github-actions", dependabot, StringComparison.Ordinal);
        Assert.DoesNotContain("package-ecosystem: nuget", dependabot, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedWrappersResolveBeforeCallingLockedTooling()
    {
        using Fixture fixture = new();
        fixture.Run(includeCallerWorkflows: false).AssertSuccess();
        foreach (string name in new[] { "build.ps1", "pack.ps1" })
        {
            string text = File.ReadAllText(Path.Combine(fixture.Repository, "scripts", name));
            int resolve = text.IndexOf("Resolve-ModKit.ps1", StringComparison.Ordinal);
            int shared = text.IndexOf(name == "build.ps1" ? "Invoke-ModBuild.ps1" : "Pack-Mod.ps1", StringComparison.Ordinal);
            Assert.True(resolve >= 0 && shared > resolve, name);
            Assert.Contains("modkit.lock.json", text, StringComparison.Ordinal);
        }
        Assert.Equal(
            File.ReadAllBytes(Path.Combine(Root, "scripts", "Resolve-ModKit.ps1")),
            File.ReadAllBytes(Path.Combine(fixture.Repository, "scripts", "Resolve-ModKit.ps1")));
    }

    [Fact]
    public void GeneratorRejectsOwnedSymlinkAndInvalidModSchemaBeforeMutation()
    {
        using Fixture fixture = new();
        string outside = Path.Combine(fixture.DirectoryPath, "outside.txt");
        File.WriteAllText(outside, "outside", new UTF8Encoding(false));
        string link = Path.Combine(fixture.Repository, ".gitignore");
        try
        {
            File.CreateSymbolicLink(link, outside);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new Xunit.Sdk.XunitException($"Symbolic-link test setup failed: {exception.Message}");
        }
        fixture.Run(includeCallerWorkflows: false).AssertFailure("symlink");
        Assert.Equal("outside", File.ReadAllText(outside));
        Assert.False(File.Exists(Path.Combine(fixture.Repository, "global.json")));

        File.Delete(link);
        string config = File.ReadAllText(fixture.ModConfig).TrimEnd().TrimEnd('}') + ",\n  \"extra\": true\n}\n";
        File.WriteAllText(fixture.ModConfig, config, new UTF8Encoding(false));
        fixture.Run(includeCallerWorkflows: false).AssertFailure("extra");
        Assert.False(File.Exists(Path.Combine(fixture.Repository, "global.json")));
    }

    [Fact]
    public void GeneratedSourceMappingSelectsVerifiedRefBytesAndCoversLockedGraph()
    {
        using Fixture fixture = new();
        fixture.Run(includeCallerWorkflows: false).AssertSuccess();
        string verifiedFeed = Path.Combine(fixture.DirectoryPath, "verified-feed");
        string normalFeed = Path.Combine(fixture.DirectoryPath, "normal-feed");
        string hostileFeed = Path.Combine(fixture.DirectoryPath, "hostile-feed");
        string bepFeed = Path.Combine(fixture.DirectoryPath, "bep-feed");
        string nugetFeed = Path.Combine(fixture.DirectoryPath, "nuget-feed");
        Directory.CreateDirectory(verifiedFeed);
        Directory.CreateDirectory(normalFeed);
        Directory.CreateDirectory(hostileFeed);
        Directory.CreateDirectory(bepFeed);
        Directory.CreateDirectory(nugetFeed);
        string verified = fixture.CreatePackage("verified", "FarmTogether2.GameApi.Ref", "VerifiedMarker", verifiedFeed);
        _ = fixture.CreatePackage("hostile", "FarmTogether2.GameApi.Ref", "HostileMarker", hostileFeed);
        string normal = fixture.CreatePackage("normal", "Fixture.Dependency", "NormalMarker", normalFeed);
        using (FileStream stream = File.OpenRead(normal))
        using (PackageArchiveReader reader = new(stream))
            Assert.Equal("Fixture.Dependency", reader.GetIdentity().Id);

        string configPath = Path.Combine(fixture.Repository, "NuGet.config");
        XDocument config = XDocument.Load(configPath);
        XElement sources = config.Root!.Element("packageSources")!;
        sources.Elements("add").Single(element => (string?)element.Attribute("key") == "FarmTogether2-ModKit").SetAttributeValue("value", verifiedFeed);
        sources.Elements("add").Single(element => (string?)element.Attribute("key") == "BepInEx").SetAttributeValue("value", bepFeed);
        sources.Elements("add").Single(element => (string?)element.Attribute("key") == "nuget.org").SetAttributeValue("value", nugetFeed);
        sources.Add(new XElement("add", new XAttribute("key", "Normal"), new XAttribute("value", normalFeed)));
        sources.Add(new XElement("add", new XAttribute("key", "Hostile"), new XAttribute("value", hostileFeed)));
        XElement sourceMapping = config.Root.Element("packageSourceMapping")!;
        sourceMapping.Elements("packageSource")
            .Single(element => (string?)element.Attribute("key") == "nuget.org")
            .Elements("package").Single(element => (string?)element.Attribute("pattern") == "System.*").Remove();
        sourceMapping.Add(
            new XElement("packageSource", new XAttribute("key", "Normal"),
                new XElement("package", new XAttribute("pattern", "Fixture.Dependency"))));
        sourceMapping.Add(
            new XElement("packageSource", new XAttribute("key", "Hostile"),
                new XElement("package", new XAttribute("pattern", "Hostile.Only"))));
        config.Save(configPath, SaveOptions.DisableFormatting);

        string generated = Path.Combine(fixture.Repository, ".modkit", "generated");
        Directory.CreateDirectory(generated);
        File.WriteAllText(Path.Combine(generated, "ModKit.lock.props"), $$"""
            <Project>
              <PropertyGroup>
                <FarmTogether2GameApiRefVersion>1.0.0</FarmTogether2GameApiRefVersion>
                <FarmTogether2ModKitPackageSource>{{verifiedFeed}}</FarmTogether2ModKitPackageSource>
              </PropertyGroup>
            </Project>
            """.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n", new UTF8Encoding(false));
        string nested = Path.Combine(fixture.Repository, "src", "Nested", "Consumer");
        Directory.CreateDirectory(nested);
        string consumer = Path.Combine(nested, "Consumer.csproj");
        File.WriteAllText(consumer, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="FarmTogether2.GameApi.Ref" Version="1.0.0" />
                <PackageReference Include="Fixture.Dependency" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(nested, "Class1.cs"), "public sealed class Class1 { }\n", new UTF8Encoding(false));
        string packages = Path.Combine(fixture.DirectoryPath, "restored-packages");
        ProcessResult restore = fixture.RunDotNet([
            "restore", consumer, "--configfile", configPath, "--packages", packages, "--no-cache", "--use-lock-file"
        ]);
        Assert.True(
            restore.ExitCode == 0,
            $"stdout:\n{restore.Stdout}\nstderr:\n{restore.Stderr}\nconfig:\n{File.ReadAllText(configPath)}\nnormal feed:\n{string.Join("\n", Directory.EnumerateFiles(normalFeed))}");
        string cached = Path.Combine(packages, "farmtogether2.gameapi.ref", "1.0.0", "farmtogether2.gameapi.ref.1.0.0.nupkg");
        Assert.Equal(Sha256(verified), Sha256(cached));

        config = XDocument.Load(configPath);
        XElement normalMapping = config.Root!.Element("packageSourceMapping")!.Elements("packageSource")
            .Single(element => (string?)element.Attribute("key") == "Normal");
        normalMapping.Elements("package").Single(element => (string?)element.Attribute("pattern") == "Fixture.Dependency").Remove();
        config.Save(configPath, SaveOptions.DisableFormatting);
        Directory.Delete(Path.Combine(nested, "obj"), recursive: true);
        ProcessResult unmappedRestore = fixture.RunDotNet([
            "restore", consumer, "--configfile", configPath, "--packages", Path.Combine(fixture.DirectoryPath, "unmapped-packages"), "--no-cache", "--force-evaluate"
        ]);
        Assert.True(unmappedRestore.ExitCode != 0, $"stdout:\n{unmappedRestore.Stdout}\nstderr:\n{unmappedRestore.Stderr}");
    }

    private static Dictionary<string, string> Snapshot(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(
            path => Path.GetRelativePath(root, path).Replace('\\', '/'),
            Sha256,
            StringComparer.Ordinal);

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), $"modkit-generator-{Guid.NewGuid():N}");
            Repository = Path.Combine(DirectoryPath, "repository");
            Directory.CreateDirectory(Path.Combine(Repository, "src", "Fixture"));
            Directory.CreateDirectory(Path.Combine(Repository, "tests"));
            Directory.CreateDirectory(Path.Combine(Repository, "packaging"));
            File.WriteAllText(Path.Combine(Repository, "src", "Fixture", "Fixture.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(Repository, "tests", "Guard.ps1"), "Write-Host guard\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(Repository, "packaging", "README_安装说明.txt"), "install\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(Repository, "README.md"), "protected readme\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(Repository, "CHANGELOG.md"), "protected changelog\n", new UTF8Encoding(false));
            ModConfig = Path.Combine(Repository, "mod.json");
            File.WriteAllText(ModConfig, """
                {
                  "schemaVersion": 1,
                  "id": "com.abmcar.farmtogether2.fixture",
                  "displayName": "FarmTogether2.Fixture",
                  "assemblyName": "FarmTogether2.Fixture",
                  "version": "1.0.0",
                  "project": "src/Fixture/Fixture.csproj",
                  "testProjects": [],
                  "guardScripts": ["tests/Guard.ps1"],
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
        public string ModConfig { get; }

        public ProcessResult Run(bool includeCallerWorkflows)
        {
            List<string> args = ["-NoLogo", "-NoProfile", "-File", Script, "-RepositoryRoot", Repository];
            if (includeCallerWorkflows)
                args.Add("-IncludeCallerWorkflows");
            return RunProcess("pwsh", args);
        }

        public Dictionary<string, string> ProtectedHashes() => new[]
        {
            "mod.json", "modkit.lock.json", "README.md", "CHANGELOG.md", "src/Fixture/Fixture.csproj",
            "tests/Guard.ps1", "packaging/README_安装说明.txt"
        }.ToDictionary(path => path, path => Sha256(Path.Combine(Repository, path)), StringComparer.Ordinal);

        public Dictionary<string, string> GeneratedCoreHashes() => new[]
        {
            ".gitattributes", ".gitignore", "global.json", "NuGet.config", "Directory.Build.props",
            "Directory.Build.targets", "LICENSE", "scripts/Resolve-ModKit.ps1", "scripts/build.ps1", "scripts/pack.ps1"
        }.ToDictionary(path => path, path => Sha256(Path.Combine(Repository, path)), StringComparer.Ordinal);

        public string CreatePackage(string label, string packageId, string marker, string output)
        {
            string root = Path.Combine(DirectoryPath, "package-" + label);
            Directory.CreateDirectory(root);
            string project = Path.Combine(root, label + ".csproj");
            File.WriteAllText(project, $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                    <PackageId>{{packageId}}</PackageId>
                    <Version>1.0.0</Version>
                    <AssemblyName>{{packageId}}</AssemblyName>
                  </PropertyGroup>
                </Project>
                """.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(root, "Marker.cs"), $"public sealed class {marker} {{ }}\n", new UTF8Encoding(false));
            RunDotNet(["pack", project, "-c", "Release", "-o", output]).AssertSuccess();
            return Assert.Single(Directory.EnumerateFiles(output, packageId + ".1.0.0.nupkg"));
        }

        public ProcessResult RunDotNet(IEnumerable<string> arguments) => RunProcess("dotnet", arguments);

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
                Directory.Delete(DirectoryPath, recursive: true);
        }
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
        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {fileName}.");
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
