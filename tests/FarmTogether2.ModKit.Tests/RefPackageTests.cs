using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using Mono.Cecil;
using NuGet.Packaging;
using Xunit;

namespace FarmTogether2.ModKit.Tests;

[Trait("Category", "LongRunning")]
public sealed class RefPackageTests
{
    private const string PackageId = "FarmTogether2.GameApi.Ref";
    private const string Version = "1.0.0";
    private static readonly DateTime FixedTimestamp = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private static readonly string ToolAssembly = Path.Combine(
        Root,
        "tools",
        "FarmTogether2.ModKit.Tool",
        "bin",
        TestBuildConfiguration.Current,
        "net10.0",
        "FarmTogether2.ModKit.Tool.dll");
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
        string firstRoot = fixture.CreateAssemblyRoot("first", deterministicMvid: false, timestamp: 0x12345678);
        string secondRoot = fixture.CreateAssemblyRoot("second", deterministicMvid: false, timestamp: 0x87654321);
        Assert.All(Assemblies.Keys, name =>
            Assert.NotEqual(
                Sha256(Path.Combine(firstRoot, $"{name}.dll")),
                Sha256(Path.Combine(secondRoot, $"{name}.dll"))));
        string first = fixture.WritePackage(firstRoot, "first-output").AssertSuccess().PackagePath;
        Thread.Sleep(TimeSpan.FromSeconds(1));
        string second = fixture.WritePackage(secondRoot, "second-output").AssertSuccess().PackagePath;

        Assert.Equal(Sha256(first), Sha256(second));
        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
        AssertCanonicalZipEncoding(first);

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
    public void VerifierRejectsSemanticallyEquivalentNoncanonicalAssemblyEncoding()
    {
        using Fixture fixture = new();
        string root = fixture.CreateAssemblyRoot("input");
        string package = fixture.WritePackage(root, "output").AssertSuccess().PackagePath;
        string replacementPath = Path.Combine(fixture.DirectoryPath, "replacement.dll");
        WriteAssembly(
            replacementPath,
            "Assembly-CSharp",
            new Version(0, 0, 0, 0),
            deterministicMvid: false,
            timestamp: 0x12345678);

        using (ZipArchive archive = ZipFile.Open(package, ZipArchiveMode.Update))
        {
            ZipArchiveEntry entry = archive.GetEntry("ref/net6.0/Assembly-CSharp.dll")
                ?? throw new Xunit.Sdk.XunitException("Reference package is missing Assembly-CSharp.dll.");
            using Stream stream = entry.Open();
            stream.SetLength(0);
            stream.Write(File.ReadAllBytes(replacementPath));
        }

        fixture.VerifyPackage(package).AssertFailure("assembly encoding is noncanonical");
    }

    [Fact]
    public void VerifierRejectsPlatformDependentZipEncoding()
    {
        using Fixture fixture = new();
        string root = fixture.CreateAssemblyRoot("input");
        string package = fixture.WritePackage(root, "output").AssertSuccess().PackagePath;
        string replacement = Path.Combine(fixture.DirectoryPath, "platform-zip.nupkg");
        using (ZipArchive source = ZipFile.OpenRead(package))
        using (ZipArchive destination = ZipFile.Open(replacement, ZipArchiveMode.Create))
        {
            foreach (ZipArchiveEntry sourceEntry in source.Entries)
            {
                ZipArchiveEntry destinationEntry = destination.CreateEntry(sourceEntry.FullName, CompressionLevel.Optimal);
                destinationEntry.LastWriteTime = new DateTimeOffset(FixedTimestamp, TimeSpan.Zero);
                destinationEntry.ExternalAttributes = 0;
                using Stream input = sourceEntry.Open();
                using Stream output = destinationEntry.Open();
                input.CopyTo(output);
            }
        }
        File.Move(replacement, package, overwrite: true);

        fixture.VerifyPackage(package).AssertFailure("ZIP encoding is noncanonical");
    }

    [Fact]
    public void WriterCanonicalizesPortablePdbDebugIdentityFromDifferentBuildRoots()
    {
        using Fixture fixture = new();
        string firstRoot = fixture.CreateAssemblyRoot("first");
        string secondRoot = fixture.CreateAssemblyRoot("second", firstRoot);
        fixture.BuildPortableDebugAssembly("debug-build-first", Path.Combine(firstRoot, "Assembly-CSharp.dll"));
        fixture.BuildPortableDebugAssembly("debug-build-second", Path.Combine(secondRoot, "Assembly-CSharp.dll"));
        Assert.NotEqual(
            Sha256(Path.Combine(firstRoot, "Assembly-CSharp.dll")),
            Sha256(Path.Combine(secondRoot, "Assembly-CSharp.dll")));

        string first = fixture.WritePackage(firstRoot, "first-output").AssertSuccess().PackagePath;
        string second = fixture.WritePackage(secondRoot, "second-output").AssertSuccess().PackagePath;

        Assert.Equal(Sha256(first), Sha256(second));
        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
    }

