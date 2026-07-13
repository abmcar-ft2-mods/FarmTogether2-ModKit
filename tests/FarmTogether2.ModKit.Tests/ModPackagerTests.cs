using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;
using Xunit;

namespace FarmTogether2.ModKit.Tests;

public sealed class ModPackagerTests
{
    private const string AssemblyName = "FarmTogether2.FixtureMod";
    private const string Version = "1.2.3";
    private static readonly DateTime FixedTimestamp = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private static readonly string ToolProject = Path.Combine(Root, "tools", "FarmTogether2.ModKit.Tool", "FarmTogether2.ModKit.Tool.csproj");

    [Fact]
    public void WriterProducesByteIdenticalAllowlistedArchives()
    {
        using Fixture first = new("first");
        using Fixture second = new("second");

        first.Write().AssertSuccess();
        Thread.Sleep(TimeSpan.FromSeconds(1));
        second.Write().AssertSuccess();

        Assert.Equal(first.AssetHashes(), second.AssetHashes());
        AssertPackageLayout(first.Output);
        first.Verify().AssertSuccess();
    }

    [Fact]
    public void VerifierRejectsExtraAssetAndPlayerDll()
    {
        using Fixture fixture = new();
        fixture.Write().AssertSuccess();
        File.WriteAllText(Path.Combine(fixture.Output, "extra.txt"), "extra", new UTF8Encoding(false));
        fixture.Verify().AssertFailure("exactly");

        File.Delete(Path.Combine(fixture.Output, "extra.txt"));
        using (ZipArchive archive = ZipFile.Open(fixture.PlayerZip, ZipArchiveMode.Update))
        {
            ZipArchiveEntry entry = archive.CreateEntry("BepInEx/plugins/Other/Other.dll");
            entry.LastWriteTime = new DateTimeOffset(FixedTimestamp, TimeSpan.Zero);
        }
        fixture.Verify().AssertFailure("closed");
    }

