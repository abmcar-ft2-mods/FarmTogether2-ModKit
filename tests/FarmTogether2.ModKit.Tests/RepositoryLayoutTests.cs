using Xunit;

namespace FarmTogether2.ModKit.Tests;

public sealed class RepositoryLayoutTests
{
    [Theory]
    [InlineData("LICENSE")]
    [InlineData("README.md")]
    [InlineData("CHANGELOG.md")]
    [InlineData("Directory.Build.props")]
    [InlineData("Directory.Packages.props")]
    [InlineData("NuGet.config")]
    [InlineData("global.json")]
    [InlineData("contracts/game-api.contract.json")]
    [InlineData("contracts/supported-builds.json")]
    public void RequiredRepositoryFileExists(string relativePath)
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        Assert.True(File.Exists(Path.Combine(root, relativePath)), relativePath);
    }
}
