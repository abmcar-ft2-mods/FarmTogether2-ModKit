using FarmTogether2.ContractModel;
using System.Text;
using System.Text.Json;

namespace FarmTogether2.CompatibilityVerifier;

public static class CompatibilityVerifier
{
    public static VerificationReport Verify(VerifyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        VerifyOptions normalized = NormalizeAndValidatePaths(options);
        SupportedBuilds supported = SupportedBuildsJson.Load(normalized.SupportedBuildsPath);
        if (!supported.ModBuilds.TryGetValue(normalized.ModId, out string? buildId))
            throw new InvalidDataException($"Mod ID '{normalized.ModId}' is not approved by supported-builds.");
        if (!ApprovedApi.ModAssemblies.TryGetValue(normalized.ModId, out string? expectedPluginAssembly))
            throw new InvalidDataException($"Mod ID '{normalized.ModId}' has no approved plugin identity.");

        GameApiContract contract;
        try
        {
            contract = ContractJson.Load(normalized.ContractPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("Game API contract could not be read.", exception);
        }
        ValidateContractDomain(contract);

        InteropSnapshot actual = CanonicalMetadata.ReadInteropDirectory(normalized.InteropDirectory);
        SupportedBuild expected = supported.Builds[buildId];
        VerifySnapshot(actual, expected, supported.Assemblies);
        string initialStubHash = CanonicalMetadata.LowerFileSha256(normalized.StubPluginPath);
        string initialRealHash = CanonicalMetadata.LowerFileSha256(normalized.RealPluginPath);
        string initialPackageHash = CanonicalMetadata.LowerFileSha256(normalized.PlayerPackagePath);

        VerificationFacts facts = new(
            AssemblyIdentitiesVerified: ApprovedApi.Assemblies.Count,
            ContractTypesVerified: 0,
            ContractMembersVerified: 0,
            EnumValuesVerified: 0,
            RuntimeTargetsVerified: 0,
            PluginAssemblyReferencesVerified: 0,
            PluginTypeReferencesVerified: 0,
            PluginMemberReferencesVerified: 0,
            PlayerPackageDllsVerified: 0);

        PluginReferenceResult pluginReferences = PluginReferenceVerifier.Verify(
            normalized.StubPluginPath,
            normalized.RealPluginPath,
            expectedPluginAssembly,
            contract);
        facts = facts with
        {
            PluginAssemblyReferencesVerified = pluginReferences.AssemblyReferences,
            PluginTypeReferencesVerified = pluginReferences.TypeReferences,
            PluginMemberReferencesVerified = pluginReferences.MemberReferences
        };

        using (AssemblyCatalog catalog = new(normalized.InteropDirectory))
        {
            facts = catalog.VerifyContract(
                contract,
                normalized.ModId,
                pluginReferences.ContractTypeKeys,
                pluginReferences.ContractMemberKeys,
                facts);
        }

        int packageDlls = PlayerPackageVerifier.Verify(
            normalized.PlayerPackagePath,
            normalized.RealPluginPath,
            expectedPluginAssembly);
        facts = facts with { PlayerPackageDllsVerified = packageDlls };

        InteropSnapshot finalSnapshot = CanonicalMetadata.ReadInteropDirectory(normalized.InteropDirectory);
        if (!string.Equals(actual.AggregateSha256, finalSnapshot.AggregateSha256, StringComparison.Ordinal) ||
            actual.AssemblyMetadataSha256.Any(pair => !string.Equals(pair.Value, finalSnapshot.AssemblyMetadataSha256[pair.Key], StringComparison.Ordinal)) ||
            actual.Assemblies.Any(pair => pair.Value != finalSnapshot.Assemblies[pair.Key]))
        {
            throw new InvalidDataException("Interop metadata changed during compatibility verification.");
        }
        string finalStubHash = CanonicalMetadata.LowerFileSha256(normalized.StubPluginPath);
        string finalRealHash = CanonicalMetadata.LowerFileSha256(normalized.RealPluginPath);
        string finalPackageHash = CanonicalMetadata.LowerFileSha256(normalized.PlayerPackagePath);
        if (!string.Equals(initialStubHash, finalStubHash, StringComparison.Ordinal) ||
            !string.Equals(initialRealHash, finalRealHash, StringComparison.Ordinal) ||
            !string.Equals(initialPackageHash, finalPackageHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A plugin or player package changed during compatibility verification.");
        }

        SortedDictionary<string, string> reportHashes = new(StringComparer.Ordinal);
        foreach ((string name, string hash) in actual.AssemblyMetadataSha256)
            reportHashes.Add(name, hash);
        VerificationReport report = new(
            SchemaVersion: 1,
            ModId: normalized.ModId,
            SteamBuildId: buildId,
            AggregateSha256: actual.AggregateSha256,
            AssemblyMetadataSha256: reportHashes,
            StubPluginSha256: finalStubHash,
            RealPluginSha256: finalRealHash,
            PlayerPackageSha256: finalPackageHash,
            Facts: facts);
        WriteReportAtomically(report, normalized.ReportPath);
        return report;
    }

    private static VerifyOptions NormalizeAndValidatePaths(VerifyOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ModId))
            throw new ArgumentException("ModId must contain a value.", nameof(options));
        string contract = FullPath(options.ContractPath, nameof(options.ContractPath));
        string supported = FullPath(options.SupportedBuildsPath, nameof(options.SupportedBuildsPath));
        string interop = FullPath(options.InteropDirectory, nameof(options.InteropDirectory));
        string stub = FullPath(options.StubPluginPath, nameof(options.StubPluginPath));
        string real = FullPath(options.RealPluginPath, nameof(options.RealPluginPath));
        string package = FullPath(options.PlayerPackagePath, nameof(options.PlayerPackagePath));
        string report = FullPath(options.ReportPath, nameof(options.ReportPath));
        StringComparer pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        string[] inputs = [contract, supported, stub, real, package];
        if (inputs.Distinct(pathComparer).Count() != inputs.Length)
            throw new ArgumentException("Every verifier input file path must be distinct.", nameof(options));
        if (inputs.Any(input => pathComparer.Equals(input, report)))
            throw new ArgumentException("Report path must be distinct from every input path.", nameof(options));
        string interopPrefix = interop.EndsWith(Path.DirectorySeparatorChar)
            ? interop
            : interop + Path.DirectorySeparatorChar;
        if (report.StartsWith(interopPrefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Report path must be outside the interop directory.", nameof(options));
        if (Directory.Exists(report))
            throw new InvalidDataException("Report path is a directory.");
        string? reportParent = Path.GetDirectoryName(report);
        if (string.IsNullOrEmpty(reportParent) || !Directory.Exists(reportParent))
            throw new InvalidDataException("Report directory does not exist.");
        return new VerifyOptions(contract, supported, options.ModId, interop, stub, real, package, report);
    }

    private static string FullPath(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} must contain a value.", name);
        return Path.GetFullPath(value);
    }

    private static void VerifySnapshot(
        InteropSnapshot actual,
        SupportedBuild expected,
        IReadOnlyDictionary<string, AssemblyIdentity> expectedIdentities)
    {
        foreach (string name in ApprovedApi.Assemblies.Keys)
        {
            if (!actual.Assemblies.TryGetValue(name, out AssemblyIdentity? actualIdentity) ||
                !expectedIdentities.TryGetValue(name, out AssemblyIdentity? expectedIdentity) ||
                actualIdentity != expectedIdentity)
            {
                throw new InvalidDataException($"Interop assembly '{name}' identity does not match supported-builds.");
            }
            if (!string.Equals(actual.AssemblyMetadataSha256[name], expected.AssemblyMetadataSha256[name], StringComparison.Ordinal))
                throw new InvalidDataException($"Interop assembly '{name}' canonical metadata fingerprint does not match the mod's supported build.");
        }
        if (!string.Equals(actual.AggregateSha256, expected.AggregateSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Interop aggregate fingerprint does not match the mod's supported build.");
    }

    private static void ValidateContractDomain(GameApiContract contract)
    {
        foreach (DirectTypeContract type in contract.DirectTypes)
        {
            if (!ApprovedApi.Assemblies.ContainsKey(type.Assembly))
                throw new InvalidDataException($"Contract type names unapproved assembly '{type.Assembly}'.");
        }
        foreach (DirectMemberContract member in contract.DirectMembers)
        {
            if (!ApprovedApi.Assemblies.ContainsKey(member.Assembly))
                throw new InvalidDataException($"Contract member names unapproved assembly '{member.Assembly}'.");
        }
        foreach (EnumValueContract value in contract.EnumValues)
        {
            if (!ApprovedApi.Assemblies.ContainsKey(value.Assembly))
                throw new InvalidDataException($"Contract enum names unapproved assembly '{value.Assembly}'.");
        }
        foreach (RuntimeTargetContract target in contract.RuntimeTargets)
        {
            if (!ApprovedApi.Assemblies.ContainsKey(target.Assembly))
                throw new InvalidDataException($"Runtime target names unapproved assembly '{target.Assembly}'.");
            foreach (string modId in target.ModIds)
            {
                if (!ApprovedApi.ModAssemblies.ContainsKey(modId))
                    throw new InvalidDataException($"Runtime target names unapproved mod ID '{modId}'.");
            }
            int directMatches = contract.DirectMembers.Count(member =>
                string.Equals(member.Assembly, target.Assembly, StringComparison.Ordinal) &&
                string.Equals(member.DeclaringType, target.Type, StringComparison.Ordinal) &&
                string.Equals(member.Kind, target.Kind, StringComparison.Ordinal) &&
                string.Equals(member.Signature, target.Signature, StringComparison.Ordinal));
            if (target.Required && directMatches != 1)
                throw new InvalidDataException($"Required runtime target '{target.Assembly}:{target.Type}:{target.Signature}' must appear exactly once in the direct contract surface.");
        }
    }

    private static void WriteReportAtomically(VerificationReport report, string path)
    {
        string parent = Path.GetDirectoryName(path)!;
        string temporary = Path.Combine(parent, "." + Path.GetFileName(path) + ".tmp." + Guid.NewGuid().ToString("N"));
        try
        {
            JsonSerializerOptions options = new()
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            };
            string json = JsonSerializer.Serialize(report, options);
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
