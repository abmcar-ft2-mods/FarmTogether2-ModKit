using FarmTogether2.ContractModel;
using Mono.Cecil;

namespace FarmTogether2.CompatibilityVerifier;

internal static class PluginReferenceVerifier
{
    internal static PluginReferenceResult Verify(
        string stubPluginPath,
        string realPluginPath,
        string expectedAssemblyName,
        GameApiContract contract)
    {
        if (!File.Exists(stubPluginPath) || !File.Exists(realPluginPath))
            throw new InvalidDataException("Stub or real plugin does not exist.");

        try
        {
            using ModuleDefinition stub = ModuleDefinition.ReadModule(stubPluginPath, NewReaderParameters());
            using ModuleDefinition real = ModuleDefinition.ReadModule(realPluginPath, NewReaderParameters());
            VerifyPluginIdentity(stub, expectedAssemblyName, "stub plugin");
            VerifyPluginIdentity(real, expectedAssemblyName, "real plugin");
            if (CanonicalMetadata.GetAssemblyIdentity(stub.Assembly.Name) != CanonicalMetadata.GetAssemblyIdentity(real.Assembly.Name))
                throw new InvalidDataException("Stub and real plugin assembly identities are not equivalent.");

            string[] stubAssemblies = AssemblyReferences(stub);
            string[] realAssemblies = AssemblyReferences(real);
            RequireEquivalent(stubAssemblies, realAssemblies, "AssemblyRef");
            VerifyApprovedAssemblyReferences(stub);
            VerifyApprovedAssemblyReferences(real);

            string[] stubTypes = TypeReferences(stub);
            string[] realTypes = TypeReferences(real);
            RequireEquivalent(stubTypes, realTypes, "TypeRef");

            string[] stubMembers = MemberReferences(stub);
            string[] realMembers = MemberReferences(real);
            RequireEquivalent(stubMembers, realMembers, "MemberRef");
            (IReadOnlySet<string> typeKeys, IReadOnlySet<string> memberKeys) = VerifyExternalReferencesAgainstContract(stub, contract);
            return new PluginReferenceResult(stubAssemblies.Length, stubTypes.Length, stubMembers.Length, typeKeys, memberKeys);
        }
        catch (BadImageFormatException exception)
        {
            throw new InvalidDataException("Stub or real plugin is not a valid managed assembly.", exception);
        }
    }

    internal static string ReadAssemblyName(string path)
    {
        try
        {
            using ModuleDefinition module = ModuleDefinition.ReadModule(path, NewReaderParameters());
            return module.Assembly.Name.Name;
        }
        catch (BadImageFormatException exception)
        {
            throw new InvalidDataException("Plugin is not a valid managed assembly.", exception);
        }
    }

    private static ReaderParameters NewReaderParameters() => new() { ReadingMode = ReadingMode.Deferred, ReadSymbols = false };

    private static void VerifyPluginIdentity(ModuleDefinition module, string expectedAssemblyName, string context)
    {
        AssemblyIdentity identity = CanonicalMetadata.GetAssemblyIdentity(module.Assembly.Name);
        if (!string.Equals(identity.Name, expectedAssemblyName, StringComparison.Ordinal) ||
            identity.Culture.Length != 0 || identity.PublicKeyToken.Length != 0)
        {
            throw new InvalidDataException($"The {context} has an unexpected assembly identity.");
        }
    }

    private static void VerifyApprovedAssemblyReferences(ModuleDefinition module)
    {
        foreach (AssemblyNameReference reference in module.AssemblyReferences)
        {
            if (!ApprovedApi.Assemblies.TryGetValue(reference.Name, out string? expectedVersion))
                continue;
            AssemblyIdentity identity = CanonicalMetadata.GetAssemblyIdentity(reference);
            if (!string.Equals(identity.Version, expectedVersion, StringComparison.Ordinal) ||
                identity.Culture.Length != 0 || identity.PublicKeyToken.Length != 0)
            {
                throw new InvalidDataException($"Plugin AssemblyRef '{reference.Name}' has an unapproved identity.");
            }
        }
    }

