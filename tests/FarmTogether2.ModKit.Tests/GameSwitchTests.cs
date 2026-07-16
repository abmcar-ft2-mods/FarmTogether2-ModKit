using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace FarmTogether2.ModKit.Tests;

[Trait("Category", "LongRunning")]
public sealed class GameSwitchTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private static readonly string SwitchScript = Path.Combine(Root, "scripts", "Switch-GameBuild.ps1");
    private static readonly string CaptureScript = Path.Combine(Root, "scripts", "Capture-InteropSnapshots.ps1");

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
        "after-copy-old-loader-dotnet",
        "after-copy-old-loader-BepInEx-core",
        "after-copy-old-loader-BepInEx-unity-libs",
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
        "after-copy-current-loader-dotnet",
        "after-copy-current-loader-BepInEx-core",
        "after-copy-current-loader-BepInEx-unity-libs",
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

    public static TheoryData<string, string> RestorePhaseEntryPoints => new()
    {
        { "prepared", "activate-old" },
        { "original-game-move-pending", "activate-old" },
        { "original-game-preserved", "activate-old" },
        { "original-save-move-pending", "activate-old" },
        { "originals-preserved", "activate-old" },
        { "old-copy-preparing", "activate-old" },
        { "old-copy-prepared", "activate-old" },
        { "old-activate-pending", "activate-old" },
        { "old-active", "activate-old" },
        { "old-deactivate-pending", "activate-current" },
        { "old-game-preserved", "activate-current" },
        { "old-save-move-pending", "activate-current" },
        { "old-save-preserved", "activate-current" },
        { "current-copy-preparing", "activate-current" },
        { "current-copy-prepared", "activate-current" },
        { "current-activate-pending", "activate-current" },
        { "current-smoke-active", "activate-current" },
        { "restore-old-game-pending", "restore-old" },
        { "restore-old-game-preserved", "restore-old" },
        { "restore-current-game-pending", "restore-current" },
        { "restore-current-game-preserved", "restore-current" },
        { "restore-no-active-game", "restore-no-active" },
        { "restore-old-save-pending", "restore-old" },
        { "restore-current-save-pending", "restore-current" },
        { "restore-save-preserved", "restore-current" },
        { "restore-original-game-pending", "restore-current" },
        { "restore-original-game-restored", "restore-current" },
        { "restore-original-save-pending", "restore-current" },
        { "restored", "restore-current" },
    };

    public static TheoryData<string, string> CaptureCrashPoints => new()
    {
        { "after-restore-pending-state", "old-active" },
        { "after-archive-restored-state", "restored" },
        { "after-activate-old", "none" },
        { "after-wait-old", "none" },
        { "after-create-old-preparing", "none" },
        { "after-copy-old-Assembly-CSharp.dll", "none" },
        { "after-copy-old-Il2Cppmscorlib.dll", "none" },
        { "after-copy-old-MilkstoneUnityExtensions.dll", "none" },
        { "after-copy-old-UnityEngine.CoreModule.dll", "none" },
        { "after-copy-old-UnityEngine.IMGUIModule.dll", "none" },
        { "after-copy-old-UnityEngine.InputLegacyModule.dll", "none" },
        { "after-copy-old-UnityEngine.TextRenderingModule.dll", "none" },
        { "after-fingerprint-old", "none" },
        { "after-promote-old", "none" },
        { "after-activate-current", "none" },
        { "after-wait-current", "none" },
        { "after-create-current-preparing", "none" },
        { "after-copy-current-Assembly-CSharp.dll", "none" },
        { "after-copy-current-Il2Cppmscorlib.dll", "none" },
        { "after-copy-current-MilkstoneUnityExtensions.dll", "none" },
        { "after-copy-current-UnityEngine.CoreModule.dll", "none" },
        { "after-copy-current-UnityEngine.IMGUIModule.dll", "none" },
        { "after-copy-current-UnityEngine.InputLegacyModule.dll", "none" },
        { "after-copy-current-UnityEngine.TextRenderingModule.dll", "none" },
        { "after-fingerprint-current", "none" },
        { "after-promote-current", "none" },
        { "after-write-supported-builds", "none" },
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

    [Fact]
    public void ActivateOldRejectsLoaderWithoutBundledDotnetDirectoryBeforeMutation()
    {
        using Fixture fixture = new();
        Directory.Delete(Path.Combine(fixture.Canonical, "dotnet"), recursive: true);

        fixture.RunSwitch("ActivateOld").AssertFailure("Required loader allowlist entry is missing");

        Assert.False(File.Exists(fixture.State));
        fixture.AssertOriginalTree();
    }

    [Theory]
    [InlineData("dotnet/coreclr.dll")]
    [InlineData("dotnet/System.Private.CoreLib.dll")]
    [InlineData("BepInEx/core/BepInEx.Unity.IL2CPP.dll")]
    public void ActivateOldRejectsMissingRequiredLoaderFileBeforeMutation(string relativePath)
    {
        using Fixture fixture = new();
        File.Delete(Path.Combine(fixture.Canonical, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        fixture.RunSwitch("ActivateOld").AssertFailure("required loader file is missing");

        Assert.False(File.Exists(fixture.State));
        fixture.AssertOriginalTree();
    }

    [Fact]
    public void ActivateOldRejectsLinkedRequiredLoaderFileBeforeMutation()
    {
        using Fixture fixture = new();
        string target = Path.Combine(fixture.DirectoryPath, "external-coreclr.dll");
        File.WriteAllText(target, "must not be followed");
        string coreClr = Path.Combine(fixture.Canonical, "dotnet", "coreclr.dll");
        File.Delete(coreClr);
        File.CreateSymbolicLink(coreClr, target);

        fixture.RunSwitch("ActivateOld").AssertFailure("symlink");

        Assert.False(File.Exists(fixture.State));
        fixture.AssertOriginalTree();
    }

    [Fact]
    public void ActivateOldRejectsLinkedOptionalLoaderDirectoryBeforeMutation()
    {
        using Fixture fixture = new();
        string unityLibraries = Path.Combine(fixture.Canonical, "BepInEx", "unity-libs");
        string target = Path.Combine(fixture.DirectoryPath, "external-unity-libs");
        Directory.Move(unityLibraries, target);
        Directory.CreateSymbolicLink(unityLibraries, target);

        fixture.RunSwitch("ActivateOld").AssertFailure("symlink");

        Assert.False(File.Exists(fixture.State));
        fixture.AssertOriginalTree();
    }

    [Fact]
    public void ActivateOldCrashResumeRejectsLinkedPartialDestinationBeforeWritingThroughIt()
    {
        using Fixture fixture = new();
        const string crashPoint = "after-copy-old-base";
        fixture.RunSwitch("ActivateOld", crashPoint).AssertFailure(crashPoint);
        string smoke = fixture.ReadState().GetProperty("oldSmokeGameDirectory").GetString()!;
        string target = Path.Combine(fixture.DirectoryPath, "external-loader-target");
        Directory.CreateDirectory(target);
        string linkedDotnet = Path.Combine(smoke, "dotnet");
        Directory.CreateSymbolicLink(linkedDotnet, target);

        fixture.RunSwitch("ActivateOld").AssertFailure("symlink");

        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
        Directory.Delete(linkedDotnet);
        fixture.RunSwitch("Restore").AssertSuccess();
        fixture.AssertRestored();
    }

    [Theory]
    [MemberData(nameof(ActivateOldCrashPoints))]
    [Trait("ReleaseShard", "2")]
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
    [Trait("ReleaseShard", "3")]
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
    [Trait("ReleaseShard", "3")]
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

    [Theory]
    [MemberData(nameof(RestorePhaseEntryPoints))]
    [Trait("ReleaseShard", "2")]
    public void EveryPersistedPhaseCanReenterRestoreAndConverge(string phase, string route)
    {
        using Fixture fixture = new();
        fixture.StopAtPhase(phase, route);
        Assert.Equal(phase, fixture.ReadState().GetProperty("phase").GetString());

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
    public void RestoreAllowsOnlyTheDecimalLastPlayedValueToChange()
    {
        using Fixture fixture = new();
        fixture.RunSwitch("ActivateOld").AssertSuccess();
        string changedManifest = fixture.RewriteAppManifest(lastPlayed: "1783922749");

        fixture.RunSwitch("Restore").AssertSuccess();

        fixture.AssertRestored(changedManifest);
    }

    [Fact]
    public void RestoreNormalizesOnlyTheActualAppStateLastPlayedValueToken()
    {
        using Fixture fixture = new();
        fixture.RewriteAppManifest(includeLastPlayedDecoys: true);
        fixture.RunSwitch("ActivateOld").AssertSuccess();
        string changedManifest = fixture.RewriteAppManifest(lastPlayed: "1783922749", includeLastPlayedDecoys: true);

        fixture.RunSwitch("Restore").AssertSuccess();

        fixture.AssertRestored(changedManifest);
    }

    [Fact]
    public void ActivateOldRejectsIncorrectRequiredAppManifestKeyCasingBeforeMutation()
    {
        using Fixture fixture = new();
        string changedManifest = fixture.InitialAppManifestText.Replace("\"LastPlayed\"", "\"lastplayed\"", StringComparison.Ordinal);
        File.WriteAllText(fixture.AppManifest, changedManifest);

        fixture.RunSwitch("ActivateOld").AssertFailure("incorrect casing");

        Assert.False(File.Exists(fixture.State));
        fixture.AssertOriginalTree(changedManifest);
    }

    [Theory]
    [InlineData("appid", "appid mismatch")]
    [InlineData("buildid", "buildid mismatch")]
    [InlineData("manifest", "current manifest")]
    [InlineData("depot", "InstalledDepots identity mismatch")]
    [InlineData("state-flags", "outside the permitted")]
    [InlineData("install-directory", "outside the permitted")]
    [InlineData("whitespace", "outside the permitted")]
    public void RestoreRejectsEveryOtherAppManifestIdentityOrByteChange(string change, string expectedError)
    {
        using Fixture fixture = new();
        fixture.RunSwitch("ActivateOld").AssertSuccess();
        string changedManifest = change switch
        {
            "appid" => fixture.RewriteAppManifest(appId: "2418529"),
            "buildid" => fixture.RewriteAppManifest(buildId: "24069958"),
            "manifest" => fixture.RewriteAppManifest(manifestId: "2755117260250472999"),
            "depot" => fixture.RewriteAppManifest(depotId: "2418599"),
            "state-flags" => fixture.RewriteAppManifest(stateFlags: "6"),
            "install-directory" => fixture.RewriteAppManifest(installDirectory: "Wrong Game"),
            "whitespace" => File.ReadAllText(fixture.AppManifest).Replace("\"Universe\"     \"1\"", "\"Universe\"      \"1\"", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(change), change, "Unknown appmanifest mutation."),
        };
        if (change == "whitespace")
        {
            File.WriteAllText(fixture.AppManifest, changedManifest);
        }

        fixture.RunSwitch("Restore").AssertFailure(expectedError);

        fixture.AssertActiveBuild("old");
        Assert.Equal(changedManifest, File.ReadAllText(fixture.AppManifest));
        string original = fixture.ReadState().GetProperty("originalGameDirectory").GetString()!;
        Assert.Equal("current-exe", File.ReadAllText(Path.Combine(original, "FarmTogether2.exe")));
    }

    [Fact]
    public void LegacyStateWithAnUnchangedManifestMigratesDuringRestore()
    {
        using Fixture fixture = new();
        fixture.RunSwitch("ActivateOld").AssertSuccess();
        fixture.RewriteStateAsLegacy();

        fixture.RunSwitch("Restore").AssertSuccess();

        fixture.AssertRestored();
        Assert.Equal(2, fixture.ReadState().GetProperty("schemaVersion").GetInt32());
    }

    [Theory]
    [InlineData("ActivateOld", false)]
    [InlineData("ActivateCurrent", true)]
    public void LegacyActiveStateMigrationIsPersistedEvenWhenTheActionHasNoPhaseTransition(string action, bool currentActive)
    {
        using Fixture fixture = new();
        if (currentActive)
        {
            fixture.PrepareCurrentActive();
        }
        else
        {
            fixture.RunSwitch("ActivateOld").AssertSuccess();
        }
        fixture.RewriteStateAsLegacy();

        fixture.RunSwitch(action).AssertSuccess();

        Assert.Equal(2, fixture.ReadState().GetProperty("schemaVersion").GetInt32());
        fixture.AssertActiveBuild(currentActive ? "current" : "old");
    }

    [Theory]
    [InlineData("before-journal-old-active")]
    [InlineData("after-journal-old-active")]
    public void LegacyActiveStateMigrationRecoversFromAtomicJournalCrashes(string crashPoint)
    {
        using Fixture fixture = new();
        fixture.RunSwitch("ActivateOld").AssertSuccess();
        fixture.RewriteStateAsLegacy();

        fixture.RunSwitch("ActivateOld", crashPoint).AssertFailure(crashPoint);
        fixture.RunSwitch("ActivateOld").AssertSuccess();

        Assert.Equal(2, fixture.ReadState().GetProperty("schemaVersion").GetInt32());
        fixture.AssertActiveBuild("old");
    }

    [Fact]
    public void LegacyTerminalStateCanBeValidatedBeforeTheCaptureArchivesIt()
    {
        using Fixture fixture = new();
        fixture.PrepareCurrentActive();
        fixture.RunSwitch("Restore").AssertSuccess();
        fixture.RewriteStateAsLegacy();

        fixture.RunSwitch("Restore").AssertSuccess();

        fixture.AssertRestored();
        Assert.Equal(1, fixture.ReadState().GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void LegacyStateRejectsEvenLastPlayedDriftBecauseItCannotProveTheOnlyChangedBytes()
    {
        using Fixture fixture = new();
        fixture.RunSwitch("ActivateOld").AssertSuccess();
        fixture.RewriteStateAsLegacy();
        fixture.RewriteAppManifest(lastPlayed: "1783922749");

        fixture.RunSwitch("Restore").AssertFailure("cannot be migrated safely");

        fixture.AssertActiveBuild("old");
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
    [InlineData("schemaVersion")]
    [InlineData("phase")]
    [InlineData("currentHashes")]
    [InlineData("currentDepotManifests")]
    public void ClosedJournalRejectsDuplicateJsonPropertiesAtEveryRelevantDepth(string scope)
    {
        using Fixture fixture = new();
        fixture.RunSwitch("ActivateOld").AssertSuccess();
        JsonElement state = fixture.ReadState();
        string original = state.GetProperty("originalGameDirectory").GetString()!;
        fixture.RewriteStateWithDuplicateProperty(scope);

        fixture.RunSwitch("Restore").AssertFailure("duplicate property");

        fixture.AssertActiveBuild("old");
        Assert.Equal("current-exe", File.ReadAllText(Path.Combine(original, "FarmTogether2.exe")));
    }

    [Theory]
    [InlineData("appManifestAppId")]
    [InlineData("oldBuildId")]
    [InlineData("currentManifestId")]
    [InlineData("currentDepotManifests")]
    [InlineData("saveWasPresent")]
    public void ClosedJournalRejectsJsonValuesWithWrongTypesBeforeMutation(string field)
    {
        using Fixture fixture = new();
        fixture.RunSwitch("ActivateOld").AssertSuccess();
        string original = fixture.ReadState().GetProperty("originalGameDirectory").GetString()!;
        fixture.RewriteStateFieldWithWrongType(field);

        fixture.RunSwitch("Restore").AssertFailure("JSON");

        fixture.AssertActiveBuild("old");
        Assert.Equal("current-exe", File.ReadAllText(Path.Combine(original, "FarmTogether2.exe")));
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
    public void ActivateOldRejectsOldDepotHashMismatchBeforeMutation()
    {
        using Fixture fixture = new();
        string[] entries = fixture.OldHashArguments.ToArray();
        string original = entries[0];
        entries[0] = original[..^1] + (original[^1] == '0' ? '1' : '0');

        fixture.RunSwitch("ActivateOld", expectedOldHashOverride: entries).AssertFailure("ExpectedOldNativeHash");

        Assert.False(File.Exists(fixture.State));
        fixture.AssertOriginalTree();
    }

    [Fact]
    public void SaveFingerprintRejectsDirectoryLinksBeforeWritingAJournal()
    {
        using Fixture fixture = new();
        string target = Path.Combine(fixture.DirectoryPath, "linked-save-target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "linked.sav"), "must not be hidden from the fingerprint");
        Directory.CreateSymbolicLink(Path.Combine(fixture.Save, "linked"), target);

        fixture.RunSwitch("ActivateOld").AssertFailure("links");

        Assert.False(File.Exists(fixture.State));
        fixture.AssertOriginalTree();
    }

    [Fact]
    public void SaveFingerprintKeepsCaseDistinctPathsOnCaseSensitiveFilesystems()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using Fixture fixture = new();
        File.WriteAllText(Path.Combine(fixture.Save, "a.sav"), "lower");
        File.WriteAllText(Path.Combine(fixture.Save, "A.sav"), "upper");
        string expected = Fixture.DirectoryFingerprint(fixture.Save);

        fixture.RunSwitch("ActivateOld").AssertSuccess();

        Assert.Equal(expected, fixture.ReadState().GetProperty("originalSaveFingerprint").GetString());
        fixture.RunSwitch("Restore").AssertSuccess();
    }

    [Fact]
    public void LoadedJournalPathTopologyIsRevalidatedBeforeMutation()
    {
        using Fixture fixture = new();
        const string crashPoint = "after-journal-prepared";
        fixture.RunSwitch("ActivateOld", crashPoint).AssertFailure(crashPoint);
        JsonObject state = JsonNode.Parse(File.ReadAllText(fixture.State))!.AsObject();
        state["downloadedOldDepot"] = fixture.Canonical;
        File.WriteAllText(fixture.State, state.ToJsonString());

        fixture.RunSwitch("Restore").AssertFailure("topology");

        fixture.AssertOriginalTree();
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.DirectoryPath, "*.modkit-*", SearchOption.TopDirectoryOnly));
    }

    [Theory]
    [InlineData("canonical")]
    [InlineData("save")]
    [InlineData("depot")]
    public void ActivateOldRejectsLinkedTreeRootsBeforeMutation(string root)
    {
        using Fixture fixture = new();

        fixture.RunLinkedRoot(root).AssertFailure("symlink");

        Assert.False(File.Exists(fixture.State));
        fixture.AssertOriginalTree();
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.DirectoryPath, "*.modkit-*", SearchOption.TopDirectoryOnly));
    }

    [Theory]
    [InlineData("canonical")]
    [InlineData("save")]
    [InlineData("depot")]
    [InlineData("appmanifest")]
    public void ActivateOldRejectsLinkedAncestorsBeforeMutation(string path)
    {
        using Fixture fixture = new();
        Fixture.IntegritySnapshot before = fixture.ReadIntegritySnapshot();

        fixture.RunLinkedAncestor(path, capture: false).AssertFailure("symlink");

        Assert.False(File.Exists(fixture.State));
        fixture.AssertIntegritySnapshot(before);
    }

    [Theory]
    [InlineData("state-in-canonical")]
    [InlineData("state-in-save")]
    [InlineData("state-in-depot")]
    [InlineData("canonical-equals-save")]
    [InlineData("canonical-contains-save")]
    [InlineData("save-contains-canonical")]
    [InlineData("save-is-filesystem-root")]
    [InlineData("canonical-equals-depot")]
    [InlineData("save-equals-depot")]
    public void ActivateOldRejectsOverlappingPathTopologyBeforeMutation(string scenario)
    {
        using Fixture fixture = new();

        ProcessResult result = fixture.RunInvalidTopology(scenario, out string attemptedState);

        result.AssertFailure("topology");
        Assert.False(File.Exists(attemptedState));
        fixture.AssertOriginalTree();
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.DirectoryPath, "*.modkit-*", SearchOption.TopDirectoryOnly));
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

    [Theory]
    [MemberData(nameof(CaptureCrashPoints))]
    [Trait("ReleaseShard", "1")]
    public void CaptureRestoresAfterEveryOperationFailureThenRerunsToSuccess(string crashPoint, string priorState)
    {
        using Fixture fixture = new();
        string? priorAttempt = fixture.PreparePriorCaptureState(priorState);

        fixture.RunCapture(crashPoint).AssertFailure(crashPoint);
        string? failedAttempt = File.Exists(fixture.State)
            ? fixture.ReadState().GetProperty("attemptId").GetString()
            : priorAttempt;
        fixture.AssertOriginalTree();
        if (File.Exists(fixture.State))
        {
            Assert.Equal("restored", fixture.ReadState().GetProperty("phase").GetString());
        }
        fixture.AssertNoPreparingDirectories();

        fixture.RunCapture().AssertSuccess();
        fixture.AssertOriginalTree();
        if (File.Exists(fixture.State))
        {
            Assert.Equal("restored", fixture.ReadState().GetProperty("phase").GetString());
        }
        fixture.AssertNoPreparingDirectories();
        Assert.NotNull(failedAttempt);
        Assert.True(File.Exists(fixture.RestoredArchive(failedAttempt!)));
        fixture.AssertCompletedSnapshot("23821227");
        fixture.AssertCompletedSnapshot("24069957");
        Assert.True(File.Exists(fixture.SupportedBuilds));
    }

    [Fact]
    public void CompletedSnapshotsAreFullyRevalidatedAndSkipAnotherLaunch()
    {
        using Fixture fixture = new();
        fixture.RunCapture().AssertSuccess();
        Assert.Equal(new[] { "23821227", "24069957" }, File.ReadAllLines(fixture.WaitLog));

        fixture.RunCapture().AssertSuccess();

        Assert.Equal(new[] { "23821227", "24069957" }, File.ReadAllLines(fixture.WaitLog));
        Assert.False(File.Exists(fixture.State));
        fixture.AssertOriginalTree();
    }

    [Fact]
    public void HardCrashOwnedPreparingDirectoryIsRemovedBeforeRerun()
    {
        using Fixture fixture = new();
        const string crashPoint = "after-copy-old-Assembly-CSharp.dll";

        fixture.RunCaptureHardCrash(crashPoint).AssertExitCode(86);
        Assert.NotEmpty(Directory.EnumerateDirectories(fixture.SnapshotRoot, "*.preparing", SearchOption.TopDirectoryOnly));

        fixture.RunCapture().AssertSuccess();

        fixture.AssertOriginalTree();
        fixture.AssertNoPreparingDirectories();
    }

    [Fact]
    public void CompletedSnapshotWithChangedDllIsRejectedBeforeAnotherSwitch()
    {
        using Fixture fixture = new();
        fixture.RunCapture().AssertSuccess();
        File.WriteAllText(Path.Combine(fixture.SnapshotRoot, "23821227", "Assembly-CSharp.dll"), "third snapshot hash");

        fixture.RunCapture().AssertFailure("snapshot");

        fixture.AssertOriginalTree();
        Assert.False(File.Exists(fixture.State));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("string-schema")]
    public void CompletedSnapshotRejectsDuplicateOrWrongJsonTypes(string corruption)
    {
        using Fixture fixture = new();
        fixture.RunCapture().AssertSuccess();
        string snapshotPath = Path.Combine(fixture.SnapshotRoot, "23821227", "snapshot.json");
        string json = File.ReadAllText(snapshotPath);
        json = corruption switch
        {
            "duplicate" => json.Replace("{\"schemaVersion\":1,", "{\"schemaVersion\":1,\"schemaVersion\":1,", StringComparison.Ordinal),
            "string-schema" => json.Replace("\"schemaVersion\":1", "\"schemaVersion\":\"1\"", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(corruption), corruption, "Unknown corruption."),
        };
        File.WriteAllText(snapshotPath, json);

        fixture.RunCapture().AssertFailure("snapshot");

        fixture.AssertOriginalTree();
        Assert.False(File.Exists(fixture.State));
    }

    [Theory]
    [InlineData("state-equals-supported")]
    [InlineData("supported-inside-snapshot")]
    [InlineData("exporter-inside-canonical")]
    [InlineData("supported-equals-exporter")]
    [InlineData("supported-equals-appmanifest")]
    [InlineData("state-equals-appmanifest")]
    [InlineData("snapshot-equals-appmanifest")]
    [InlineData("appmanifest-equals-switch")]
    [InlineData("appmanifest-equals-exporter")]
    [InlineData("appmanifest-equals-capture")]
    [InlineData("supported-equals-capture")]
    [InlineData("state-equals-switch")]
    [InlineData("snapshot-parent-link")]
    [InlineData("supported-parent-link")]
    public void CaptureRejectsConflictingOutputAndToolPathsBeforeGameMutation(string scenario)
    {
        using Fixture fixture = new();

        ProcessResult result = fixture.RunInvalidCaptureTopology(scenario, out Fixture.IntegritySnapshot before);

        result.AssertFailure("topology");
        Assert.False(File.Exists(fixture.State));
        fixture.AssertIntegritySnapshot(before);
    }

    [Theory]
    [InlineData("canonical")]
    [InlineData("save")]
    [InlineData("depot")]
    [InlineData("appmanifest")]
    public void CaptureRejectsLinkedAncestorsBeforeMutation(string path)
    {
        using Fixture fixture = new();
        Fixture.IntegritySnapshot before = fixture.ReadIntegritySnapshot();

        fixture.RunLinkedAncestor(path, capture: true).AssertFailure("symlink");

        Assert.False(File.Exists(fixture.State));
        fixture.AssertIntegritySnapshot(before);
    }

    [Fact]
    public void CaptureRejectsALinkedAncestorOfItsOwnToolingBeforeMutation()
    {
        using Fixture fixture = new();

        ProcessResult result = fixture.RunCaptureThroughLinkedToolAncestor(out Fixture.IntegritySnapshot before);

        result.AssertFailure("symlink");
        Assert.False(File.Exists(fixture.State));
        fixture.AssertIntegritySnapshot(before);
    }

    [Fact]
    public void CaptureReportsBothCaptureAndRestoreFailures()
    {
        using Fixture fixture = new();

        ProcessResult result = fixture.RunCaptureWithCaptureAndRestoreFailure();

        result.AssertFailure("primary capture failure");
        result.AssertFailure("Appmanifest");
    }

    [Fact]
    public void CaptureRevalidatesActiveNativeFilesBeforePromotingSnapshot()
    {
        using Fixture fixture = new();

        ProcessResult result = fixture.RunCaptureWithActiveNativeMutation();

        result.AssertFailure("hash mismatch");
        Assert.False(Directory.Exists(Path.Combine(fixture.SnapshotRoot, "23821227")));
        File.WriteAllText(Path.Combine(fixture.Canonical, "FarmTogether2.exe"), "old-exe");
        fixture.RunSwitch("Restore").AssertSuccess();
        fixture.AssertRestored();
    }

    [Fact]
    public void CaptureRejectsNon64HexCurrentHashBeforeGameMutation()
    {
        using Fixture fixture = new();

        fixture.RunCaptureWithInvalidNativeHash().AssertFailure("64-hex");

        Assert.False(File.Exists(fixture.State));
        fixture.AssertOriginalTree();
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.DirectoryPath, "*.modkit-*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void CaptureRejectsAnExistingRestoredArchiveWithoutMovingTheJournal()
    {
        using Fixture fixture = new();
        fixture.PrepareCurrentActive();
        fixture.RunSwitch("Restore").AssertSuccess();
        string attempt = fixture.ReadState().GetProperty("attemptId").GetString()!;
        File.WriteAllText(fixture.RestoredArchive(attempt), "occupied");

        fixture.RunCapture().AssertFailure("ambiguity");

        fixture.AssertRestored();
        Assert.Equal("occupied", File.ReadAllText(fixture.RestoredArchive(attempt)));
    }

    private static void AssertJsonSchema(JsonElement state)
    {
        Assert.Equal(2, state.GetProperty("schemaVersion").GetInt32());
        string[] expected =
        {
            "schemaVersion", "attemptId", "phase", "canonical", "saveDirectory", "appManifestPath",
            "downloadedOldDepot", "originalGameDirectory", "oldSmokeGameDirectory", "currentSmokeGameDirectory",
            "originalSaveDirectory", "oldSmokeSaveDirectory", "currentSmokeSaveDirectory", "saveWasPresent",
            "originalSaveFingerprint", "appManifestAppId", "appManifestNormalizedSha256", "currentDepotManifests",
            "oldBuildId", "currentBuildId", "oldManifestId",
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
            SnapshotRoot = Path.Combine(DirectoryPath, "snapshots");
            SupportedBuilds = Path.Combine(DirectoryPath, "supported-builds.json");
            FakeExporter = Path.Combine(DirectoryPath, "fake-exporter.ps1");
            CaptureDriver = Path.Combine(DirectoryPath, "capture-driver.ps1");
            WaitLog = Path.Combine(DirectoryPath, "wait.log");

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

            InitialAppManifestText = AppManifestText();
            File.WriteAllText(AppManifest, InitialAppManifestText);
            CurrentHashes = Hashes(Canonical);
            OldHashes = Hashes(OldDepot);
            CurrentHashArguments = CurrentHashes.Select(pair => $"{pair.Key}={pair.Value}").ToArray();
            OldHashArguments = OldHashes.Select(pair => $"{pair.Key}={pair.Value}").ToArray();
            WriteFakeExporter();
            WriteCaptureDriver();
        }

        public string DirectoryPath { get; }
        public string Canonical { get; }
        public string Save { get; }
        public string OldDepot { get; }
        public string AppManifest { get; }
        public string State { get; }
        public string SnapshotRoot { get; }
        public string SupportedBuilds { get; }
        public string FakeExporter { get; }
        public string CaptureDriver { get; }
        public string WaitLog { get; }
        public string InitialAppManifestText { get; }
        public IReadOnlyDictionary<string, string> CurrentHashes { get; }
        public IReadOnlyDictionary<string, string> OldHashes { get; }
        public IReadOnlyList<string> CurrentHashArguments { get; }
        public IReadOnlyList<string> OldHashArguments { get; }

        public sealed record IntegritySnapshot(
            string CanonicalTree,
            string SaveTree,
            string DepotTree,
            byte[] AppManifestBytes,
            byte[] CaptureScriptBytes,
            byte[] SwitchScriptBytes,
            byte[] ExporterBytes,
            bool StateExists,
            bool SnapshotRootExists,
            bool SupportedBuildsExists,
            bool WaitLogExists,
            IReadOnlyDictionary<string, byte[]> AdditionalToolBytes);

        public IntegritySnapshot ReadIntegritySnapshot(params string[] additionalToolPaths) => new(
            DirectoryTreeFingerprint(Canonical),
            DirectoryTreeFingerprint(Save),
            DirectoryTreeFingerprint(OldDepot),
            File.ReadAllBytes(AppManifest),
            File.ReadAllBytes(CaptureScript),
            File.ReadAllBytes(SwitchScript),
            File.ReadAllBytes(FakeExporter),
            File.Exists(State),
            Directory.Exists(SnapshotRoot),
            File.Exists(SupportedBuilds),
            File.Exists(WaitLog),
            additionalToolPaths.ToDictionary(path => path, File.ReadAllBytes, StringComparer.Ordinal));

        public void AssertIntegritySnapshot(IntegritySnapshot expected)
        {
            Assert.Equal(expected.CanonicalTree, DirectoryTreeFingerprint(Canonical));
            Assert.Equal(expected.SaveTree, DirectoryTreeFingerprint(Save));
            Assert.Equal(expected.DepotTree, DirectoryTreeFingerprint(OldDepot));
            Assert.Equal(expected.AppManifestBytes, File.ReadAllBytes(AppManifest));
            Assert.Equal(expected.CaptureScriptBytes, File.ReadAllBytes(CaptureScript));
            Assert.Equal(expected.SwitchScriptBytes, File.ReadAllBytes(SwitchScript));
            Assert.Equal(expected.ExporterBytes, File.ReadAllBytes(FakeExporter));
            Assert.Equal(expected.StateExists, File.Exists(State));
            Assert.Equal(expected.SnapshotRootExists, Directory.Exists(SnapshotRoot));
            Assert.Equal(expected.SupportedBuildsExists, File.Exists(SupportedBuilds));
            Assert.Equal(expected.WaitLogExists, File.Exists(WaitLog));
            foreach ((string path, byte[] bytes) in expected.AdditionalToolBytes)
            {
                Assert.Equal(bytes, File.ReadAllBytes(path));
            }
            Assert.Empty(Directory.EnumerateFileSystemEntries(DirectoryPath, "*.modkit-*", SearchOption.TopDirectoryOnly));
        }

        public ProcessResult RunSwitch(
            string action,
            string? crashPoint = null,
            IReadOnlyList<string>? expectedHashOverride = null,
            string? statePathOverride = null,
            string? canonicalOverride = null,
            string? saveOverride = null,
            string? depotOverride = null,
            string? appManifestOverride = null,
            IReadOnlyList<string>? expectedOldHashOverride = null)
        {
            static string Quote(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
            string selectedState = statePathOverride ?? State;
            string selectedCanonical = canonicalOverride ?? Canonical;
            string selectedSave = saveOverride ?? Save;
            string selectedDepot = depotOverride ?? OldDepot;
            string selectedAppManifest = appManifestOverride ?? AppManifest;
            StringBuilder invocation = new($"& {Quote(SwitchScript)} -Action {Quote(action)} -StatePath {Quote(selectedState)}");
            if (action == "ActivateOld")
            {
                invocation.Append($" -CanonicalGameDirectory {Quote(selectedCanonical)} -SaveDirectory {Quote(selectedSave)} -AppManifestPath {Quote(selectedAppManifest)} -DownloadedOldDepot {Quote(selectedDepot)}");
                invocation.Append(" -OldBuildId 23821227 -OldManifestId 7567480468616200523 -CurrentBuildId 24069957 -CurrentManifestId 2755117260250472086");
                invocation.Append($" -ExpectedOldNativeHash @({string.Join(',', (expectedOldHashOverride ?? OldHashArguments).Select(Quote))})");
                invocation.Append($" -ExpectedCurrentNativeHash @({string.Join(',', (expectedHashOverride ?? CurrentHashArguments).Select(Quote))})");
            }

            string driver = Path.Combine(DirectoryPath, $"switch-driver-{Guid.NewGuid():N}.ps1");
            var driverBody = new StringBuilder();
            driverBody.AppendLine("try {");
            driverBody.Append("    ").AppendLine(invocation.ToString());
            driverBody.AppendLine("} catch {");
            driverBody.AppendLine("    [Console]::Error.WriteLine($_.Exception.Message)");
            driverBody.AppendLine("    exit 1");
            driverBody.AppendLine("}");
            File.WriteAllText(
                driver,
                driverBody.ToString().Replace("\r\n", "\n", StringComparison.Ordinal),
                new UTF8Encoding(false));
            return RunPowerShell(new[] { "-File", driver }, "FARMT2_SWITCH_FAIL_AT", crashPoint);
        }

        public ProcessResult RunCapture(string? crashPoint = null) =>
            RunPowerShell(new[] { "-File", CaptureDriver }, "FARMT2_CAPTURE_FAIL_AT", crashPoint);

        public ProcessResult RunCaptureHardCrash(string crashPoint) =>
            RunPowerShell(new[] { "-File", CaptureDriver }, "FARMT2_CAPTURE_HARD_CRASH_AT", crashPoint);

        public ProcessResult RunCaptureWithInvalidNativeHash()
        {
            string original = CurrentHashArguments[0];
            string invalid = original[..^1];
            string body = File.ReadAllText(CaptureDriver).Replace(original, invalid, StringComparison.Ordinal);
            string driver = Path.Combine(DirectoryPath, "invalid-hash-capture-driver.ps1");
            File.WriteAllText(driver, body);
            return RunPowerShell(new[] { "-File", driver }, "FARMT2_CAPTURE_FAIL_AT", null);
        }

        public ProcessResult RunCaptureWithCaptureAndRestoreFailure()
        {
            static string Quote(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
            string body = File.ReadAllText(CaptureDriver);
            string hook = $$"""
                $hook = {
                  param($point)
                  if ($point -ceq 'after-activate-current') {
                    [IO.File]::WriteAllText('{{AppManifest.Replace("'", "''", StringComparison.Ordinal)}}', 'forced restore failure')
                    throw 'primary capture failure'
                  }
                }
                """;
            body = body.Replace("$wait = {", $"{hook}{Environment.NewLine}$wait = {{", StringComparison.Ordinal);
            body = body.Replace(
                $" -ContractExporterPath {Quote(FakeExporter)}",
                $" -ContractExporterPath {Quote(FakeExporter)} -OperationHook $hook",
                StringComparison.Ordinal);
            string driver = Path.Combine(DirectoryPath, "capture-and-restore-failure-driver.ps1");
            File.WriteAllText(driver, body);
            return RunPowerShell(new[] { "-File", driver }, "FARMT2_CAPTURE_FAIL_AT", null);
        }

        public ProcessResult RunCaptureWithActiveNativeMutation()
        {
            static string Quote(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
            string body = File.ReadAllText(CaptureDriver);
            string hook = $$"""
                $hook = {
                  param($point)
                  if ($point -ceq 'after-fingerprint-old') {
                    [IO.File]::WriteAllText('{{Path.Combine(Canonical, "FarmTogether2.exe").Replace("'", "''", StringComparison.Ordinal)}}', 'tampered-old-exe')
                  }
                }
                """;
            body = body.Replace("$wait = {", $"{hook}{Environment.NewLine}$wait = {{", StringComparison.Ordinal);
            body = body.Replace(
                $" -ContractExporterPath {Quote(FakeExporter)}",
                $" -ContractExporterPath {Quote(FakeExporter)} -OperationHook $hook",
                StringComparison.Ordinal);
            string driver = Path.Combine(DirectoryPath, "active-native-mutation-capture-driver.ps1");
            File.WriteAllText(driver, body);
            return RunPowerShell(new[] { "-File", driver }, "FARMT2_CAPTURE_FAIL_AT", null);
        }

        public ProcessResult RunInvalidCaptureTopology(string scenario, out IntegritySnapshot before)
        {
            static string Quote(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
            string body = File.ReadAllText(CaptureDriver);
            switch (scenario)
            {
                case "state-equals-supported":
                    body = body.Replace(Quote(SupportedBuilds), Quote(State), StringComparison.Ordinal);
                    break;
                case "supported-inside-snapshot":
                    body = body.Replace(Quote(SupportedBuilds), Quote(Path.Combine(SnapshotRoot, "supported-builds.json")), StringComparison.Ordinal);
                    break;
                case "exporter-inside-canonical":
                    string nestedExporter = Path.Combine(Canonical, "capture-exporter.ps1");
                    File.Copy(FakeExporter, nestedExporter);
                    body = body.Replace(Quote(FakeExporter), Quote(nestedExporter), StringComparison.Ordinal);
                    break;
                case "supported-equals-exporter":
                    body = body.Replace(Quote(SupportedBuilds), Quote(FakeExporter), StringComparison.Ordinal);
                    break;
                case "supported-equals-appmanifest":
                    body = body.Replace(Quote(SupportedBuilds), Quote(AppManifest), StringComparison.Ordinal);
                    break;
                case "state-equals-appmanifest":
                    body = body.Replace(Quote(State), Quote(AppManifest), StringComparison.Ordinal);
                    break;
                case "snapshot-equals-appmanifest":
                    body = body.Replace(Quote(SnapshotRoot), Quote(AppManifest), StringComparison.Ordinal);
                    break;
                case "appmanifest-equals-switch":
                    body = body.Replace(Quote(AppManifest), Quote(SwitchScript), StringComparison.Ordinal);
                    break;
                case "appmanifest-equals-exporter":
                    body = body.Replace(Quote(AppManifest), Quote(FakeExporter), StringComparison.Ordinal);
                    break;
                case "appmanifest-equals-capture":
                    body = body.Replace(Quote(AppManifest), Quote(CaptureScript), StringComparison.Ordinal);
                    break;
                case "supported-equals-capture":
                    body = body.Replace(Quote(SupportedBuilds), Quote(CaptureScript), StringComparison.Ordinal);
                    break;
                case "state-equals-switch":
                    body = body.Replace(Quote(State), Quote(SwitchScript), StringComparison.Ordinal);
                    break;
                case "snapshot-parent-link":
                    string snapshotTarget = Path.Combine(DirectoryPath, "snapshot-link-target");
                    string snapshotLink = Path.Combine(DirectoryPath, "snapshot-link");
                    Directory.CreateDirectory(snapshotTarget);
                    Directory.CreateSymbolicLink(snapshotLink, snapshotTarget);
                    body = body.Replace(Quote(SnapshotRoot), Quote(Path.Combine(snapshotLink, "snapshots")), StringComparison.Ordinal);
                    break;
                case "supported-parent-link":
                    string supportedTarget = Path.Combine(DirectoryPath, "supported-link-target");
                    string supportedLink = Path.Combine(DirectoryPath, "supported-link");
                    Directory.CreateDirectory(supportedTarget);
                    Directory.CreateSymbolicLink(supportedLink, supportedTarget);
                    body = body.Replace(Quote(SupportedBuilds), Quote(Path.Combine(supportedLink, "supported-builds.json")), StringComparison.Ordinal);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown capture topology scenario.");
            }
            before = ReadIntegritySnapshot();
            string driver = Path.Combine(DirectoryPath, $"invalid-capture-driver-{Guid.NewGuid():N}.ps1");
            File.WriteAllText(driver, body);
            return RunPowerShell(new[] { "-File", driver }, "FARMT2_CAPTURE_FAIL_AT", null);
        }

        public ProcessResult RunLinkedAncestor(string path, bool capture)
        {
            static string Quote(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
            string alias = Path.Combine(DirectoryPath, $"ancestor-link-{Guid.NewGuid():N}");
            Directory.CreateSymbolicLink(alias, DirectoryPath);
            string original = path switch
            {
                "canonical" => Canonical,
                "save" => Save,
                "depot" => OldDepot,
                "appmanifest" => AppManifest,
                _ => throw new ArgumentOutOfRangeException(nameof(path), path, "Unknown linked ancestor path."),
            };
            string throughLink = Path.Combine(alias, Path.GetRelativePath(DirectoryPath, original));
            if (capture)
            {
                string body = File.ReadAllText(CaptureDriver).Replace(Quote(original), Quote(throughLink), StringComparison.Ordinal);
                string driver = Path.Combine(DirectoryPath, $"linked-ancestor-capture-driver-{Guid.NewGuid():N}.ps1");
                File.WriteAllText(driver, body);
                return RunPowerShell(new[] { "-File", driver }, "FARMT2_CAPTURE_FAIL_AT", null);
            }
            return RunSwitch(
                "ActivateOld",
                canonicalOverride: path == "canonical" ? throughLink : Canonical,
                saveOverride: path == "save" ? throughLink : Save,
                depotOverride: path == "depot" ? throughLink : OldDepot,
                appManifestOverride: path == "appmanifest" ? throughLink : AppManifest);
        }

        public ProcessResult RunCaptureThroughLinkedToolAncestor(out IntegritySnapshot before)
        {
            static string Quote(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
            string target = Path.Combine(DirectoryPath, "linked-tool-target");
            string alias = Path.Combine(DirectoryPath, "linked-tool-parent");
            Directory.CreateDirectory(target);
            string captureCopy = Path.Combine(target, Path.GetFileName(CaptureScript));
            string switchCopy = Path.Combine(target, Path.GetFileName(SwitchScript));
            File.Copy(CaptureScript, captureCopy);
            File.Copy(SwitchScript, switchCopy);
            Directory.CreateSymbolicLink(alias, target);
            string linkedCapture = Path.Combine(alias, Path.GetFileName(CaptureScript));
            string body = File.ReadAllText(CaptureDriver).Replace(Quote(CaptureScript), Quote(linkedCapture), StringComparison.Ordinal);
            string driver = Path.Combine(DirectoryPath, "linked-tool-capture-driver.ps1");
            File.WriteAllText(driver, body);
            before = ReadIntegritySnapshot(captureCopy, switchCopy);
            return RunPowerShell(new[] { "-File", driver }, "FARMT2_CAPTURE_FAIL_AT", null);
        }

        public ProcessResult RunInvalidTopology(string scenario, out string attemptedState)
        {
            attemptedState = State;
            string canonical = Canonical;
            string save = Save;
            string depot = OldDepot;
            switch (scenario)
            {
                case "state-in-canonical":
                    attemptedState = Path.Combine(Canonical, "journal", "game-switch.json");
                    break;
                case "state-in-save":
                    attemptedState = Path.Combine(Save, "game-switch.json");
                    break;
                case "state-in-depot":
                    attemptedState = Path.Combine(OldDepot, "game-switch.json");
                    break;
                case "canonical-equals-save":
                    save = Canonical;
                    break;
                case "canonical-contains-save":
                    save = Path.Combine(Canonical, "nested-save");
                    Directory.CreateDirectory(save);
                    File.WriteAllText(Path.Combine(save, "nested.sav"), "nested save");
                    break;
                case "save-contains-canonical":
                    save = DirectoryPath;
                    break;
                case "save-is-filesystem-root":
                    save = Path.GetPathRoot(Canonical)!;
                    break;
                case "canonical-equals-depot":
                    depot = Canonical;
                    break;
                case "save-equals-depot":
                    depot = Save;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown topology scenario.");
            }
            return RunSwitch(
                "ActivateOld",
                statePathOverride: attemptedState,
                canonicalOverride: canonical,
                saveOverride: save,
                depotOverride: depot);
        }

        public ProcessResult RunLinkedRoot(string root)
        {
            string canonical = Canonical;
            string save = Save;
            string depot = OldDepot;
            switch (root)
            {
                case "canonical":
                    canonical = Path.Combine(DirectoryPath, "canonical-link");
                    Directory.CreateSymbolicLink(canonical, Canonical);
                    break;
                case "save":
                    save = Path.Combine(DirectoryPath, "save-link");
                    Directory.CreateSymbolicLink(save, Save);
                    break;
                case "depot":
                    depot = Path.Combine(DirectoryPath, "depot-link");
                    Directory.CreateSymbolicLink(depot, OldDepot);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(root), root, "Unknown linked root.");
            }
            return RunSwitch(
                "ActivateOld",
                canonicalOverride: canonical,
                saveOverride: save,
                depotOverride: depot);
        }

        public void PrepareCurrentActive()
        {
            RunSwitch("ActivateOld").AssertSuccess();
            Directory.CreateDirectory(Save);
            File.WriteAllText(Path.Combine(Save, "old-smoke.sav"), "old save");
            RunSwitch("ActivateCurrent").AssertSuccess();
        }

        public string? PreparePriorCaptureState(string priorState)
        {
            switch (priorState)
            {
                case "none":
                    return null;
                case "old-active":
                    RunSwitch("ActivateOld").AssertSuccess();
                    return ReadState().GetProperty("attemptId").GetString();
                case "restored":
                    PrepareCurrentActive();
                    RunSwitch("Restore").AssertSuccess();
                    return ReadState().GetProperty("attemptId").GetString();
                default:
                    throw new ArgumentOutOfRangeException(nameof(priorState), priorState, "Unknown prior capture state.");
            }
        }

        public void StopAtPhase(string phase, string route)
        {
            string crashPoint = $"after-journal-{phase}";
            switch (route)
            {
                case "activate-old":
                    RunSwitch("ActivateOld", crashPoint).AssertFailure(crashPoint);
                    break;
                case "activate-current":
                    RunSwitch("ActivateOld").AssertSuccess();
                    CreateSmokeSave("old-smoke.sav");
                    RunSwitch("ActivateCurrent", crashPoint).AssertFailure(crashPoint);
                    break;
                case "restore-old":
                    RunSwitch("ActivateOld").AssertSuccess();
                    CreateSmokeSave("old-smoke.sav");
                    RunSwitch("Restore", crashPoint).AssertFailure(crashPoint);
                    break;
                case "restore-current":
                    PrepareCurrentActive();
                    CreateSmokeSave("current-smoke.sav");
                    RunSwitch("Restore", crashPoint).AssertFailure(crashPoint);
                    break;
                case "restore-no-active":
                    const string preparationPoint = "after-journal-originals-preserved";
                    RunSwitch("ActivateOld", preparationPoint).AssertFailure(preparationPoint);
                    RunSwitch("Restore", crashPoint).AssertFailure(crashPoint);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(route), route, "Unknown phase setup route.");
            }
        }

        private void CreateSmokeSave(string name)
        {
            Directory.CreateDirectory(Save);
            File.WriteAllText(Path.Combine(Save, name), "smoke save");
        }

        public JsonElement ReadState()
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(State));
            return document.RootElement.Clone();
        }

        public string RestoredArchive(string attemptId) => Path.Combine(DirectoryPath, $"game-switch-{attemptId}-restored.json");

        public void AssertActiveBuild(string build)
        {
            Assert.True(Directory.Exists(Canonical));
            Assert.Equal($"{build}-exe", File.ReadAllText(Path.Combine(Canonical, "FarmTogether2.exe")));
        }

        public void AssertCleanLoaderOnly()
        {
            Assert.True(File.Exists(Path.Combine(Canonical, "winhttp.dll")));
            Assert.True(File.Exists(Path.Combine(Canonical, "dotnet", "coreclr.dll")));
            Assert.True(File.Exists(Path.Combine(Canonical, "dotnet", "System.Private.CoreLib.dll")));
            Assert.True(File.Exists(Path.Combine(Canonical, "BepInEx", "core", "loader.dll")));
            Assert.True(File.Exists(Path.Combine(Canonical, "BepInEx", "core", "BepInEx.Unity.IL2CPP.dll")));
            Assert.True(File.Exists(Path.Combine(Canonical, "BepInEx", "unity-libs", "2022.3.62.zip")));
            Assert.False(File.Exists(Path.Combine(Canonical, "BepInEx", "plugins", "private.dll")));
            Assert.False(File.Exists(Path.Combine(Canonical, "BepInEx", "config", "private.cfg")));
        }

        public void AssertRestored(string? expectedAppManifest = null)
        {
            Assert.Equal("current-exe", File.ReadAllText(Path.Combine(Canonical, "FarmTogether2.exe")));
            Assert.Equal("original user tree", File.ReadAllText(Path.Combine(Canonical, "user-install.txt")));
            Assert.Equal("must not copy", File.ReadAllText(Path.Combine(Canonical, "BepInEx", "plugins", "private.dll")));
            if (ReadState().GetProperty("saveWasPresent").GetBoolean())
            {
                Assert.Equal("original save", File.ReadAllText(Path.Combine(Save, "original.sav")));
            }
            Assert.Equal(expectedAppManifest ?? InitialAppManifestText, File.ReadAllText(AppManifest));
            Assert.Equal("restored", ReadState().GetProperty("phase").GetString());
        }

        public void AssertOriginalTree(string? expectedAppManifest = null)
        {
            Assert.Equal("current-exe", File.ReadAllText(Path.Combine(Canonical, "FarmTogether2.exe")));
            Assert.Equal("original user tree", File.ReadAllText(Path.Combine(Canonical, "user-install.txt")));
            Assert.Equal("original save", File.ReadAllText(Path.Combine(Save, "original.sav")));
            Assert.Equal(expectedAppManifest ?? InitialAppManifestText, File.ReadAllText(AppManifest));
        }

        public void AssertCompletedSnapshot(string buildId)
        {
            string path = Path.Combine(SnapshotRoot, buildId);
            string snapshotPath = Path.Combine(path, "snapshot.json");
            Assert.True(File.Exists(snapshotPath));
            using JsonDocument snapshot = JsonDocument.Parse(File.ReadAllText(snapshotPath));
            Assert.Equal(
                new[] { "aggregateSha256", "assemblies", "assemblyMetadataSha256", "schemaVersion", "steamBuildId" },
                snapshot.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
            foreach (string assembly in InteropAssemblies)
            {
                Assert.True(File.Exists(Path.Combine(path, $"{assembly}.dll")), $"{buildId}/{assembly}.dll");
            }
        }

        public void AssertNoPreparingDirectories()
        {
            if (Directory.Exists(SnapshotRoot))
            {
                Assert.Empty(Directory.EnumerateDirectories(SnapshotRoot, "*.preparing", SearchOption.TopDirectoryOnly));
            }
        }

        public string RewriteAppManifest(
            string lastPlayed = "1783921384",
            string appId = "2418520",
            string buildId = "24069957",
            string depotId = "2418521",
            string manifestId = "2755117260250472086",
            string stateFlags = "4",
            string installDirectory = "Farm Together 2",
            bool includeExtraDepot = false,
            bool includeLastPlayedDecoys = false)
        {
            string text = AppManifestText(lastPlayed, appId, buildId, depotId, manifestId, stateFlags, installDirectory, includeExtraDepot, includeLastPlayedDecoys);
            File.WriteAllText(AppManifest, text);
            return text;
        }

        public void RewriteStateAsLegacy()
        {
            JsonObject state = JsonNode.Parse(File.ReadAllText(State))!.AsObject();
            state["schemaVersion"] = 1;
            state.Remove("appManifestAppId");
            state.Remove("appManifestNormalizedSha256");
            state.Remove("currentDepotManifests");
            state["appManifestSha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(AppManifest))).ToLowerInvariant();
            File.WriteAllText(State, state.ToJsonString());
        }

        public void RewriteStateWithDuplicateProperty(string scope)
        {
            string json = File.ReadAllText(State);
            JsonObject state = JsonNode.Parse(json)!.AsObject();
            string target;
            string replacement;
            switch (scope)
            {
                case "schemaVersion":
                    target = "\"schemaVersion\":2";
                    replacement = $"{target},{target}";
                    break;
                case "phase":
                    string phase = state["phase"]!.GetValue<string>();
                    target = $"\"phase\":{JsonSerializer.Serialize(phase)}";
                    replacement = $"{target},{target}";
                    break;
                case "currentHashes":
                case "currentDepotManifests":
                    JsonObject map = state[scope]!.AsObject();
                    KeyValuePair<string, JsonNode?> entry = map.First();
                    string serializedKey = JsonSerializer.Serialize(entry.Key);
                    string serializedValue = entry.Value!.ToJsonString();
                    string pair = $"{serializedKey}:{serializedValue}";
                    target = $"\"{scope}\":{{{pair}";
                    replacement = $"\"{scope}\":{{{pair},{pair}";
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown duplicate property scope.");
            }
            string changed = json.Replace(target, replacement, StringComparison.Ordinal);
            Assert.NotEqual(json, changed);
            File.WriteAllText(State, changed);
        }

        public void RewriteStateFieldWithWrongType(string field)
        {
            JsonObject state = JsonNode.Parse(File.ReadAllText(State))!.AsObject();
            switch (field)
            {
                case "appManifestAppId":
                case "oldBuildId":
                case "currentManifestId":
                    state[field] = long.Parse(state[field]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "currentDepotManifests":
                    JsonObject depots = state[field]!.AsObject();
                    string depot = depots.First().Key;
                    depots[depot] = long.Parse(depots[depot]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "saveWasPresent":
                    state[field] = state[field]!.GetValue<bool>() ? "true" : "false";
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(field), field, "Unknown wrong-type field.");
            }
            File.WriteAllText(State, state.ToJsonString());
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

        private static readonly string[] InteropAssemblies =
        {
            "Assembly-CSharp",
            "Il2Cppmscorlib",
            "MilkstoneUnityExtensions",
            "UnityEngine.CoreModule",
            "UnityEngine.IMGUIModule",
            "UnityEngine.InputLegacyModule",
            "UnityEngine.TextRenderingModule",
        };

        private static string AppManifestText(
            string lastPlayed = "1783921384",
            string appId = "2418520",
            string buildId = "24069957",
            string depotId = "2418521",
            string manifestId = "2755117260250472086",
            string stateFlags = "4",
            string installDirectory = "Farm Together 2",
            bool includeExtraDepot = false,
            bool includeLastPlayedDecoys = false)
        {
            string extraDepot = includeExtraDepot
                ? "\t\t\"2418522\"\n\t\t{\n\t\t\t\"manifest\"\t\t\"9999999999999999999\"\n\t\t}\n"
                : string.Empty;
            string lastPlayedDecoys = includeLastPlayedDecoys
                ? "\t\"FakeValue\"\t\t\"LastPlayed\"\n\t\"123\"\t\t\"not-the-runtime-value\"\n\t\"Nested\"\n\t{\n\t\t\"LastPlayed\"\t\t\"456\"\n\t}\n\t// \"LastPlayed\" \"789\"\n"
                : string.Empty;
            return $$"""
                "AppState"
                {
                    "appid"        "{{appId}}"
                    "Universe"     "1"
                    "name"         "Farm Together 2"
                    "StateFlags"   "{{stateFlags}}"
                    "installdir"   "{{installDirectory}}"
                {{lastPlayedDecoys}}    "LastUpdated"  "1783921000"
                    "LastPlayed"   "{{lastPlayed}}"
                    "buildid"      "{{buildId}}"
                    "LastOwner"    "76561198000000000"
                    "InstalledDepots"
                    {
                        "{{depotId}}"
                        {
                            "manifest"     "{{manifestId}}"
                            "size"         "123456789"
                        }
                {{extraDepot}}    }
                }
                """ + Environment.NewLine;
        }

        private void WriteCaptureDriver()
        {
            static string Quote(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
            string oldHashes = string.Join(',', OldHashArguments.Select(Quote));
            string hashes = string.Join(',', CurrentHashArguments.Select(Quote));
            string body = $$"""
                $ErrorActionPreference = 'Stop'
                $wait = {
                  param($buildId, $gameDirectory)
                  Add-Content -LiteralPath '{{WaitLog.Replace("'", "''", StringComparison.Ordinal)}}' -Value $buildId
                  $interop = Join-Path $gameDirectory 'BepInEx/interop'
                  New-Item -ItemType Directory -Force -Path $interop | Out-Null
                  foreach ($name in 'Assembly-CSharp','Il2Cppmscorlib','MilkstoneUnityExtensions','UnityEngine.CoreModule','UnityEngine.IMGUIModule','UnityEngine.InputLegacyModule','UnityEngine.TextRenderingModule') {
                    Set-Content -LiteralPath (Join-Path $interop "$name.dll") -Value "$buildId-$name" -NoNewline
                  }
                  New-Item -ItemType Directory -Force -Path '{{Save.Replace("'", "''", StringComparison.Ordinal)}}' | Out-Null
                  Set-Content -LiteralPath (Join-Path '{{Save.Replace("'", "''", StringComparison.Ordinal)}}' "$buildId.sav") -Value $buildId -NoNewline
                }
                & '{{CaptureScript.Replace("'", "''", StringComparison.Ordinal)}}' -StatePath '{{State.Replace("'", "''", StringComparison.Ordinal)}}' -SnapshotRoot '{{SnapshotRoot.Replace("'", "''", StringComparison.Ordinal)}}' -CanonicalGameDirectory '{{Canonical.Replace("'", "''", StringComparison.Ordinal)}}' -SaveDirectory '{{Save.Replace("'", "''", StringComparison.Ordinal)}}' -AppManifestPath '{{AppManifest.Replace("'", "''", StringComparison.Ordinal)}}' -DownloadedOldDepot '{{OldDepot.Replace("'", "''", StringComparison.Ordinal)}}' -OldBuildId 23821227 -OldManifestId 7567480468616200523 -CurrentBuildId 24069957 -CurrentManifestId 2755117260250472086 -ExpectedOldNativeHash @({{oldHashes}}) -ExpectedCurrentNativeHash @({{hashes}}) -ModBuild @('com.abmcar.farmtogether2.autosellmod=24069957','com.abmcar.farmtogether2.qolmod=23821227','com.abmcar.farmtogether2.automodrangemod=23821227','com.abmcar.farmtogether2.farmhandspeedmod=23821227') -SupportedBuildsOutput '{{SupportedBuilds.Replace("'", "''", StringComparison.Ordinal)}}' -WaitForLaunchAndClose $wait -ContractExporterPath '{{FakeExporter.Replace("'", "''", StringComparison.Ordinal)}}'
                """;
            File.WriteAllText(CaptureDriver, body);
        }

        private void WriteFakeExporter()
        {
            string body = """
                [CmdletBinding()]
                param(
                  [string]$SnapshotInteropDirectory,
                  [string]$SteamBuildId,
                  [string]$SnapshotOutput,
                  [string[]]$BuildSnapshot,
                  [string[]]$ModBuild,
                  [string]$SupportedBuildsOutput
                )
                $ErrorActionPreference = 'Stop'
                $names = @('Assembly-CSharp','Il2Cppmscorlib','MilkstoneUnityExtensions','UnityEngine.CoreModule','UnityEngine.IMGUIModule','UnityEngine.InputLegacyModule','UnityEngine.TextRenderingModule')
                function Write-JsonAtomic($value, $path) {
                  $temporary = "$path.tmp"
                  $json = ($value | ConvertTo-Json -Depth 8 -Compress) + "`n"
                  [IO.File]::WriteAllText($temporary, $json, [Text.UTF8Encoding]::new($false))
                  [IO.File]::Move($temporary, $path, (Test-Path -LiteralPath $path))
                }
                if ($SnapshotInteropDirectory) {
                  $hashes = [ordered]@{}
                  $assemblies = @()
                  foreach ($name in $names) {
                    $path = Join-Path $SnapshotInteropDirectory "$name.dll"
                    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "missing assembly: $name" }
                    $hashes[$name] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
                    $assemblies += [ordered]@{name=$name;version='0.0.0.0';culture='';publicKeyToken=''}
                  }
                  $vector = ($hashes.GetEnumerator() | ForEach-Object { "$($_.Key)`t$($_.Value)" }) -join "`n"
                  $aggregate = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($vector))).ToLowerInvariant()
                  Write-JsonAtomic ([ordered]@{schemaVersion=1;steamBuildId="$SteamBuildId";aggregateSha256=$aggregate;assemblyMetadataSha256=$hashes;assemblies=$assemblies}) $SnapshotOutput
                  return
                }
                if (-not $BuildSnapshot -or $BuildSnapshot.Count -ne 2) { throw 'exactly two snapshots required' }
                if (-not $ModBuild -or $ModBuild.Count -ne 4) { throw 'exactly four mod builds required' }
                $builds = [ordered]@{}
                $assemblies = $null
                foreach ($path in $BuildSnapshot) {
                  $snapshot = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
                  $builds[$snapshot.steamBuildId] = [ordered]@{aggregateSha256=$snapshot.aggregateSha256;assemblyMetadataSha256=$snapshot.assemblyMetadataSha256}
                  $assemblies = $snapshot.assemblies
                }
                $mods = [ordered]@{}
                foreach ($entry in $ModBuild) {
                  $parts = $entry.Split('=', 2)
                  $mods[$parts[0]] = [ordered]@{steamBuildId=$parts[1]}
                }
                Write-JsonAtomic ([ordered]@{schemaVersion=1;mods=$mods;builds=$builds;assemblies=$assemblies}) $SupportedBuildsOutput
                """;
            File.WriteAllText(FakeExporter, body);
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
            Directory.CreateDirectory(Path.Combine(root, "dotnet"));
            Directory.CreateDirectory(Path.Combine(root, "BepInEx", "core"));
            Directory.CreateDirectory(Path.Combine(root, "BepInEx", "config"));
            Directory.CreateDirectory(Path.Combine(root, "BepInEx", "unity-libs"));
            File.WriteAllText(Path.Combine(root, "doorstop_config.ini"), "doorstop");
            File.WriteAllText(Path.Combine(root, ".doorstop_version"), "4");
            File.WriteAllText(Path.Combine(root, "winhttp.dll"), "loader");
            File.WriteAllText(Path.Combine(root, "dotnet", "coreclr.dll"), "runtime");
            File.WriteAllText(Path.Combine(root, "dotnet", "System.Private.CoreLib.dll"), "core library");
            File.WriteAllText(Path.Combine(root, "BepInEx", "core", "loader.dll"), "core");
            File.WriteAllText(Path.Combine(root, "BepInEx", "core", "BepInEx.Unity.IL2CPP.dll"), "entry point");
            File.WriteAllText(Path.Combine(root, "BepInEx", "config", "BepInEx.cfg"), "loader config");
            File.WriteAllText(Path.Combine(root, "BepInEx", "unity-libs", "2022.3.62.zip"), "unity libraries");
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

        public static string DirectoryFingerprint(string root)
        {
            string vector = string.Join(
                '\n',
                Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Select(path => new
                    {
                        Path = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'),
                        Hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
                    })
                    .OrderBy(entry => entry.Path, StringComparer.Ordinal)
                    .Select(entry => $"{entry.Path}\t{entry.Hash}"));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(vector))).ToLowerInvariant();
        }

        private static string DirectoryTreeFingerprint(string root)
        {
            if (!Directory.Exists(root))
            {
                return "<absent>";
            }
            List<string> entries = new() { "directory\t." };
            entries.AddRange(
                Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                    .Select(path => $"directory\t{Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/')}")
                    .Order(StringComparer.Ordinal));
            entries.AddRange(
                Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Select(path =>
                    {
                        string relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
                        return $"file\t{relative}\t{hash}";
                    })
                    .Order(StringComparer.Ordinal));
            entries.Sort(StringComparer.Ordinal);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', entries)))).ToLowerInvariant();
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


        public void AssertExitCode(int expected) =>
            Assert.True(ExitCode == expected, $"pwsh exited {ExitCode}, expected {expected}\nstdout:\n{Output}\nstderr:\n{Error}");
    }
}