    [Fact]
    public void WriterRejectsVersionAndAssemblyIdentityMismatchWithoutOutput()
    {
        using Fixture fixture = new();
        fixture.Write(msbuildVersion: "9.9.9").AssertFailure("version");
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Output));

        WriteAssembly(fixture.PluginDll, "FarmTogether2.OtherMod", Version);
        fixture.Write().AssertFailure("assembly");
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Output));
    }

    [Fact]
    public void WriterRejectsSymlinkInputWithoutReadingTarget()
    {
        using Fixture fixture = new();
        string outside = Path.Combine(fixture.DirectoryPath, "outside.dll");
        WriteAssembly(outside, AssemblyName, Version);
        File.Delete(fixture.PluginDll);
        try
        {
            File.CreateSymbolicLink(fixture.PluginDll, outside);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new Xunit.Sdk.XunitException($"Symbolic-link test setup failed: {exception.Message}");
        }

        fixture.Write().AssertFailure("symlink");
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Output));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void ChecksumParserRejectsDuplicateAndUnlistedLines()
    {
        using Fixture fixture = new();
        fixture.Write().AssertSuccess();
        string line = File.ReadAllLines(fixture.Checksums)[0];
        File.AppendAllText(fixture.Checksums, line + "\n", new UTF8Encoding(false));
        fixture.Verify().AssertFailure("checksum");
    }

    [Fact]
    public void VerifierRejectsCorruptCompressedPayloadEvenWhenOuterChecksumIsUpdated()
    {
        using Fixture fixture = new();
        fixture.Write().AssertSuccess();
        byte[] archive = File.ReadAllBytes(fixture.PlayerZip);
        Assert.Equal(0x04034b50u, BinaryPrimitives.ReadUInt32LittleEndian(archive));
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(6));
        Assert.Equal(0, flags & 0x0008);
        int compressedLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(18)));
        int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(26));
        int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(28));
        int payloadOffset = checked(30 + nameLength + extraLength);
        Assert.True(compressedLength > 4);
        archive[payloadOffset + (compressedLength / 2)] ^= 0x5a;
        File.WriteAllBytes(fixture.PlayerZip, archive);
        fixture.UpdatePlayerChecksum();

        fixture.Verify().AssertFailure("payload");
    }

    private static void AssertPackageLayout(string output)
    {
        string playerName = $"{AssemblyName}-v{Version}.zip";
        string symbolsName = $"{AssemblyName}-v{Version}-symbols.zip";
        Assert.Equal(
            new[] { "SHA256SUMS.txt", playerName, symbolsName }.Order(StringComparer.Ordinal),
            Directory.EnumerateFiles(output).Select(Path.GetFileName).Order(StringComparer.Ordinal));

        using ZipArchive player = ZipFile.OpenRead(Path.Combine(output, playerName));
        Assert.Equal(
            new[]
            {
                $"BepInEx/plugins/{AssemblyName}/{AssemblyName}.dll",
                "LICENSE",
                "README_安装说明.txt"
            }.Order(StringComparer.Ordinal),
            player.Entries.Select(entry => entry.FullName));
        Assert.All(player.Entries, entry =>
        {
            Assert.Equal(FixedTimestamp, entry.LastWriteTime.DateTime);
            Assert.Equal(0, entry.ExternalAttributes);
        });

        using ZipArchive symbols = ZipFile.OpenRead(Path.Combine(output, symbolsName));
        ZipArchiveEntry symbol = Assert.Single(symbols.Entries);
        Assert.Equal($"{AssemblyName}.pdb", symbol.FullName);
        Assert.Equal(FixedTimestamp, symbol.LastWriteTime.DateTime);
    }

    private static void WriteAssembly(string path, string name, string version)
    {
        using AssemblyDefinition assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition(name, new Version(version)),
            name,
            ModuleKind.Dll);
        assembly.Write(path, new WriterParameters { DeterministicMvid = true });
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(string? label = null)
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), $"modkit-mod-package-{label}-{Guid.NewGuid():N}");
            Repository = Path.Combine(DirectoryPath, "repository");
            Output = Path.Combine(DirectoryPath, "output");
            string projectDirectory = Path.Combine(Repository, "src", AssemblyName);
            PluginDll = Path.Combine(projectDirectory, AssemblyName + ".dll");
            PluginPdb = Path.Combine(projectDirectory, AssemblyName + ".pdb");
            Directory.CreateDirectory(projectDirectory);
            Directory.CreateDirectory(Path.Combine(Repository, "packaging"));
            Directory.CreateDirectory(Output);
            WriteAssembly(PluginDll, AssemblyName, Version);
            File.WriteAllBytes(PluginPdb, [1, 2, 3, 4]);
            File.WriteAllText(Path.Combine(Repository, "packaging", "README_安装说明.txt"), "Install\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(Repository, "LICENSE"), "MIT\n", new UTF8Encoding(false));
            ModConfig = Path.Combine(Repository, "mod.json");
            File.WriteAllText(
                ModConfig,
                $$"""
                {
                  "schemaVersion": 1,
                  "id": "com.abmcar.farmtogether2.fixturemod",
                  "displayName": "{{AssemblyName}}",
                  "assemblyName": "{{AssemblyName}}",
                  "version": "{{Version}}",
                  "project": "src/{{AssemblyName}}/{{AssemblyName}}.csproj",
                  "testProjects": [],
                  "guardScripts": [],
                  "installReadme": "packaging/README_安装说明.txt",
                  "supportedSteamBuild": "24069957"
                }
                """.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n",
                new UTF8Encoding(false));
        }

        public string DirectoryPath { get; }
        public string Repository { get; }
        public string Output { get; }
        public string ModConfig { get; }
        public string PluginDll { get; }
        public string PluginPdb { get; }
        public string PlayerZip => Path.Combine(Output, $"{AssemblyName}-v{Version}.zip");
        public string Checksums => Path.Combine(Output, "SHA256SUMS.txt");

        public ProcessResult Write(string msbuildVersion = Version) => Run(
        [
            "mod-package", "write",
            "--mod-config", ModConfig,
            "--repository-root", Repository,
            "--plugin-dll", PluginDll,
            "--plugin-pdb", PluginPdb,
            "--msbuild-version", msbuildVersion,
            "--output", Output
        ]);

        public ProcessResult Verify() => Run(
        [
            "mod-package", "verify",
            "--mod-config", ModConfig,
            "--artifacts", Output
        ]);

        public Dictionary<string, string> AssetHashes() => Directory.EnumerateFiles(Output)
            .ToDictionary(
                path => Path.GetFileName(path)!,
                path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
                StringComparer.Ordinal);

        public void UpdatePlayerChecksum()
        {
            string playerName = Path.GetFileName(PlayerZip);
            string playerHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(PlayerZip))).ToLowerInvariant();
            string[] lines = File.ReadAllLines(Checksums);
            int index = Array.FindIndex(lines, line => line.EndsWith("  " + playerName, StringComparison.Ordinal));
            Assert.True(index >= 0);
            lines[index] = $"{playerHash}  {playerName}";
            File.WriteAllText(Checksums, string.Join('\n', lines) + "\n", new UTF8Encoding(false));
        }

        private static ProcessResult Run(IEnumerable<string> toolArguments)
        {
            ProcessStartInfo startInfo = new("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (string argument in new[] { "run", "--project", ToolProject, "-c", "Release", "--no-build", "--" }.Concat(toolArguments))
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
