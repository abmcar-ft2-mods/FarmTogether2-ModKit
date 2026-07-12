using FarmTogether2.ContractModel;
using Mono.Cecil;

namespace FarmTogether2.CompatibilityVerifier;

internal sealed class AssemblyCatalog : IDisposable
{
    private readonly SortedDictionary<string, AssemblyDefinition> assemblies = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, TypeDefinition>> types = new(StringComparer.Ordinal);

    internal AssemblyCatalog(string interopDirectory)
    {
        try
        {
            foreach (string name in ApprovedApi.Assemblies.Keys)
            {
                string path = Path.Combine(Path.GetFullPath(interopDirectory), name + ".dll");
                AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters
                {
                    ReadingMode = ReadingMode.Deferred,
                    ReadSymbols = false
                });
                assemblies.Add(name, assembly);
                Dictionary<string, TypeDefinition> index = new(StringComparer.Ordinal);
                foreach (TypeDefinition type in CanonicalMetadata.GetAllTypeDefinitions(assembly.MainModule))
                {
                    string typeName = CanonicalMetadata.ContractTypeName(type);
                    if (!index.TryAdd(typeName, type))
                        throw new InvalidDataException($"Interop assembly '{name}' contains duplicate type '{typeName}'.");
                }
                types.Add(name, index);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal VerificationFacts VerifyContract(
        GameApiContract contract,
        string modId,
        IReadOnlySet<string> selectedTypeKeys,
        IReadOnlySet<string> selectedMemberKeys,
        VerificationFacts prior)
    {
        int typeCount = 0;
        foreach (DirectTypeContract item in contract.DirectTypes.Where(item =>
                     selectedTypeKeys.Contains($"{item.Assembly}\u001f{item.Type}")))
        {
            TypeDefinition type = ResolveType(item.Assembly, item.Type, "contract type");
            string actualKind = TypeKind(type);
            if (!string.Equals(actualKind, item.Kind, StringComparison.Ordinal))
                throw new InvalidDataException($"Contract type '{item.Assembly}:{item.Type}' has kind '{actualKind}' instead of '{item.Kind}'.");
            string? actualBase = type.BaseType is null ? null : CanonicalMetadata.ContractTypeName(type.BaseType);
            if (!string.Equals(actualBase, item.BaseType, StringComparison.Ordinal))
                throw new InvalidDataException($"Contract type '{item.Assembly}:{item.Type}' has the wrong base type.");
            typeCount++;
        }

        int memberCount = 0;
        foreach (DirectMemberContract item in contract.DirectMembers.Where(item =>
                     selectedMemberKeys.Contains($"{item.Assembly}\u001f{item.DeclaringType}\u001f{item.Signature}")))
        {
            VerifyMember(item.Assembly, item.DeclaringType, item.Kind, item.Signature, item.IsStatic, "contract member");
            memberCount++;
        }

        int enumCount = 0;
        foreach (EnumValueContract item in contract.EnumValues.Where(item =>
                     selectedTypeKeys.Contains($"{item.Assembly}\u001f{item.EnumType}")))
        {
            TypeDefinition type = ResolveType(item.Assembly, item.EnumType, "contract enum");
            if (!type.IsEnum)
                throw new InvalidDataException($"Contract enum '{item.Assembly}:{item.EnumType}' is not an enum.");
            FieldDefinition[] matches = type.Fields.Where(field =>
                field.IsStatic && field.HasConstant && string.Equals(field.Name, item.Name, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1)
                throw new InvalidDataException($"Contract enum value '{item.Assembly}:{item.EnumType}.{item.Name}' did not resolve exactly once.");
            long value;
            try
            {
                value = Convert.ToInt64(matches[0].Constant, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException)
            {
                throw new InvalidDataException($"Contract enum value '{item.Assembly}:{item.EnumType}.{item.Name}' is not integral.", exception);
            }
            if (value != item.Value)
                throw new InvalidDataException($"Contract enum value '{item.Assembly}:{item.EnumType}.{item.Name}' has the wrong numeric value.");
            enumCount++;
        }

        int runtimeCount = 0;
        foreach (RuntimeTargetContract target in contract.RuntimeTargets.Where(target => target.ModIds.Contains(modId, StringComparer.Ordinal)))
        {
            DirectMemberContract[] directMatches = contract.DirectMembers.Where(member =>
                string.Equals(member.Assembly, target.Assembly, StringComparison.Ordinal) &&
                string.Equals(member.DeclaringType, target.Type, StringComparison.Ordinal) &&
                string.Equals(member.Kind, target.Kind, StringComparison.Ordinal) &&
                string.Equals(member.Signature, target.Signature, StringComparison.Ordinal)).ToArray();
            if (target.Required && directMatches.Length != 1)
                throw new InvalidDataException($"Required runtime target '{target.Assembly}:{target.Type}:{target.Signature}' is absent from the direct contract surface.");
            if (directMatches.Length > 1)
                throw new InvalidDataException($"Runtime target '{target.Assembly}:{target.Type}:{target.Signature}' has duplicate direct contract entries.");
            bool? expectedStatic = directMatches.Length == 1 ? directMatches[0].IsStatic : null;
            VerifyMember(target.Assembly, target.Type, target.Kind, target.Signature, expectedStatic, "runtime target");
            runtimeCount++;
        }

        return prior with
        {
            ContractTypesVerified = typeCount,
            ContractMembersVerified = memberCount,
            EnumValuesVerified = enumCount,
            RuntimeTargetsVerified = runtimeCount
        };
    }

    public void Dispose()
    {
        foreach (AssemblyDefinition assembly in assemblies.Values)
            assembly.Dispose();
        assemblies.Clear();
        types.Clear();
    }

    private TypeDefinition ResolveType(string assemblyName, string typeName, string context)
    {
        if (!ApprovedApi.Assemblies.ContainsKey(assemblyName))
            throw new InvalidDataException($"{context} names unapproved assembly '{assemblyName}'.");
        if (!types[assemblyName].TryGetValue(typeName, out TypeDefinition? type))
            throw new InvalidDataException($"{context} '{assemblyName}:{typeName}' did not resolve exactly once.");
        return type;
    }

    private void VerifyMember(
        string assemblyName,
        string declaringType,
        string kind,
        string signature,
        bool? expectedStatic,
        string context)
    {
        TypeDefinition type = ResolveType(assemblyName, declaringType, context);
        List<bool> matches = kind switch
        {
            "method" => type.Methods
                .Where(method => string.Equals(MethodSignature(method), signature, StringComparison.Ordinal))
                .Select(method => method.IsStatic)
                .ToList(),
            "field" => type.Fields
                .Where(field => string.Equals(FieldSignature(field), signature, StringComparison.Ordinal))
                .Select(field => field.IsStatic)
                .ToList(),
            "property" => type.Properties
                .Where(property => string.Equals(PropertySignature(property), signature, StringComparison.Ordinal))
                .Select(PropertyIsStatic)
                .ToList(),
            _ => throw new InvalidDataException($"{context} has unsupported member kind '{kind}'.")
        };
        if (expectedStatic.HasValue)
            matches = matches.Where(isStatic => isStatic == expectedStatic.Value).ToList();
        if (matches.Count != 1)
            throw new InvalidDataException($"{context} '{assemblyName}:{declaringType}:{signature}' resolved {matches.Count} times instead of exactly once.");
    }

    private static string TypeKind(TypeDefinition type)
    {
        if (type.IsEnum)
            return "enum";
        if (type.IsInterface)
            return "interface";
        if (type.IsValueType)
            return "struct";
        if (type.BaseType is not null && string.Equals(CanonicalMetadata.ContractTypeName(type.BaseType), "System.MulticastDelegate", StringComparison.Ordinal))
            return "delegate";
        return "class";
    }

    private static string MethodSignature(MethodReference method) =>
        $"{CanonicalMetadata.ContractTypeName(method.ReturnType)} {method.Name}({string.Join(',', method.Parameters.Select(parameter => CanonicalMetadata.ContractTypeName(parameter.ParameterType)))})";

    private static string FieldSignature(FieldReference field) =>
        $"{CanonicalMetadata.ContractTypeName(field.FieldType)} {field.Name}";

    private static string PropertySignature(PropertyDefinition property)
    {
        string suffix = property.Parameters.Count == 0
            ? string.Empty
            : $"({string.Join(',', property.Parameters.Select(parameter => CanonicalMetadata.ContractTypeName(parameter.ParameterType)))})";
        return $"{CanonicalMetadata.ContractTypeName(property.PropertyType)} {property.Name}{suffix}";
    }

    private static bool PropertyIsStatic(PropertyDefinition property)
    {
        MethodDefinition? accessor = property.GetMethod ?? property.SetMethod;
        if (accessor is null)
            throw new InvalidDataException($"Property '{property.FullName}' has no accessor.");
        if (property.GetMethod is not null && property.SetMethod is not null && property.GetMethod.IsStatic != property.SetMethod.IsStatic)
            throw new InvalidDataException($"Property '{property.FullName}' has inconsistent accessor static flags.");
        return accessor.IsStatic;
    }
}
