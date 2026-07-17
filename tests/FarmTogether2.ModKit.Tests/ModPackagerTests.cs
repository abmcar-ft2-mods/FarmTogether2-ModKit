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
    private static readonly string ToolAssembly = Path.Combine(
        Root,
        "tools",
        "FarmTogether2.ModKit.Tool",
        "bin",
        TestBuildConfiguration.Current,
        "net8.0",
        "FarmTogether2.ModKit.Tool.dll");

    [Fact]
    public void WriterProducesByteIdenticalAllowlistedArchives()
    {
        using Fixture first = new("first");
        using Fixture second = new("second", first);
        Dictionary<string, string> firstInputs = first.InputHashes();
        Dictionary<string, string> secondInputs = second.InputHashes();

        first.Write().AssertSuccess();
        Thread.Sleep(TimeSpan.FromSeconds(1));
        second.Write().AssertSuccess();

        string comparison = BuildDeterminismComparison(first, second, firstInputs, secondInputs);
        Assert.True(DictionaryEqual(firstInputs, secondInputs), "Fixture inputs differ before packaging.\n" + comparison);
        Assert.True(DictionaryEqual(first.AssetHashes(), second.AssetHashes()), "Package artifacts differ.\n" + comparison);
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
        assembly.Write(path, new WriterParameters { DeterministicMvid = true, Timestamp = 0 });
    }

    private static bool DictionaryEqual(IReadOnlyDictionary<string, string> first, IReadOnlyDictionary<string, string> second) =>
        first.Count == second.Count && first.All(pair => second.TryGetValue(pair.Key, out string? value) && value == pair.Value);

    private static string BuildDeterminismComparison(
        Fixture first,
        Fixture second,
        IReadOnlyDictionary<string, string> firstInputs,
        IReadOnlyDictionary<string, string> secondInputs)
    {
        StringBuilder report = new();
        AppendHashes(report, "inputs:first", firstInputs);
        AppendHashes(report, "inputs:second", secondInputs);
        Dictionary<string, string> firstFiles = first.InputFiles();
        Dictionary<string, string> secondFiles = second.InputFiles();
        foreach (string name in firstFiles.Keys.Order(StringComparer.Ordinal))
        {
            AppendRawDifference(report, "input:" + name, firstFiles[name], secondFiles[name]);
        }

        Dictionary<string, string> firstAssets = first.AssetPaths();
        Dictionary<string, string> secondAssets = second.AssetPaths();
        AppendHashes(report, "assets:first", first.AssetHashes());
        AppendHashes(report, "assets:second", second.AssetHashes());
        foreach (string name in firstAssets.Keys.Union(secondAssets.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            bool hasFirst = firstAssets.TryGetValue(name, out string? firstPath);
            bool hasSecond = secondAssets.TryGetValue(name, out string? secondPath);
            if (!hasFirst || !hasSecond)
            {
                report.AppendLine($"asset:{name}: missing first={!hasFirst} second={!hasSecond}");
                continue;
            }
            AppendRawDifference(report, "asset:" + name, firstPath!, secondPath!);
            if (Path.GetExtension(name).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                AppendArchive(report, "archive:first:" + name, firstPath!);
                AppendArchive(report, "archive:second:" + name, secondPath!);
            }
        }
        return report.ToString();
    }

    private static void AppendHashes(StringBuilder report, string label, IReadOnlyDictionary<string, string> hashes)
    {
        foreach ((string name, string hash) in hashes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            report.AppendLine($"{label}:{name}: sha256={hash}");
    }

    private static void AppendRawDifference(StringBuilder report, string label, string firstPath, string secondPath)
    {
        byte[] first = File.ReadAllBytes(firstPath);
        byte[] second = File.ReadAllBytes(secondPath);
        int limit = Math.Min(first.Length, second.Length);
        int offset = 0;
        while (offset < limit && first[offset] == second[offset])
            offset++;
        string difference = offset < limit
            ? $"offset={offset} first=0x{first[offset]:x2} second=0x{second[offset]:x2}"
            : offset == first.Length && offset == second.Length
                ? "identical"
                : $"offset={offset} first=<eof:{first.Length}> second=<eof:{second.Length}>";
        report.AppendLine($"{label}: firstLength={first.Length} secondLength={second.Length} firstDifference={difference}");
    }

    private static void AppendArchive(StringBuilder report, string label, string path)
    {
        using ZipArchive archive = ZipFile.OpenRead(path);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            using Stream payload = entry.Open();
            string hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
            report.AppendLine(
                $"{label}:{entry.FullName}: sha256={hash} crc32={entry.Crc32:x8} length={entry.Length} compressedLength={entry.CompressedLength} " +
                $"lastWriteTime={entry.LastWriteTime:O} externalAttributes={entry.ExternalAttributes}");
        }
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(string? label = null, Fixture? sourceInputs = null)
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), $"modkit-mod-package-{label}-{Guid.NewGuid():N}");
            Repository = Path.Combine(DirectoryPath, "repository");
            Output = Path.Combine(DirectoryPath, "output");
            string projectDirectory = Path.Combine(Repository, "src", AssemblyName);
            PluginDll = Path.Combine(projectDirectory, AssemblyName + ".dll");
            PluginPdb = Path.Combine(projectDirectory, AssemblyName + ".pdb");
            Readme = Path.Combine(Repository, "packaging", "README_安装说明.txt");
            License = Path.Combine(Repository, "LICENSE");
            Directory.CreateDirectory(projectDirectory);
            Directory.CreateDirectory(Path.Combine(Repository, "packaging"));
            Directory.CreateDirectory(Output);
            if (sourceInputs is null)
            {
                WriteAssembly(PluginDll, AssemblyName, Version);
                File.WriteAllBytes(PluginPdb, [1, 2, 3, 4]);
                File.WriteAllText(Readme, "Install\n", new UTF8Encoding(false));
                File.WriteAllText(License, "MIT\n", new UTF8Encoding(false));
            }
            else
            {
                File.Copy(sourceInputs.PluginDll, PluginDll);
                File.Copy(sourceInputs.PluginPdb, PluginPdb);
                File.Copy(sourceInputs.Readme, Readme);
                File.Copy(sourceInputs.License, License);
            }
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
        public string Readme { get; }
        public string License { get; }
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

        public Dictionary<string, string> InputHashes() => InputFiles().ToDictionary(
            pair => pair.Key,
            pair => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pair.Value))).ToLowerInvariant(),
            StringComparer.Ordinal);

        public Dictionary<string, string> InputFiles() => new(StringComparer.Ordinal)
        {
            ["pluginDll"] = PluginDll,
            ["pluginPdb"] = PluginPdb,
            ["readme"] = Readme,
            ["license"] = License,
            ["modConfig"] = ModConfig
        };

        public Dictionary<string, string> AssetPaths() => Directory.EnumerateFiles(Output)
            .ToDictionary(path => Path.GetFileName(path)!, StringComparer.Ordinal);

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
            foreach (string argument in new[] { ToolAssembly }.Concat(toolArguments))
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
