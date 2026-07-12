namespace FarmTogether2.ModKit.Tool;

internal static class LockFileResolver
{
    public static ModKitLock Read(string path) => StrictLockJson.Read(Path.GetFullPath(path));

    public static void Write(string path, ModKitLock value)
    {
        string output = Path.GetFullPath(path);
        string? parent = Path.GetDirectoryName(output);
        if (string.IsNullOrEmpty(parent))
            throw new DirectoryNotFoundException($"Lock output parent does not exist: {parent}");
        SafePath.AssertDirectory(parent, "Lock output parent");
        if (Directory.Exists(output))
            throw new InvalidDataException($"Lock output must not be a directory: {output}");
        if (File.Exists(output))
            SafePath.AssertRegularFile(output, "Existing lock output");
        byte[] bytes = StrictLockJson.Serialize(value);
        string temporary = Path.Combine(parent, $".{Path.GetFileName(output)}.{Guid.NewGuid():N}.preparing");
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            _ = StrictLockJson.Read(temporary);
            SafePath.AssertDirectory(parent, "Lock output parent");
            if (File.Exists(output))
                SafePath.AssertRegularFile(output, "Existing lock output");
            File.Move(temporary, output, overwrite: true);
            ModKitLock written = StrictLockJson.Read(output);
            if (written != value)
                throw new IOException("Lock output differs after atomic promotion.");
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
