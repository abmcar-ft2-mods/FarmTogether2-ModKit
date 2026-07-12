using System.Collections.ObjectModel;

namespace FarmTogether2.CompatibilityVerifier;

public sealed record VerifyOptions(
    string ContractPath,
    string SupportedBuildsPath,
    string ModId,
    string InteropDirectory,
    string StubPluginPath,
    string RealPluginPath,
    string PlayerPackagePath,
    string ReportPath);

public sealed record VerificationFacts(
    int AssemblyIdentitiesVerified,
    int ContractTypesVerified,
    int ContractMembersVerified,
    int EnumValuesVerified,
    int RuntimeTargetsVerified,
    int PluginAssemblyReferencesVerified,
    int PluginTypeReferencesVerified,
    int PluginMemberReferencesVerified,
    int PlayerPackageDllsVerified);

public sealed record VerificationReport(
    int SchemaVersion,
    string ModId,
    string SteamBuildId,
    string AggregateSha256,
    IReadOnlyDictionary<string, string> AssemblyMetadataSha256,
    string StubPluginSha256,
    string RealPluginSha256,
    string PlayerPackageSha256,
    VerificationFacts Facts);

internal sealed record AssemblyIdentity(string Name, string Version, string Culture, string PublicKeyToken);

internal sealed record SupportedBuild(
    string AggregateSha256,
    IReadOnlyDictionary<string, string> AssemblyMetadataSha256);

internal sealed record SupportedBuilds(
    IReadOnlyDictionary<string, string> ModBuilds,
    IReadOnlyDictionary<string, SupportedBuild> Builds,
    IReadOnlyDictionary<string, AssemblyIdentity> Assemblies);

internal sealed record InteropSnapshot(
    string AggregateSha256,
    IReadOnlyDictionary<string, string> AssemblyMetadataSha256,
    IReadOnlyDictionary<string, AssemblyIdentity> Assemblies);

internal sealed record PluginReferenceResult(
    int AssemblyReferences,
    int TypeReferences,
    int MemberReferences,
    IReadOnlySet<string> ContractTypeKeys,
    IReadOnlySet<string> ContractMemberKeys);

internal static class ApprovedApi
{
    internal static readonly ReadOnlyDictionary<string, string> Assemblies =
        new(new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["Assembly-CSharp"] = "0.0.0.0",
            ["Il2Cppmscorlib"] = "4.0.0.0",
            ["MilkstoneUnityExtensions"] = "1.0.0.0",
            ["UnityEngine.CoreModule"] = "0.0.0.0",
            ["UnityEngine.IMGUIModule"] = "0.0.0.0",
            ["UnityEngine.InputLegacyModule"] = "0.0.0.0",
            ["UnityEngine.TextRenderingModule"] = "0.0.0.0"
        });

    internal static readonly ReadOnlyDictionary<string, string> ModAssemblies =
        new(new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["com.abmcar.farmtogether2.automodrangemod"] = "FarmTogether2.AutoModRangeMod",
            ["com.abmcar.farmtogether2.autosellmod"] = "FarmTogether2.AutoSellMod",
            ["com.abmcar.farmtogether2.farmhandspeedmod"] = "FarmTogether2.FarmhandSpeedMod",
            ["com.abmcar.farmtogether2.qolmod"] = "FarmTogether2.QoLMod"
        });
}
