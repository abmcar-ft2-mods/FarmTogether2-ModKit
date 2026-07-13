using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace FarmTogether2.ModKit.Tool;

internal static class ModPackager
{
    private static readonly DateTimeOffset FixedTimestamp = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static void Write(
        string modConfigPath,
        string repositoryRoot,
        string pluginDll,
        string pluginPdb,
        string msbuildVersion,
        string outputDirectory)
    {
        string root = Path.GetFullPath(repositoryRoot);
        SafePath.AssertDirectory(root, "Repository root");
        ModConfiguration config = StrictModJson.Read(modConfigPath);
        string expectedConfig = Path.Combine(root, "mod.json");
        if (!Path.GetFullPath(modConfigPath).Equals(expectedConfig, PathComparison))
            throw new InvalidDataException("Mod configuration must be the repository-root mod.json.");
        if (msbuildVersion != config.Version)
            throw new InvalidDataException("MSBuild Version does not match mod.json version.");

        string dll = AssertRepositoryInput(root, pluginDll, config.AssemblyName + ".dll", "Plugin DLL");
        string pdb = AssertRepositoryInput(root, pluginPdb, config.AssemblyName + ".pdb", "Plugin PDB");
        AssertAssemblyIdentity(dll, config.AssemblyName);
        string readme = StrictModJson.ResolveRepositoryPath(root, config.InstallReadme, "installReadme");
        SafePath.AssertRegularFile(readme, "Install README");
        string license = Path.Combine(root, "LICENSE");
        SafePath.AssertRegularFile(license, "License");

        string output = Path.GetFullPath(outputDirectory);
        SafePath.AssertDirectory(output, "Package output directory");
        if (Directory.EnumerateFileSystemEntries(output).Any())
            throw new InvalidDataException("Package output directory must be empty.");

        string playerName = PlayerAssetName(config);
        string symbolsName = SymbolsAssetName(config);
        string player = Path.Combine(output, playerName);
        string symbols = Path.Combine(output, symbolsName);
        string checksums = Path.Combine(output, "SHA256SUMS.txt");
        try
        {
            WriteArchive(
                player,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [$"BepInEx/plugins/{config.AssemblyName}/{config.AssemblyName}.dll"] = dll,
                    ["LICENSE"] = license,
                    [Path.GetFileName(readme)] = readme
                });
            WriteArchive(
                symbols,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [$"{config.AssemblyName}.pdb"] = pdb
                });
            string checksumText = string.Join(
                "\n",
                new[] { player, symbols }
                    .OrderBy(Path.GetFileName, StringComparer.Ordinal)
                    .Select(path => $"{Sha256(path)}  {Path.GetFileName(path)}")) + "\n";
            File.WriteAllText(checksums, checksumText, new UTF8Encoding(false));
            Verify(modConfigPath, output);
        }
        catch
        {
            foreach (string path in new[] { checksums, symbols, player })
            {
                if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                    File.Delete(path);
            }
            throw;
        }
    }

    public static void Verify(string modConfigPath, string artifactsDirectory)
    {
        ModConfiguration config = StrictModJson.Read(modConfigPath);
        string artifacts = Path.GetFullPath(artifactsDirectory);
        SafePath.AssertDirectoryTreeHasNoReparsePoint(artifacts, "Package artifacts");
        string playerName = PlayerAssetName(config);
        string symbolsName = SymbolsAssetName(config);
        string[] expectedFiles = [playerName, symbolsName, "SHA256SUMS.txt"];
        string[] actualFiles = Directory.EnumerateFileSystemEntries(artifacts)
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray()!;
        if (!actualFiles.SequenceEqual(expectedFiles.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidDataException("Package artifacts must contain exactly the two versioned ZIPs and SHA256SUMS.txt.");
        foreach (string name in expectedFiles)
            SafePath.AssertRegularFile(Path.Combine(artifacts, name), $"Package artifact {name}");

        VerifyArchive(
            Path.Combine(artifacts, playerName),
            [
                $"BepInEx/plugins/{config.AssemblyName}/{config.AssemblyName}.dll",
                "LICENSE",
                Path.GetFileName(config.InstallReadme)
            ]);
        VerifyArchive(
            Path.Combine(artifacts, symbolsName),
            [$"{config.AssemblyName}.pdb"]);
        VerifyChecksums(
            Path.Combine(artifacts, "SHA256SUMS.txt"),
            new[] { playerName, symbolsName },
            artifacts);
    }

    public static string PlayerAssetName(ModConfiguration config) => $"{config.AssemblyName}-v{config.Version}.zip";
    public static string SymbolsAssetName(ModConfiguration config) => $"{config.AssemblyName}-v{config.Version}-symbols.zip";

    internal static void VerifyChecksums(string checksumPath, IReadOnlyCollection<string> expectedNames, string directory)
    {
        SafePath.AssertRegularFile(checksumPath, "SHA256SUMS.txt");
        string text = File.ReadAllText(checksumPath);
        if (text.Contains('\r') || !text.EndsWith('\n') || text.Length == 0)
            throw new InvalidDataException("Checksum file must use canonical LF-terminated lines.");
        string[] lines = text[..^1].Split('\n');
        if (lines.Length != expectedNames.Count)
            throw new InvalidDataException("Checksum file does not contain exactly one line per release asset.");
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (string line in lines)
        {
            if (line.Length < 67 || line[64..66] != "  ")
                throw new InvalidDataException("Checksum file contains a noncanonical line.");
            string hash = line[..64];
            string name = line[66..];
            if (!IsLowerHex(hash) || Path.GetFileName(name) != name || name.Contains(':') || !values.TryAdd(name, hash))
                throw new InvalidDataException("Checksum file contains an unsafe, duplicate, or noncanonical entry.");
        }
        string[] expected = expectedNames.Order(StringComparer.Ordinal).ToArray();
        if (!values.Keys.Order(StringComparer.Ordinal).SequenceEqual(expected, StringComparer.Ordinal))
            throw new InvalidDataException("Checksum file names differ from the closed release asset set.");
        foreach (string name in expected)
        {
            string path = Path.Combine(directory, name);
            SafePath.AssertRegularFile(path, $"Checksummed asset {name}");
            if (Sha256(path) != values[name])
                throw new InvalidDataException($"SHA-256 mismatch for release asset {name}.");
        }
    }

    internal static string Sha256(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void WriteArchive(string path, IReadOnlyDictionary<string, string> entries)
    {
        string temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach ((string name, string source) in entries.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                    entry.LastWriteTime = FixedTimestamp;
                    entry.ExternalAttributes = 0;
                    using Stream input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using Stream output = entry.Open();
                    input.CopyTo(output);
                }
            }
            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static void VerifyArchive(string path, IReadOnlyCollection<string> expectedEntries)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using ZipArchive archive = new(stream, ZipArchiveMode.Read, leaveOpen: false);
        string[] actual = archive.Entries.Select(entry => entry.FullName).ToArray();
        string[] expected = expectedEntries.Order(StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal) || actual.Distinct(StringComparer.Ordinal).Count() != actual.Length)
            throw new InvalidDataException("Archive does not match its closed allowlisted layout.");
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (entry.FullName.Contains('\\') || entry.FullName.Contains(':') || entry.FullName.StartsWith('/') ||
                entry.FullName.Split('/').Any(segment => segment.Length == 0 || segment is "." or "..") ||
                entry.Name.Length == 0 || entry.LastWriteTime.DateTime != FixedTimestamp.DateTime || entry.ExternalAttributes != 0)
                throw new InvalidDataException("Archive contains an unsafe or noncanonical entry.");

            (uint crc32, long length) payload;
            try
            {
                using Stream payloadStream = entry.Open();
                payload = ReadPayload(payloadStream);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException)
            {
                throw new InvalidDataException($"Archive entry payload decompression failed: {entry.FullName}", exception);
            }
            if (payload.length != entry.Length || payload.crc32 != entry.Crc32)
                throw new InvalidDataException($"Archive entry payload length or CRC-32 mismatch: {entry.FullName}");
        }
    }

    private static (uint crc32, long length) ReadPayload(Stream stream)
    {
        uint crc = uint.MaxValue;
        long length = 0;
        Span<byte> buffer = stackalloc byte[8192];
        while (true)
        {
            int read = stream.Read(buffer);
            if (read == 0)
                return (~crc, length);
            length = checked(length + read);
            foreach (byte value in buffer[..read])
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++)
                    crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xedb88320u;
            }
        }
    }

    private static string AssertRepositoryInput(string root, string path, string expectedName, string label)
    {
        string fullPath = Path.GetFullPath(path);
        string relative = Path.GetRelativePath(root, fullPath);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidDataException($"{label} must remain inside the repository.");
        if (Path.GetFileName(fullPath) != expectedName)
            throw new InvalidDataException($"{label} name does not match mod.json assemblyName.");
        SafePath.AssertRegularFile(fullPath, label);
        return fullPath;
    }

    private static void AssertAssemblyIdentity(string path, string expectedName)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using PEReader reader = new(stream, PEStreamOptions.LeaveOpen);
        if (!reader.HasMetadata)
            throw new InvalidDataException("Plugin DLL is not a managed assembly.");
        MetadataReader metadata = reader.GetMetadataReader();
        if (!metadata.IsAssembly)
            throw new InvalidDataException("Plugin DLL does not contain an assembly definition.");
        AssemblyDefinition definition = metadata.GetAssemblyDefinition();
        string name = metadata.GetString(definition.Name);
        if (name != expectedName)
            throw new InvalidDataException("Plugin assembly identity does not match mod.json assemblyName.");
    }

    private static bool IsLowerHex(string value) => value.Length == 64 && value.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
