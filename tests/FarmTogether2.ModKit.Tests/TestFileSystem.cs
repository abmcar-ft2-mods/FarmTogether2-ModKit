namespace FarmTogether2.ModKit.Tests;

internal static class TestFileSystem
{
    public static void DeleteDirectoryTree(string path)
    {
        if (!Directory.Exists(path))
            return;

        var pending = new Stack<(string Path, FileAttributes Attributes)>();
        var directories = new Stack<string>();
        pending.Push((path, File.GetAttributes(path)));
        while (pending.TryPop(out (string Path, FileAttributes Attributes) current))
        {
            FileAttributes attributes = current.Attributes;
            bool isDirectory = (attributes & FileAttributes.Directory) != 0;
            bool isReparsePoint = (attributes & FileAttributes.ReparsePoint) != 0;

            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                attributes &= ~FileAttributes.ReadOnly;
                File.SetAttributes(current.Path, attributes);
            }

            if (isReparsePoint)
            {
                if (isDirectory)
                    Directory.Delete(current.Path);
                else
                    File.Delete(current.Path);
                continue;
            }

            if (!isDirectory)
            {
                File.Delete(current.Path);
                continue;
            }

            directories.Push(current.Path);
            foreach (string entry in Directory.EnumerateFileSystemEntries(current.Path))
                pending.Push((entry, File.GetAttributes(entry)));
        }

        while (directories.TryPop(out string? directory))
            Directory.Delete(directory);
    }
}
