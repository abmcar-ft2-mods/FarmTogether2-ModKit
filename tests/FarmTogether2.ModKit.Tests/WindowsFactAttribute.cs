using System.Runtime.CompilerServices;
using Xunit;

namespace FarmTogether2.ModKit.Tests;

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows junction coverage runs only on Windows.";
    }
}
