using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace FarmTogether2.ModKit.Tests;

public sealed class GameSwitchTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private static readonly string SwitchScript = Path.Combine(Root, "scripts", "Switch-GameBuild.ps1");

    public static TheoryData<string> ActivateOldCrashPoints => new()
    {
        "before-journal-prepared",
        "before-journal-original-game-move-pending",
        "after-rename-original-game-to-backup",
        "before-journal-original-game-preserved",
        "before-journal-original-save-move-pending",
        "after-rename-original-save-to-backup",
        "before-journal-originals-preserved",
        "before-journal-old-copy-preparing",
        "after-copy-old-base",
        "after-copy-old-loader-doorstop_config.ini",
        "after-copy-old-loader-.doorstop_version",
        "after-copy-old-loader-winhttp.dll",
        "after-copy-old-loader-BepInEx-core",
        "after-copy-old-loader-BepInEx-config-BepInEx.cfg",
        "before-journal-old-copy-prepared",
        "before-journal-old-activate-pending",
        "after-rename-old-smoke-to-canonical",
        "before-journal-old-active",
    };

    public static TheoryData<string> ActivateCurrentCrashPoints => new()
    {
        "before-journal-old-deactivate-pending",
        "after-rename-canonical-old-to-old-smoke",
        "before-journal-old-game-preserved",
        "before-journal-old-save-move-pending",
        "after-rename-old-save-to-old-smoke-save",
        "before-journal-old-save-preserved",
        "before-journal-current-copy-preparing",
        "after-copy-current-base",
        "after-copy-current-loader-doorstop_config.ini",
        "after-copy-current-loader-.doorstop_version",
        "after-copy-current-loader-winhttp.dll",
        "after-copy-current-loader-BepInEx-core",
        "after-copy-current-loader-BepInEx-config-BepInEx.cfg",
        "before-journal-current-copy-prepared",
        "before-journal-current-activate-pending",
        "after-rename-current-smoke-to-canonical",
        "before-journal-current-smoke-active",
    };

    public static TheoryData<string> RestoreCrashPoints => new()
    {
        "before-journal-restore-current-game-pending",
        "after-rename-canonical-current-to-current-smoke",
        "before-journal-restore-current-game-preserved",
        "before-journal-restore-current-save-pending",
        "after-rename-current-save-to-current-smoke-save",
        "before-journal-restore-save-preserved",
        "before-journal-restore-original-game-pending",
        "after-rename-original-game-to-canonical",
        "before-journal-restore-original-game-restored",
        "before-journal-restore-original-save-pending",
        "after-rename-original-save-to-canonical",
        "before-journal-restored",
    };

    [Fact]
    public void ActivateSwitchAndRestorePreserveExactTreesAndFingerprintVector()
    {
        using Fixture fixture = new();

        fixture.RunSwitch("ActivateOld").AssertSuccess();
        JsonElement oldState = fixture.ReadState();
        Assert.Equal("old-active", oldState.GetProperty("phase").GetString());
        Assert.Equal(fixture.Canonical, oldState.GetProperty("canonical").GetString());
        Assert.Equal(Fingerprint(fixture.OldHashes), oldState.GetProperty("oldFingerprint").GetString());
        Assert.Equal(Fingerprint(fixture.CurrentHashes), oldState.GetProperty("currentFingerprint").GetString());
        AssertJsonSchema(oldState);
        fixture.AssertActiveBuild("old");
        fixture.AssertCleanLoaderOnly();

        Directory.CreateDirectory(fixture.Save);
        File.WriteAllText(Path.Combine(fixture.Save, "old-smoke.sav"), "old save");
        fixture.RunSwitch("ActivateCurrent").AssertSuccess();
        fixture.AssertActiveBuild("current");
        fixture.AssertCleanLoaderOnly();

        Directory.CreateDirectory(fixture.Save);
        File.WriteAllText(Path.Combine(fixture.Save, "current-smoke.sav"), "current save");
        fixture.RunSwitch("Restore").AssertSuccess();
        fixture.AssertRestored();

        JsonElement restored = fixture.ReadState();
        Assert.Equal("restored", restored.GetProperty("phase").GetString());
        Assert.True(Directory.Exists(restored.GetProperty("oldSmokeGameDirectory").GetString()!));
        Assert.True(Directory.Exists(restored.GetProperty("currentSmokeGameDirectory").GetString()!));
        Assert.True(Directory.Exists(restored.GetProperty("oldSmokeSaveDirectory").GetString()!));
        Assert.True(Directory.Exists(restored.GetProperty("currentSmokeSaveDirectory").GetString()!));
    }

    [Theory]
    [MemberData(nameof(ActivateOldCrashPoints))]
    public void ActivateOldResumesEveryPhysicalAndJournalCrash(string crashPoint)
    {
        using Fixture fixture = new();
        fixture.RunSwitch("ActivateOld", crashPoint).AssertFailure(crashPoint);
        fixture.RunSwitch("ActivateOld").AssertSuccess();
        fixture.AssertActiveBuild("old");
        fixture.RunSwitch("Restore").AssertSuccess();
        fixture.AssertRestored();
    }

    [Theory]
    [MemberData(nameof(ActivateCurrentCrashPoints))]
    public void ActivateCurrentResumesEveryPhysicalAndJournalCrash(string crashPoint)
    {
        using Fixture fixture = new();
        fixture.RunSwitch("ActivateOld").AssertSuccess();
        Directory.CreateDirectory(fixture.Save);
        File.WriteAllText(Path.Combine(fixture.Save, "old-smoke.sav"), "old save");

        fixture.RunSwitch("ActivateCurrent", crashPoint).AssertFailure(crashPoint);
        fixture.RunSwitch("ActivateCurrent").AssertSuccess();
        fixture.AssertActiveBuild("current");
        fixture.RunSwitch("Restore").AssertSuccess();
        fixture.AssertRestored();
    }

    [Theory]
    [MemberData(nameof(RestoreCrashPoints))]
    public void RestoreResumesEveryPhysicalAndJournalCrash(string crashPoint)
    {
        using Fixture fixture = new();
        fixture.PrepareCurrentActive();
        Directory.CreateDirectory(fixture.Save);
        File.WriteAllText(Path.Combine(fixture.Save, "current-smoke.sav"), "current save");

        fixture.RunSwitch("Restore", crashPoint).AssertFailure(crashPoint);
        fixture.RunSwitch("Restore").AssertSuccess();
        fixture.AssertRestored();
    }

    [Fact]
    public void RestoreRejectsThirdHashWithoutMovingAnyOriginalTree()
    {
        using Fixture fixture = new();
        fixture.RunSwitch("ActivateOld").AssertSuccess();
        JsonElement state = fixture.ReadState();
        string original = state.GetProperty("originalGameDirectory").GetString()!;
        File.WriteAllText(Path.Combine(fixture.Canonical, "FarmTogether2.exe"), "third hash");

        fixture.RunSwitch("Restore").AssertFailure("hash mismatch");

        Assert.True(Directory.Exists(fixture.Canonical));
        Assert.Equal("third hash", File.ReadAllText(Path.Combine(fixture.Canonical, "FarmTogether2.exe")));
        Assert.True(Directory.Exists(original));
        Assert.Equal("current-exe", File.ReadAllText(Path.Combine(original, "FarmTogether2.exe")));
    }

    [Fact]
    public void RestoreRejectsAmbiguousTopologyWithoutOverwriteOrMove()
    {
        using Fixture fixture = new();
        fixture.RunSwitch("ActivateOld").AssertSuccess();
        JsonElement state = fixture.ReadState();
        string oldSmoke = state.GetProperty("oldSmokeGameDirectory").GetString()!;
        Directory.CreateDirectory(oldSmoke);
        File.WriteAllText(Path.Combine(oldSmoke, "sentinel.txt"), "do not touch");

        fixture.RunSwitch("Restore").AssertFailure("ambiguous");

        fixture.AssertActiveBuild("old");
        Assert.Equal("do not touch", File.ReadAllText(Path.Combine(oldSmoke, "sentinel.txt")));
    }

    [Fact]
    public void ClosedJournalRejectsExtraFieldBeforeAnyMutation()
    {
        using Fixture fixture = new();
        fixture.RunSwitch("ActivateOld").AssertSuccess();
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(fixture.State));
        Dictionary<string, object?> changed = JsonSerializer.Deserialize<Dictionary<string, object?>>(document.RootElement.GetRawText())!;
        changed.Add("unexpected", true);
        File.WriteAllText(fixture.State, JsonSerializer.Serialize(changed));

        fixture.RunSwitch("Restore").AssertFailure("schema");
        fixture.AssertActiveBuild("old");
    }

    [Theory]
    [InlineData("FarmTogether2.exe")]
    [InlineData("../FarmTogether2.exe=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("FarmTogether2.exe=not-a-hash")]
    public void ActivateOldRejectsMalformedOrNonCanonicalHashEntries(string replacement)
    {
        using Fixture fixture = new();
        string[] entries = fixture.CurrentHashArguments.ToArray();
        entries[0] = replacement;
        fixture.RunSwitch("ActivateOld", expectedHashOverride: entries).AssertFailure("ExpectedCurrentNativeHash");
        Assert.False(File.Exists(fixture.State));
        Assert.True(Directory.Exists(fixture.Canonical));
    }

    [Fact]
    public void ActivateOldRejectsDuplicateNormalizedHashPaths()
    {
        using Fixture fixture = new();
        string[] entries = fixture.CurrentHashArguments.Append(fixture.CurrentHashArguments[0].Replace('/', '\\')).ToArray();
        fixture.RunSwitch("ActivateOld", expectedHashOverride: entries).AssertFailure("duplicate");
        Assert.True(Directory.Exists(fixture.Canonical));
    }

    [Fact]
    public void RestorePreservesAnOriginallyAbsentSaveDirectory()
    {
        using Fixture fixture = new(saveExists: false);
        fixture.RunSwitch("ActivateOld").AssertSuccess();
        Directory.CreateDirectory(fixture.Save);
        File.WriteAllText(Path.Combine(fixture.Save, "smoke.sav"), "temporary");
        fixture.RunSwitch("Restore").AssertSuccess();

        Assert.False(Directory.Exists(fixture.Save));
        Assert.Equal("restored", fixture.ReadState().GetProperty("phase").GetString());
    }

    private static void AssertJsonSchema(JsonElement state)
    {
        string[] expected =
        {
            "schemaVersion", "attemptId", "phase", "canonical", "saveDirectory", "appManifestPath",
            "downloadedOldDepot", "originalGameDirectory", "oldSmokeGameDirectory", "currentSmokeGameDirectory",
            "originalSaveDirectory", "oldSmokeSaveDirectory", "currentSmokeSaveDirectory", "saveWasPresent",
            "originalSaveFingerprint", "appManifestSha256", "oldBuildId", "currentBuildId", "oldManifestId",
            "currentManifestId", "oldHashes", "currentHashes", "oldFingerprint", "currentFingerprint",
        };
        Assert.Equal(expected.Order(StringComparer.Ordinal), state.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    private static string Fingerprint(IReadOnlyDictionary<string, string> hashes)
    {
        string vector = string.Join('\n', hashes.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}\t{pair.Value}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(vector))).ToLowerInvariant();
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly string[] NativePaths =
        {
            "FarmTogether2.exe",
            "FarmTogether2_Data/il2cpp_data/Metadata/global-metadata.dat",
            "GameAssembly.dll",
        };

        public Fixture(bool saveExists = true)
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), $"farmtogether2-modkit-{Guid.NewGuid():N}");
            Canonical = Path.Combine(DirectoryPath, "Farm Together 2");
            Save = Path.Combine(DirectoryPath, "FarmTogether2-save");
            OldDepot = Path.Combine(DirectoryPath, "old-depot");
            AppManifest = Path.Combine(DirectoryPath, "appmanifest_2418520.acf");
            State = Path.Combine(DirectoryPath, "game-switch.json");

            CreateBuild(Canonical, "current");
            CreateBuild(OldDepot, "old");
            File.WriteAllText(Path.Combine(Canonical, "user-install.txt"), "original user tree");
            CreateLoader(Canonical);
            Directory.CreateDirectory(Path.Combine(Canonical, "BepInEx", "plugins"));
            File.WriteAllText(Path.Combine(Canonical, "BepInEx", "plugins", "private.dll"), "must not copy");
            File.WriteAllText(Path.Combine(Canonical, "BepInEx", "config", "private.cfg"), "must not copy");
            if (saveExists)
            {
                Directory.CreateDirectory(Save);
                File.WriteAllText(Path.Combine(Save, "original.sav"), "original save");
            }

            File.WriteAllText(AppManifest, "manifest must stay unchanged");
            CurrentHashes = Hashes(Canonical);
            OldHashes = Hashes(OldDepot);
            CurrentHashArguments = CurrentHashes.Select(pair => $"{pair.Key}={pair.Value}").ToArray();
        }

        public string DirectoryPath { get; }
        public string Canonical { get; }
        public string Save { get; }
        public string OldDepot { get; }
        public string AppManifest { get; }
        public string State { get; }
        public IReadOnlyDictionary<string, string> CurrentHashes { get; }
        public IReadOnlyDictionary<string, string> OldHashes { get; }
        public IReadOnlyList<string> CurrentHashArguments { get; }

        public ProcessResult RunSwitch(string action, string? crashPoint = null, IReadOnlyList<string>? expectedHashOverride = null)
        {
            static string Quote(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
            StringBuilder invocation = new($"& {Quote(SwitchScript)} -Action {Quote(action)} -StatePath {Quote(State)}");
            if (action == "ActivateOld")
            {
                invocation.Append($" -CanonicalGameDirectory {Quote(Canonical)} -SaveDirectory {Quote(Save)} -AppManifestPath {Quote(AppManifest)} -DownloadedOldDepot {Quote(OldDepot)}");
                invocation.Append(" -OldBuildId 23821227 -OldManifestId 7567480468616200523 -CurrentBuildId 24069957 -CurrentManifestId 2755117260250472086");
                invocation.Append($" -ExpectedCurrentNativeHash @({string.Join(',', (expectedHashOverride ?? CurrentHashArguments).Select(Quote))})");
            }

            string driver = Path.Combine(DirectoryPath, $"switch-driver-{Guid.NewGuid():N}.ps1");
            File.WriteAllText(driver, invocation.ToString());
            return RunPowerShell(new[] { "-File", driver }, "FARMT2_SWITCH_FAIL_AT", crashPoint);
        }

        public void PrepareCurrentActive()
        {
            RunSwitch("ActivateOld").AssertSuccess();
            Directory.CreateDirectory(Save);
            File.WriteAllText(Path.Combine(Save, "old-smoke.sav"), "old save");
            RunSwitch("ActivateCurrent").AssertSuccess();
        }

        public JsonElement ReadState()
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(State));
            return document.RootElement.Clone();
        }

        public void AssertActiveBuild(string build)
        {
            Assert.True(Directory.Exists(Canonical));
            Assert.Equal($"{build}-exe", File.ReadAllText(Path.Combine(Canonical, "FarmTogether2.exe")));
        }

        public void AssertCleanLoaderOnly()
        {
            Assert.True(File.Exists(Path.Combine(Canonical, "winhttp.dll")));
            Assert.True(File.Exists(Path.Combine(Canonical, "BepInEx", "core", "loader.dll")));
            Assert.False(File.Exists(Path.Combine(Canonical, "BepInEx", "plugins", "private.dll")));
            Assert.False(File.Exists(Path.Combine(Canonical, "BepInEx", "config", "private.cfg")));
        }

        public void AssertRestored()
        {
            Assert.Equal("current-exe", File.ReadAllText(Path.Combine(Canonical, "FarmTogether2.exe")));
            Assert.Equal("original user tree", File.ReadAllText(Path.Combine(Canonical, "user-install.txt")));
            Assert.Equal("must not copy", File.ReadAllText(Path.Combine(Canonical, "BepInEx", "plugins", "private.dll")));
            if (ReadState().GetProperty("saveWasPresent").GetBoolean())
            {
                Assert.Equal("original save", File.ReadAllText(Path.Combine(Save, "original.sav")));
            }
            Assert.Equal("manifest must stay unchanged", File.ReadAllText(AppManifest));
            Assert.Equal("restored", ReadState().GetProperty("phase").GetString());
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static void CreateBuild(string root, string build)
        {
            Directory.CreateDirectory(Path.Combine(root, "FarmTogether2_Data", "il2cpp_data", "Metadata"));
            File.WriteAllText(Path.Combine(root, "FarmTogether2.exe"), $"{build}-exe");
            File.WriteAllText(Path.Combine(root, "GameAssembly.dll"), $"{build}-assembly");
            File.WriteAllText(Path.Combine(root, "FarmTogether2_Data", "il2cpp_data", "Metadata", "global-metadata.dat"), $"{build}-metadata");
        }

        private static void CreateLoader(string root)
        {
            Directory.CreateDirectory(Path.Combine(root, "BepInEx", "core"));
            Directory.CreateDirectory(Path.Combine(root, "BepInEx", "config"));
            File.WriteAllText(Path.Combine(root, "doorstop_config.ini"), "doorstop");
            File.WriteAllText(Path.Combine(root, ".doorstop_version"), "4");
            File.WriteAllText(Path.Combine(root, "winhttp.dll"), "loader");
            File.WriteAllText(Path.Combine(root, "BepInEx", "core", "loader.dll"), "core");
            File.WriteAllText(Path.Combine(root, "BepInEx", "config", "BepInEx.cfg"), "loader config");
        }

        private static SortedDictionary<string, string> Hashes(string root)
        {
            SortedDictionary<string, string> result = new(StringComparer.Ordinal);
            foreach (string relative in NativePaths)
            {
                result.Add(relative, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))))).ToLowerInvariant());
            }
            return result;
        }

        private static ProcessResult RunPowerShell(IEnumerable<string> arguments, string environmentName, string? environmentValue)
        {
            ProcessStartInfo startInfo = new("pwsh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
            if (environmentValue is not null)
            {
                startInfo.Environment[environmentName] = environmentValue;
            }

            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start pwsh.");
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return new ProcessResult(process.ExitCode, output, error);
        }
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error)
    {
        public void AssertSuccess() => Assert.True(ExitCode == 0, $"pwsh exited {ExitCode}\nstdout:\n{Output}\nstderr:\n{Error}");

        public void AssertFailure(string expectedText)
        {
            Assert.NotEqual(0, ExitCode);
            Assert.Contains(expectedText, $"{Output}\n{Error}", StringComparison.OrdinalIgnoreCase);
        }
    }
}
