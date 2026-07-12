using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using Mono.Cecil;
using NuGet.Packaging;
using Xunit;

namespace FarmTogether2.ModKit.Tests;

public sealed class RefPackageTests
{
    private const string PackageId = "FarmTogether2.GameApi.Ref";
    private const string Version = "1.0.0";
    private static readonly DateTime FixedTimestamp = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private static readonly string ToolProject = Path.Combine(Root, "tools", "FarmTogether2.ModKit.Tool", "FarmTogether2.ModKit.Tool.csproj");
    private static readonly IReadOnlyDictionary<string, Version> Assemblies =
        new Dictionary<string, Version>(StringComparer.Ordinal)
        {
            ["Assembly-CSharp"] = new(0, 0, 0, 0),
            ["Il2Cppmscorlib"] = new(4, 0, 0, 0),
            ["MilkstoneUnityExtensions"] = new(1, 0, 0, 0),
            ["UnityEngine.CoreModule"] = new(0, 0, 0, 0),
            ["UnityEngine.IMGUIModule"] = new(0, 0, 0, 0),
            ["UnityEngine.InputLegacyModule"] = new(0, 0, 0, 0),
            ["UnityEngine.TextRenderingModule"] = new(0, 0, 0, 0)
        };

    [Fact]
    public async Task WriterProducesByteIdenticalClosedPackagesFromDifferentRoots()
    {
        using Fixture fixture = new();
        string firstRoot = fixture.CreateAssemblyRoot("first");
        string secondRoot = fixture.CreateAssemblyRoot("second", firstRoot);
        string first = fixture.WritePackage(firstRoot, "first-output").AssertSuccess().PackagePath;
        Thread.Sleep(TimeSpan.FromSeconds(1));
        string second = fixture.WritePackage(secondRoot, "second-output").AssertSuccess().PackagePath;

        Assert.Equal(Sha256(first), Sha256(second));
        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));

        using FileStream stream = File.OpenRead(first);
        using PackageArchiveReader reader = new(stream);
        Assert.False(await reader.IsSignedAsync(CancellationToken.None));
        Assert.Equal(PackageId, reader.GetIdentity().Id);
        Assert.Equal(Version, reader.GetIdentity().Version.ToNormalizedString());
        string[] references = reader.GetFiles()
            .Where(path => path.StartsWith("ref/net6.0/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            Assemblies.Keys.Select(name => $"ref/net6.0/{name}.dll").Order(StringComparer.Ordinal),
            references);
        Assert.DoesNotContain(reader.GetFiles(), path =>
            path.StartsWith("lib/", StringComparison.Ordinal) ||
            path.StartsWith("runtimes/", StringComparison.Ordinal) ||
            path.StartsWith("tools/", StringComparison.Ordinal) ||
            path.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) ||
            (path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && path != "[Content_Types].xml"));

        using ZipArchive archive = new(File.OpenRead(first), ZipArchiveMode.Read);
        string[] names = archive.Entries.Select(entry => entry.FullName).ToArray();
        Assert.Equal(names.Order(StringComparer.Ordinal), names);
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        Assert.All(archive.Entries, entry =>
        {
            Assert.Equal(FixedTimestamp, entry.LastWriteTime.DateTime);
            Assert.Equal(0, entry.ExternalAttributes);
        });
    }

    [Fact]
    public void WriterRejectsMissingDuplicateAndUnexpectedAssemblies()
    {
        using Fixture fixture = new();
        string root = fixture.CreateAssemblyRoot("input");
        string[] paths = AssemblyPaths(root);

        fixture.WritePackage(root, "missing", paths[..^1]).AssertFailure("Exactly 7");
        fixture.WritePackage(root, "duplicate", paths[..^1].Append(paths[0])).AssertFailure("Duplicate");
        string unexpected = Path.Combine(root, "Unexpected.dll");
        File.Copy(paths[0], unexpected);
        fixture.WritePackage(root, "unexpected", paths[..^1].Append(unexpected)).AssertFailure("Unexpected");
    }

    [Fact]
    public void WriterRejectsNoncanonicalAndStrongNamedAssemblyIdentity()
    {
        using Fixture fixture = new();
        string root = fixture.CreateAssemblyRoot("input");
        WriteAssembly(Path.Combine(root, "Assembly-CSharp.dll"), "Assembly-CSharp", new Version(1, 0, 0, 0));
        fixture.WritePackage(root, "wrong-version").AssertFailure("noncanonical");

        root = fixture.CreateAssemblyRoot("signed");
        WriteAssembly(
            Path.Combine(root, "Assembly-CSharp.dll"),
            "Assembly-CSharp",
            new Version(0, 0, 0, 0),
            publicKey: [1, 2, 3, 4]);
        fixture.WritePackage(root, "signed-output").AssertFailure("public key");
    }

    [Fact]
    public void WrittenPackageRestoresAndBuildsFromAnIsolatedLocalSource()
    {
        using Fixture fixture = new();
        string assemblyRoot = fixture.CreateAssemblyRoot("input");
        PackageResult package = fixture.WritePackage(assemblyRoot, "source").AssertSuccess();
        string consumer = Path.Combine(fixture.DirectoryPath, "consumer");
        Directory.CreateDirectory(consumer);
        string project = Path.Combine(consumer, "Consumer.csproj");
        File.WriteAllText(
            project,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <RestorePackagesWithLockFile>false</RestorePackagesWithLockFile>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="FarmTogether2.GameApi.Ref" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """.Replace("\r\n", "\n", StringComparison.Ordinal),
            new System.Text.UTF8Encoding(false));
        File.WriteAllText(Path.Combine(consumer, "Class1.cs"), "public sealed class Class1 { }\n", new System.Text.UTF8Encoding(false));
        string packages = Path.Combine(fixture.DirectoryPath, "restored-packages");

        fixture.RunDotNet([
            "restore", project,
            "--source", Path.GetDirectoryName(package.PackagePath)!,
            "--packages", packages,
            "--no-cache"
        ]).AssertSuccess();
        fixture.RunDotNet(["build", project, "-c", "Release", "--no-restore"]).AssertSuccess();

        string assets = File.ReadAllText(Path.Combine(consumer, "obj", "project.assets.json"));
        Assert.Contains("FarmTogether2.GameApi.Ref/1.0.0", assets, StringComparison.Ordinal);
        foreach (string name in Assemblies.Keys)
            Assert.Contains($"ref/net6.0/{name}.dll", assets, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifierRejectsAnyExtraOrSignatureEntry()
    {
        using Fixture fixture = new();
        string root = fixture.CreateAssemblyRoot("input");
        string package = fixture.WritePackage(root, "output").AssertSuccess().PackagePath;
        using (ZipArchive archive = ZipFile.Open(package, ZipArchiveMode.Update))
        {
            ZipArchiveEntry extra = archive.CreateEntry("unexpected.txt");
            extra.LastWriteTime = new DateTimeOffset(FixedTimestamp, TimeSpan.Zero);
            extra.ExternalAttributes = 0;
        }
        fixture.VerifyPackage(package).AssertFailure("closed package layout");

        package = fixture.WritePackage(root, "signed-output").AssertSuccess().PackagePath;
        using (ZipArchive archive = ZipFile.Open(package, ZipArchiveMode.Update))
        {
            ZipArchiveEntry signature = archive.CreateEntry(".signature.p7s");
            signature.LastWriteTime = new DateTimeOffset(FixedTimestamp, TimeSpan.Zero);
            signature.ExternalAttributes = 0;
        }
        fixture.VerifyPackage(package).AssertFailure();
    }

    [Theory]
    [InlineData("invalid-image")]
    [InlineData("wrong-name")]
    [InlineData("wrong-version")]
    [InlineData("public-key")]
    [InlineData("culture")]
    public void VerifierRejectsTamperedAssemblyIdentity(string mutation)
    {
        using Fixture fixture = new();
        string root = fixture.CreateAssemblyRoot("input");
        string package = fixture.WritePackage(root, "output").AssertSuccess().PackagePath;
        byte[] replacement;
        if (mutation == "invalid-image")
        {
            replacement = "not-an-assembly"u8.ToArray();
        }
        else
        {
            string replacementPath = Path.Combine(fixture.DirectoryPath, "replacement.dll");
            WriteAssembly(
                replacementPath,
                mutation == "wrong-name" ? "Wrong-Assembly" : "Assembly-CSharp",
                mutation == "wrong-version" ? new Version(1, 0, 0, 0) : new Version(0, 0, 0, 0),
                publicKey: mutation == "public-key" ? [1, 2, 3, 4] : null,
                culture: mutation == "culture" ? "fr-FR" : null);
            replacement = File.ReadAllBytes(replacementPath);
        }

        using (ZipArchive archive = ZipFile.Open(package, ZipArchiveMode.Update))
        {
            ZipArchiveEntry entry = archive.GetEntry("ref/net6.0/Assembly-CSharp.dll")
                ?? throw new Xunit.Sdk.XunitException("Reference package is missing Assembly-CSharp.dll.");
            using Stream stream = entry.Open();
            stream.SetLength(0);
            stream.Write(replacement);
        }

        fixture.VerifyPackage(package).AssertFailure("assembly");
    }

    [Fact]
    public void WriterRejectsSymlinkAncestorWithoutTouchingItsTarget()
    {
        using Fixture fixture = new();
        string root = fixture.CreateAssemblyRoot("input");
        string outside = Path.Combine(fixture.DirectoryPath, "outside");
        string link = Path.Combine(fixture.DirectoryPath, "linked-output");
        Directory.CreateDirectory(outside);
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new Xunit.Sdk.XunitException($"Symbolic-link test setup failed: {exception.Message}");
        }

        fixture.WritePackage(root, link).AssertFailure("symlink");

        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    [Theory]
    [InlineData("1.0.0-beta")]
    [InlineData("01.0.0")]
    [InlineData("1.0")]
    public void WriterRejectsNoncanonicalPackageVersion(string version)
    {
        using Fixture fixture = new();
        string root = fixture.CreateAssemblyRoot("input");
        fixture.WritePackage(root, "output", version: version).AssertFailure("canonical stable SemVer");
    }

    private static string[] AssemblyPaths(string root) => Assemblies.Keys
        .Order(StringComparer.Ordinal)
        .Select(name => Path.Combine(root, $"{name}.dll"))
        .ToArray();

    private static void WriteAssembly(
        string path,
        string name,
        Version version,
        byte[]? publicKey = null,
        string? culture = null)
    {
        AssemblyNameDefinition identity = new(name, version);
        identity.Culture = culture;
        if (publicKey is not null)
        {
            identity.PublicKey = publicKey;
            identity.Attributes |= AssemblyAttributes.PublicKey;
        }
        using AssemblyDefinition assembly = AssemblyDefinition.CreateAssembly(identity, name, ModuleKind.Dll);
        assembly.Write(path, new WriterParameters { DeterministicMvid = true });
    }

    private static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), $"modkit-ref-package-{Guid.NewGuid():N}");
            Directory.CreateDirectory(DirectoryPath);
        }

        public string DirectoryPath { get; }

        public string CreateAssemblyRoot(string name, string? copyFrom = null)
        {
            string root = Path.Combine(DirectoryPath, name);
            Directory.CreateDirectory(root);
            foreach ((string assemblyName, Version version) in Assemblies)
            {
                string destination = Path.Combine(root, $"{assemblyName}.dll");
                if (copyFrom is null)
                    WriteAssembly(destination, assemblyName, version);
                else
                    File.Copy(Path.Combine(copyFrom, $"{assemblyName}.dll"), destination);
            }
            return root;
        }

        public PackageResult WritePackage(
            string assemblyRoot,
            string outputDirectory,
            IEnumerable<string>? assemblyPaths = null,
            string version = Version)
        {
            string outputRoot = Path.Combine(DirectoryPath, outputDirectory);
            Directory.CreateDirectory(outputRoot);
            string package = Path.Combine(outputRoot, $"{PackageId}.{version}.nupkg");
            List<string> arguments =
            [
                "run", "--project", ToolProject, "-c", TestBuildConfiguration.Current, "--no-build", "--",
                "ref-package", "write",
                "--output", package,
                "--package-id", PackageId,
                "--version", version
            ];
            foreach (string path in assemblyPaths ?? AssemblyPaths(assemblyRoot))
            {
                arguments.Add("--assembly");
                arguments.Add(path);
            }
            ProcessStartInfo startInfo = new("dotnet")
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
            return new PackageResult(package, process.ExitCode, stdout, stderr);
        }

        public ProcessResult VerifyPackage(string package)
        {
            List<string> arguments =
            [
                "run", "--project", ToolProject, "-c", TestBuildConfiguration.Current, "--no-build", "--",
                "ref-package", "verify",
                "--package", package,
                "--package-id", PackageId,
                "--version", Version
            ];
            return RunDotNet(arguments);
        }

        public ProcessResult RunDotNet(IEnumerable<string> arguments)
        {
            ProcessStartInfo startInfo = new("dotnet")
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

    private sealed record PackageResult(string PackagePath, int ExitCode, string Stdout, string Stderr)
    {
        public PackageResult AssertSuccess()
        {
            Assert.True(ExitCode == 0, $"stdout:\n{Stdout}\nstderr:\n{Stderr}");
            Assert.True(File.Exists(PackagePath));
            return this;
        }

        public void AssertFailure(string message)
        {
            Assert.NotEqual(0, ExitCode);
            Assert.Contains(message, $"{Stdout}\n{Stderr}", StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(PackagePath));
        }
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr)
    {
        public void AssertSuccess() => Assert.True(ExitCode == 0, $"stdout:\n{Stdout}\nstderr:\n{Stderr}");

        public void AssertFailure(string? expected = null)
        {
            Assert.NotEqual(0, ExitCode);
            if (expected is not null)
                Assert.Contains(expected, $"{Stdout}\n{Stderr}", StringComparison.OrdinalIgnoreCase);
        }
    }
}