    private static string[] AssemblyReferences(ModuleDefinition module) => module.AssemblyReferences
        .Select(AssemblyIdentitySignature)
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static string[] TypeReferences(ModuleDefinition module) => module.GetTypeReferences()
        .Select(type => $"{ScopeIdentity(type)}\t{CanonicalMetadata.ContractTypeName(type)}")
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static string[] MemberReferences(ModuleDefinition module) => module.GetMemberReferences()
        .Select(MemberReferenceSignature)
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static string MemberReferenceSignature(MemberReference member) => member switch
    {
        MethodReference method =>
            $"method\t{ScopeIdentity(method.DeclaringType)}\t{CanonicalMetadata.ContractTypeName(method.DeclaringType)}\t{method.Name}\t{method.HasThis}\t{method.ExplicitThis}\t{(int)method.CallingConvention}\t{method.GenericParameters.Count}\t{GenericMethodArguments(method)}\t{CanonicalMetadata.ContractTypeName(method.ReturnType)}\t{string.Join(',', method.Parameters.Select(parameter => $"{CanonicalMetadata.ContractTypeName(parameter.ParameterType)}:{(int)parameter.Attributes}"))}",
        FieldReference field =>
            $"field\t{ScopeIdentity(field.DeclaringType)}\t{CanonicalMetadata.ContractTypeName(field.DeclaringType)}\t{field.Name}\t{CanonicalMetadata.ContractTypeName(field.FieldType)}",
        _ => throw new InvalidDataException($"Plugin contains unsupported MemberRef kind '{member.GetType().Name}'.")
    };

    private static string GenericMethodArguments(MethodReference method) => method is GenericInstanceMethod generic
        ? string.Join(',', generic.GenericArguments.Select(CanonicalMetadata.ContractTypeName))
        : string.Empty;

    private static string ScopeIdentity(TypeReference type)
    {
        TypeReference current = type;
        while (current is TypeSpecification specification)
            current = specification.ElementType;
        IMetadataScope scope = current.Scope;
        while (scope is TypeReference scopeType)
        {
            current = scopeType;
            while (current is TypeSpecification specification)
                current = specification.ElementType;
            scope = current.Scope;
        }
        return scope switch
        {
            AssemblyNameReference assembly => AssemblyIdentitySignature(assembly),
            ModuleDefinition module => AssemblyIdentitySignature(module.Assembly.Name),
            ModuleReference module => $"module:{module.Name}",
            _ => throw new InvalidDataException("Plugin TypeRef has an unsupported metadata scope.")
        };
    }

    private static string AssemblyIdentitySignature(AssemblyNameReference reference)
    {
        AssemblyIdentity identity = CanonicalMetadata.GetAssemblyIdentity(reference);
        return $"{identity.Name}\t{identity.Version}\t{identity.Culture}\t{identity.PublicKeyToken}";
    }

    private static void RequireEquivalent(string[] stub, string[] real, string kind)
    {
        if (!stub.SequenceEqual(real, StringComparer.Ordinal))
            throw new InvalidDataException($"Stub and real plugin {kind} sets are not equivalent.");
    }

    private static (IReadOnlySet<string> TypeKeys, IReadOnlySet<string> MemberKeys) VerifyExternalReferencesAgainstContract(
        ModuleDefinition plugin,
        GameApiContract contract)
    {
        HashSet<string> contractTypes = new(contract.DirectTypes.Select(item =>
            $"{item.Assembly}\u001f{item.Type}"), StringComparer.Ordinal);
        HashSet<string> selectedTypes = new(StringComparer.Ordinal);
        foreach (TypeReference type in plugin.GetTypeReferences())
        {
            string? assemblyName = ApprovedScopeName(type);
            if (assemblyName is null)
                continue;
            string key = $"{assemblyName}\u001f{CanonicalMetadata.ContractTypeName(type)}";
            if (!contractTypes.Contains(key))
                throw new InvalidDataException($"Stub plugin TypeRef '{assemblyName}:{CanonicalMetadata.ContractTypeName(type)}' is absent from the game API contract.");
            selectedTypes.Add(key);
        }

        HashSet<string> selectedMembers = new(StringComparer.Ordinal);
        foreach (MemberReference member in plugin.GetMemberReferences())
        {
            string? assemblyName = ApprovedScopeName(member.DeclaringType);
            if (assemblyName is null)
                continue;
            string declaringType = OpenDeclaringTypeName(member.DeclaringType);
            DirectMemberContract[] matches = member switch
            {
                FieldReference field => contract.DirectMembers.Where(item =>
                    string.Equals(item.Assembly, assemblyName, StringComparison.Ordinal) &&
                    string.Equals(item.DeclaringType, declaringType, StringComparison.Ordinal) &&
                    string.Equals(item.Kind, "field", StringComparison.Ordinal) &&
                    string.Equals(item.Signature, $"{CanonicalMetadata.ContractTypeName(field.FieldType)} {field.Name}", StringComparison.Ordinal)).ToArray(),
                MethodReference method => FindMethodContractEntries(contract, assemblyName, declaringType, method),
                _ => throw new InvalidDataException($"Stub plugin contains unsupported game MemberRef kind '{member.GetType().Name}'.")
            };
            if (matches.Length != 1)
                throw new InvalidDataException($"Stub plugin MemberRef '{assemblyName}:{declaringType}:{member.Name}' resolved to {matches.Length} contract entries instead of exactly one.");
            DirectMemberContract selected = matches[0];
            selectedMembers.Add($"{selected.Assembly}\u001f{selected.DeclaringType}\u001f{selected.Signature}");
        }
        return (selectedTypes, selectedMembers);
    }

