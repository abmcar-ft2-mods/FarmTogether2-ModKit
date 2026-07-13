namespace FarmTogether2.ModKit.Tests;

internal static class TestFileSystem
{
    public static void DeleteDirectoryTree(string path)
    {
        if (!Directory.Exists(path))
            return;

        ClearReadOnlyAttributes(path);
        Directory.Delete(path, recursive: true);
    }

    private static void ClearReadOnlyAttributes(string path)
    {
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.TryPop(out string? current))
        {
            FileAttributes attributes = File.GetAttributes(current);
            bool isDirectory = (attributes & FileAttributes.Directory) != 0;
            bool isReparsePoint = (attributes & FileAttributes.ReparsePoint) != 0;

            if (isDirectory && !isReparsePoint)
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(current))
                    pending.Push(entry);
            }

            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(current, attributes & ~FileAttributes.ReadOnly);
        }
    }
}
