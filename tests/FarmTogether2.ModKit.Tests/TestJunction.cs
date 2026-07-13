using System.Diagnostics;

namespace FarmTogether2.ModKit.Tests;

internal static class TestJunction
{
    public static void Create(string linkPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Directory junctions are available only on Windows.");

        var startInfo = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(
            "$ErrorActionPreference = 'Stop'; " +
            "$null = New-Item -ItemType Junction -Path $env:MODKIT_JUNCTION_LINK -Target $env:MODKIT_JUNCTION_TARGET");
        startInfo.Environment["MODKIT_JUNCTION_LINK"] = linkPath;
        startInfo.Environment["MODKIT_JUNCTION_TARGET"] = targetPath;
        using Process process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Unable to start PowerShell for junction capability probe.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || !Directory.Exists(linkPath))
        {
            throw new InvalidOperationException(
                $"Directory junctions are unavailable: exit={process.ExitCode} stdout={stdout} stderr={stderr}");
        }
    }
}
