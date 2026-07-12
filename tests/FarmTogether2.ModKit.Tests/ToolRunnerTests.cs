using System.Diagnostics;
using System.Text;
using Xunit;

namespace FarmTogether2.ModKit.Tests;

public sealed class ToolRunnerTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));

    [Fact]
    public void RunnerBuildsOutsideToolingTreeAndCleansItsOwnedAttempt()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"modkit-tool-runner-{Guid.NewGuid():N}");
        string checkout = Path.Combine(directory, "tooling");
        string toolSource = Path.Combine(Root, "tools", "FarmTogether2.ModKit.Tool");
        string toolDestination = Path.Combine(checkout, "tools", "FarmTogether2.ModKit.Tool");
        string scripts = Path.Combine(checkout, "scripts");
        string temporary = Path.Combine(directory, "temporary");
        Directory.CreateDirectory(toolDestination);
        Directory.CreateDirectory(scripts);
        Directory.CreateDirectory(temporary);
        try
        {
            foreach (string source in Directory.EnumerateFiles(toolSource))
                File.Copy(source, Path.Combine(toolDestination, Path.GetFileName(source)));
            foreach (string name in new[] { "Directory.Build.props", "Directory.Packages.props", "NuGet.config", "global.json" })
                File.Copy(Path.Combine(Root, name), Path.Combine(checkout, name));
            string runner = Path.Combine(scripts, "Invoke-ModKitTool.ps1");
            File.Copy(Path.Combine(Root, "scripts", "Invoke-ModKitTool.ps1"), runner);
            string lockFile = Path.Combine(directory, "modkit.lock.json");
            File.WriteAllText(lockFile, """
                {
                  "schemaVersion": 1,
                  "repository": "abmcar/FarmTogether2-ModKit",
                  "workflowCommit": "0123456789abcdef0123456789abcdef01234567",
                  "packageId": "FarmTogether2.GameApi.Ref",
                  "packageVersion": "1.0.0",
                  "releaseTag": "v1.0.0",
                  "assetName": "FarmTogether2.GameApi.Ref.1.0.0.nupkg",
                  "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                }
                """.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n", new UTF8Encoding(false));

            ProcessStartInfo startInfo = new("pwsh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-File", runner, "lock", "verify", "--file", lockFile })
                startInfo.ArgumentList.Add(argument);
            startInfo.Environment["PATH"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet") + Path.PathSeparator +
                (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
            startInfo.Environment["DOTNET_ROOT"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet");
            startInfo.Environment["TMPDIR"] = temporary;
            startInfo.Environment["TMP"] = temporary;
            startInfo.Environment["TEMP"] = temporary;
            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start pwsh.");
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.True(process.ExitCode == 0, $"stdout:\n{stdout}\nstderr:\n{stderr}");
            Assert.False(Directory.Exists(Path.Combine(toolDestination, "bin")));
            Assert.False(Directory.Exists(Path.Combine(toolDestination, "obj")));
            Assert.DoesNotContain(Directory.EnumerateDirectories(temporary),
                path => Path.GetFileName(path).StartsWith("farmtogether2-modkit-tool-", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