    [Fact]
    public void CanonicalZipWriterRejectsZip64SentinelEntryCount()
    {
        SortedDictionary<string, byte[]> entries = new(StringComparer.Ordinal);
        for (int index = 0; index < ushort.MaxValue; index++)
            entries.Add(index.ToString("D5", System.Globalization.CultureInfo.InvariantCulture), []);
        System.Reflection.Assembly tool = System.Reflection.Assembly.LoadFrom(ToolAssembly);
        Type writer = tool.GetType("FarmTogether2.ModKit.Tool.CanonicalZipWriter", throwOnError: true)!;
        System.Reflection.MethodInfo method = Assert.Single(writer.GetMethods(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static),
            candidate => candidate.Name == "Write" && candidate.GetParameters().Length == 1);

        System.Reflection.TargetInvocationException exception = Assert.Throws<System.Reflection.TargetInvocationException>(
            () => method.Invoke(null, [entries]));
        InvalidDataException failure = Assert.IsType<InvalidDataException>(exception.InnerException);
        Assert.Contains("too many entries", failure.Message, StringComparison.Ordinal);
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
                <TargetFramework>net10.0</TargetFramework>
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
    [InlineData("retargetable")]
    [InlineData("windows-runtime")]
    [InlineData("hash-algorithm")]
    [InlineData("console-module")]
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
                culture: mutation == "culture" ? "fr-FR" : null,
                attributes: mutation switch
                {
                    "retargetable" => AssemblyAttributes.Retargetable,
                    "windows-runtime" => AssemblyAttributes.WindowsRuntime,
                    _ => (AssemblyAttributes)0
                },
                hashAlgorithm: mutation == "hash-algorithm"
                    ? Mono.Cecil.AssemblyHashAlgorithm.MD5
                    : Mono.Cecil.AssemblyHashAlgorithm.SHA1,
                moduleKind: mutation == "console-module" ? ModuleKind.Console : ModuleKind.Dll);
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
        string? culture = null,
        bool deterministicMvid = true,
        uint timestamp = 0,
        AssemblyAttributes attributes = (AssemblyAttributes)0,
        Mono.Cecil.AssemblyHashAlgorithm hashAlgorithm = Mono.Cecil.AssemblyHashAlgorithm.SHA1,
        ModuleKind moduleKind = ModuleKind.Dll)
    {
        AssemblyNameDefinition identity = new(name, version);
        identity.Culture = culture;
        identity.Attributes |= attributes;
        identity.HashAlgorithm = hashAlgorithm;
        if (publicKey is not null)
        {
            identity.PublicKey = publicKey;
            identity.Attributes |= AssemblyAttributes.PublicKey;
        }
        using AssemblyDefinition assembly = AssemblyDefinition.CreateAssembly(identity, name, moduleKind);
        assembly.Write(path, new WriterParameters { DeterministicMvid = deterministicMvid, Timestamp = timestamp });
    }

    private static void AssertCanonicalZipEncoding(string path)
    {
        const uint localHeaderSignature = 0x04034b50;
        const uint centralHeaderSignature = 0x02014b50;
        const uint endSignature = 0x06054b50;
        const ushort version20 = 20;
        const ushort utf8Flag = 1 << 11;
        const ushort fixedDosDate = ((2000 - 1980) << 9) | (1 << 5) | 1;

        using FileStream stream = File.OpenRead(path);
        using BinaryReader reader = new(stream, System.Text.Encoding.UTF8, leaveOpen: false);
        Assert.True(stream.Length >= 22);
        stream.Position = stream.Length - 22;
        Assert.Equal(endSignature, reader.ReadUInt32());
        Assert.Equal((ushort)0, reader.ReadUInt16());
        Assert.Equal((ushort)0, reader.ReadUInt16());
        ushort entriesOnDisk = reader.ReadUInt16();
        ushort totalEntries = reader.ReadUInt16();
        Assert.Equal(entriesOnDisk, totalEntries);
        uint centralLength = reader.ReadUInt32();
        uint centralOffset = reader.ReadUInt32();
        Assert.Equal((ushort)0, reader.ReadUInt16());
        Assert.Equal(stream.Length - 22, centralOffset + centralLength);

        stream.Position = centralOffset;
        for (int index = 0; index < totalEntries; index++)
        {
            Assert.Equal(centralHeaderSignature, reader.ReadUInt32());
            Assert.Equal(version20, reader.ReadUInt16());
            Assert.Equal(version20, reader.ReadUInt16());
            Assert.Equal(utf8Flag, reader.ReadUInt16());
            Assert.Equal((ushort)0, reader.ReadUInt16());
            Assert.Equal((ushort)0, reader.ReadUInt16());
            Assert.Equal(fixedDosDate, reader.ReadUInt16());
            uint crc32 = reader.ReadUInt32();
            uint compressedSize = reader.ReadUInt32();
            Assert.Equal(compressedSize, reader.ReadUInt32());
            ushort nameLength = reader.ReadUInt16();
            Assert.Equal((ushort)0, reader.ReadUInt16());
            Assert.Equal((ushort)0, reader.ReadUInt16());
            Assert.Equal((ushort)0, reader.ReadUInt16());
            Assert.Equal((ushort)0, reader.ReadUInt16());
            Assert.Equal(0u, reader.ReadUInt32());
            uint localOffset = reader.ReadUInt32();
            byte[] name = reader.ReadBytes(nameLength);
            if (System.Text.Encoding.UTF8.GetString(name) == "FarmTogether2.GameApi.Ref.nuspec")
                Assert.Equal(0x75d28247u, crc32);
            long nextCentralHeader = stream.Position;

            stream.Position = localOffset;
            Assert.Equal(localHeaderSignature, reader.ReadUInt32());
            Assert.Equal(version20, reader.ReadUInt16());
            Assert.Equal(utf8Flag, reader.ReadUInt16());
            Assert.Equal((ushort)0, reader.ReadUInt16());
            Assert.Equal((ushort)0, reader.ReadUInt16());
            Assert.Equal(fixedDosDate, reader.ReadUInt16());
            Assert.Equal(crc32, reader.ReadUInt32());
            Assert.Equal(compressedSize, reader.ReadUInt32());
            Assert.Equal(compressedSize, reader.ReadUInt32());
            Assert.Equal(nameLength, reader.ReadUInt16());
            Assert.Equal((ushort)0, reader.ReadUInt16());
            Assert.Equal(name, reader.ReadBytes(nameLength));
            stream.Position = nextCentralHeader;
        }
        Assert.Equal(centralOffset + centralLength, stream.Position);
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

        public string CreateAssemblyRoot(
            string name,
            string? copyFrom = null,
            bool deterministicMvid = true,
            uint timestamp = 0)
        {
            string root = Path.Combine(DirectoryPath, name);
            Directory.CreateDirectory(root);
            foreach ((string assemblyName, Version version) in Assemblies)
            {
                string destination = Path.Combine(root, $"{assemblyName}.dll");
                if (copyFrom is null)
                    WriteAssembly(
                        destination,
                        assemblyName,
                        version,
                        deterministicMvid: deterministicMvid,
                        timestamp: timestamp);
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
                ToolAssembly,
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

        public void BuildPortableDebugAssembly(string name, string destination)
        {
            string projectRoot = Path.Combine(DirectoryPath, name);
            Directory.CreateDirectory(projectRoot);
            string project = Path.Combine(projectRoot, "Assembly-CSharp.csproj");
            File.WriteAllText(
                project,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net6.0</TargetFramework>
                    <AssemblyName>Assembly-CSharp</AssemblyName>
                    <AssemblyVersion>0.0.0.0</AssemblyVersion>
                    <DebugType>portable</DebugType>
                    <DebugSymbols>true</DebugSymbols>
                    <Deterministic>true</Deterministic>
                    <RestorePackagesWithLockFile>false</RestorePackagesWithLockFile>
                    <NuGetAudit>false</NuGetAudit>
                  </PropertyGroup>
                </Project>
                """.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n",
                new System.Text.UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(projectRoot, "Probe.cs"),
                "public sealed class Probe { public int Value => 42; }\n",
                new System.Text.UTF8Encoding(false));
            RunDotNet(["build", project, "-c", "Release", "-warnaserror"]).AssertSuccess();
            string assembly = Path.Combine(projectRoot, "bin", "Release", "net6.0", "Assembly-CSharp.dll");
            string symbols = Path.Combine(projectRoot, "bin", "Release", "net6.0", "Assembly-CSharp.pdb");
            Assert.True(File.Exists(assembly));
            Assert.True(File.Exists(symbols));
            File.Copy(assembly, destination, overwrite: true);
        }

        public ProcessResult VerifyPackage(string package)
        {
            List<string> arguments =
            [
                ToolAssembly,
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
