namespace FarmTogether2.ModKit.Tests;

internal static class TestBuildConfiguration
{
    public static string Current { get; } = GetCurrent();

    private static string GetCurrent()
    {
        string outputDirectory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        string? configuration = Directory.GetParent(outputDirectory)?.Name;
        return configuration is "Debug" or "Release"
            ? configuration
            : throw new InvalidOperationException(
                $"Could not determine the test build configuration from {AppContext.BaseDirectory}.");
    }
}
