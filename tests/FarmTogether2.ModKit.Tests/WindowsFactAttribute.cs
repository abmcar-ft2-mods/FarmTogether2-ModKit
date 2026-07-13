using Xunit;

namespace FarmTogether2.ModKit.Tests;

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows junction coverage runs only on Windows.";
    }
}