    private static DirectMemberContract[] FindMethodContractEntries(
        GameApiContract contract,
        string assemblyName,
        string declaringType,
        MethodReference method)
    {
        bool isStatic = !method.HasThis;
        List<DirectMemberContract> candidates = [];
        if (method.Name.StartsWith("get_", StringComparison.Ordinal) || method.Name.StartsWith("set_", StringComparison.Ordinal))
        {
            bool setter = method.Name.StartsWith("set_", StringComparison.Ordinal);
            if (!setter || method.Parameters.Count != 0)
            {
                string propertyName = method.Name[4..];
                TypeReference propertyType = setter ? method.Parameters[^1].ParameterType : method.ReturnType;
                int indexCount = setter ? method.Parameters.Count - 1 : method.Parameters.Count;
                string suffix = indexCount == 0
                    ? string.Empty
                    : $"({string.Join(',', method.Parameters.Take(indexCount).Select(parameter => CanonicalMetadata.ContractTypeName(parameter.ParameterType)))})";
                string signature = $"{CanonicalMetadata.ContractTypeName(propertyType)} {propertyName}{suffix}";
                candidates.AddRange(contract.DirectMembers.Where(item =>
                    string.Equals(item.Assembly, assemblyName, StringComparison.Ordinal) &&
                    string.Equals(item.DeclaringType, declaringType, StringComparison.Ordinal) &&
                    string.Equals(item.Kind, "property", StringComparison.Ordinal) &&
                    string.Equals(item.Signature, signature, StringComparison.Ordinal) &&
                    item.IsStatic == isStatic));
            }
        }

        string methodSignature = $"{CanonicalMetadata.ContractTypeName(method.ReturnType)} {method.Name}({string.Join(',', method.Parameters.Select(parameter => CanonicalMetadata.ContractTypeName(parameter.ParameterType)))})";
        candidates.AddRange(contract.DirectMembers.Where(item =>
            string.Equals(item.Assembly, assemblyName, StringComparison.Ordinal) &&
            string.Equals(item.DeclaringType, declaringType, StringComparison.Ordinal) &&
            string.Equals(item.Kind, "method", StringComparison.Ordinal) &&
            string.Equals(item.Signature, methodSignature, StringComparison.Ordinal) &&
            item.IsStatic == isStatic));
        return candidates.Distinct().ToArray();
    }

    private static string OpenDeclaringTypeName(TypeReference type)
    {
        TypeReference current = type;
        while (current is TypeSpecification specification)
            current = specification.ElementType;
        return CanonicalMetadata.ContractTypeName(current);
    }

    private static string? ApprovedScopeName(TypeReference type)
    {
        TypeReference current = type;
        while (current is TypeSpecification specification)
            current = specification.ElementType;
        IMetadataScope scope = current.Scope;
        while (scope is TypeReference scopeType)
        {
            current = scopeType;
            while (current is TypeSpecification specification)
                current = specification.ElementType;
            scope = current.Scope;
        }
        if (scope is AssemblyNameReference assembly && ApprovedApi.Assemblies.ContainsKey(assembly.Name))
            return assembly.Name;
        return null;
    }
}
