using Xunit;

namespace FarmTogether2.ModKit.Tests;

public sealed class TestFileSystemTests
{
    [Fact]
    public void DeleteDirectoryTreeDoesNotFollowDirectorySymbolicLinks()
    {
        AssertDoesNotFollowDirectoryLink(
            (linkPath, targetPath) => Directory.CreateSymbolicLink(linkPath, targetPath));
    }

    [WindowsFact]
    public void DeleteDirectoryTreeDoesNotFollowDirectoryJunctions()
    {
        AssertDoesNotFollowDirectoryLink(TestJunction.Create);
    }

    private static void AssertDoesNotFollowDirectoryLink(Action<string, string> createLink)
    {
        string testRoot = Path.Combine(Path.GetTempPath(), $"modkit-delete-tree-{Guid.NewGuid():N}");
        string tree = Path.Combine(testRoot, "tree");
        string outside = Path.Combine(testRoot, "outside");
        string link = Path.Combine(tree, "link");
        string sentinel = Path.Combine(outside, "sentinel.txt");
        Directory.CreateDirectory(tree);
        Directory.CreateDirectory(outside);
        File.WriteAllText(sentinel, "keep");

        try
        {
            createLink(link, outside);

            TestFileSystem.DeleteDirectoryTree(tree);

            Assert.False(Directory.Exists(tree));
            Assert.Equal("keep", File.ReadAllText(sentinel));
        }
        finally
        {
            if (Directory.Exists(link))
                Directory.Delete(link);
            if (Directory.Exists(tree))
                Directory.Delete(tree, recursive: true);
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }
}
