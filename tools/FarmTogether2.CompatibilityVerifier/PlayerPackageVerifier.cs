using System.IO.Compression;
using System.Security.Cryptography;

namespace FarmTogether2.CompatibilityVerifier;

internal static class PlayerPackageVerifier
{
    internal static int Verify(string packagePath, string realPluginPath, string expectedAssemblyName)
    {
        if (!File.Exists(packagePath))
            throw new InvalidDataException("Player package does not exist.");
        if (!File.Exists(realPluginPath))
            throw new InvalidDataException("Real plugin does not exist.");

        try
        {
            using ZipArchive archive = ZipFile.OpenRead(packagePath);
            HashSet<string> names = new(StringComparer.Ordinal);
            List<ZipArchiveEntry> dllEntries = [];
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string normalized = ValidateEntryName(entry.FullName);
                if (!names.Add(normalized))
                    throw new InvalidDataException($"Player package contains duplicate entry '{normalized}'.");
                if (IsUnixSymlink(entry))
                    throw new InvalidDataException($"Player package entry '{normalized}' is a symbolic link.");
                bool isDirectory = normalized.EndsWith("/", StringComparison.Ordinal);
                if (isDirectory)
                {
                    if (entry.Length != 0 || entry.CompressedLength != 0)
                        throw new InvalidDataException($"Player package directory '{normalized}' has file data.");
                    continue;
                }

                string[] segments = normalized.Split('/');
                if (segments.Any(segment => segment.Equals("interop", StringComparison.OrdinalIgnoreCase) ||
                                            segment.Equals("stubs", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidDataException($"Player package leaks an interop or stub path at '{normalized}'.");
                }
                string leaf = segments[^1];
                string withoutExtension = Path.GetFileNameWithoutExtension(leaf);
                if (ApprovedApi.Assemblies.Keys.Contains(withoutExtension, StringComparer.OrdinalIgnoreCase) ||
                    withoutExtension.Equals("FarmTogether2", StringComparison.OrdinalIgnoreCase) ||
                    withoutExtension.Equals("GameAssembly", StringComparison.OrdinalIgnoreCase) ||
                    withoutExtension.Equals("global-metadata", StringComparison.OrdinalIgnoreCase) ||
                    withoutExtension.Contains("Il2CppInterop", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Player package leaks game or stub assembly '{leaf}'.");
                if (leaf.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Player package contains an executable.");
                if (leaf.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Player package contains a symbols file.");
                if (leaf.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    dllEntries.Add(entry);
            }

            if (dllEntries.Count != 1)
                throw new InvalidDataException($"Player package must contain exactly one DLL, found {dllEntries.Count}.");
            ZipArchiveEntry pluginEntry = dllEntries[0];
            string expectedPath = $"BepInEx/plugins/{expectedAssemblyName}/{expectedAssemblyName}.dll";
            if (!string.Equals(pluginEntry.FullName, expectedPath, StringComparison.Ordinal))
                throw new InvalidDataException("Player package DLL has the wrong name or location.");
            string archiveHash;
            using (Stream input = pluginEntry.Open())
                archiveHash = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
            string fileHash = CanonicalMetadata.LowerFileSha256(realPluginPath);
            if (!string.Equals(archiveHash, fileHash, StringComparison.Ordinal))
                throw new InvalidDataException("Player package DLL does not match the verified real plugin.");
            return 1;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("Player package could not be read as a ZIP archive.", exception);
        }
    }

    private static string ValidateEntryName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Contains("\\", StringComparison.Ordinal) || name.Contains(":", StringComparison.Ordinal))
            throw new InvalidDataException("Player package contains an invalid entry name.");
        if (name.StartsWith("/", StringComparison.Ordinal) || name.StartsWith("//", StringComparison.Ordinal))
            throw new InvalidDataException("Player package contains an absolute entry name.");
        string[] segments = name.Split('/');
        int contentSegments = name.EndsWith("/", StringComparison.Ordinal) ? segments.Length - 1 : segments.Length;
        if (contentSegments == 0 || segments.Take(contentSegments).Any(segment => segment.Length == 0 || segment is "." or ".."))
            throw new InvalidDataException("Player package contains a traversal or ambiguous entry name.");
        return name;
    }

    private static bool IsUnixSymlink(ZipArchiveEntry entry)
    {
        int unixMode = (entry.ExternalAttributes >> 16) & 0xffff;
        return (unixMode & 0xf000) == 0xa000;
    }
}
