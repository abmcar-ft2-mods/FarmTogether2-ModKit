using Mono.Cecil;
using System.Globalization;

namespace FarmTogether2.CompatibilityVerifier;

internal static class MetadataSpecificationSignatures
{
    private const uint MaximumMetadataRid = 0x00ff_ffff;

    internal static string[] TypeSpecifications(ModuleDefinition module) =>
        EnumerateTable(module, TokenType.TypeSpec, "TypeSpec")
            .Select(provider => provider is TypeReference type
                ? TypeSignature(type)
                : throw new InvalidDataException($"TypeSpec token resolved to unsupported metadata '{provider.GetType().Name}'."))
            .Order(StringComparer.Ordinal)
            .ToArray();

    internal static string[] MethodSpecifications(ModuleDefinition module) =>
        EnumerateTable(module, TokenType.MethodSpec, "MethodSpec")
            .Select(provider => provider is GenericInstanceMethod method
                ? MethodSpecificationSignature(method)
                : throw new InvalidDataException($"MethodSpec token resolved to unsupported metadata '{provider.GetType().Name}'."))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static IEnumerable<IMetadataTokenProvider> EnumerateTable(
        ModuleDefinition module,
        TokenType tokenType,
        string tableName)
    {
        // ECMA-335 table row IDs are contiguous, so the first missing RID ends the table.
        for (uint rid = 1; rid <= MaximumMetadataRid; rid++)
        {
            IMetadataTokenProvider? provider = module.LookupToken(new MetadataToken(tokenType, rid));
            if (provider is null)
                yield break;
            if (provider.MetadataToken.TokenType != tokenType)
                throw new InvalidDataException($"{tableName} token resolved to the wrong metadata table.");
            yield return provider;
        }
    }

    private static string MethodSpecificationSignature(GenericInstanceMethod method) => Composite(
        "method-spec",
        MethodReferenceSignature(method.ElementMethod),
        Sequence(method.GenericArguments.Select(TypeSignature)));

    private static string MethodReferenceSignature(MethodReference method) => Composite(
        "method",
        TypeSignature(method.DeclaringType),
        method.Name,
        BooleanSignature(method.HasThis),
        BooleanSignature(method.ExplicitThis),
        IntegerSignature((int)method.CallingConvention),
        IntegerSignature(method.GenericParameters.Count),
        TypeSignature(method.ReturnType),
        Sequence(method.Parameters.Select(parameter => TypeSignature(parameter.ParameterType))));

    private static string TypeSignature(TypeReference type) => type switch
    {
        GenericInstanceType generic => Composite(
            "generic-instance",
            TypeSignature(generic.ElementType),
            BooleanSignature(generic.IsValueType),
            Sequence(generic.GenericArguments.Select(TypeSignature))),
        ArrayType array => Composite(
            "array",
            TypeSignature(array.ElementType),
            BooleanSignature(array.IsVector),
            Sequence(array.Dimensions.Select(ArrayDimensionSignature))),
        ByReferenceType reference => Composite("by-reference", TypeSignature(reference.ElementType)),
        PointerType pointer => Composite("pointer", TypeSignature(pointer.ElementType)),
        PinnedType pinned => Composite("pinned", TypeSignature(pinned.ElementType)),
        SentinelType sentinel => Composite("sentinel", TypeSignature(sentinel.ElementType)),
        RequiredModifierType required => Composite(
            "required-modifier",
            TypeSignature(required.ModifierType),
            TypeSignature(required.ElementType)),
        OptionalModifierType optional => Composite(
            "optional-modifier",
            TypeSignature(optional.ModifierType),
            TypeSignature(optional.ElementType)),
        FunctionPointerType function => Composite(
            "function-pointer",
            BooleanSignature(function.HasThis),
            BooleanSignature(function.ExplicitThis),
            IntegerSignature((int)function.CallingConvention),
            IntegerSignature(function.GenericParameters.Count),
            TypeSignature(function.ReturnType),
            Sequence(function.Parameters.Select(parameter => TypeSignature(parameter.ParameterType)))),
        GenericParameter parameter => Composite(
            parameter.Type == GenericParameterType.Method ? "method-parameter" : "type-parameter",
            IntegerSignature(parameter.Position)),
        TypeSpecification specification => throw new InvalidDataException(
            $"Plugin contains unsupported TypeSpec kind '{specification.GetType().Name}'."),
        _ => Composite(
            "named-type",
            ScopeIdentity(type),
            type.FullName,
            BooleanSignature(type.IsValueType))
    };

    private static string ArrayDimensionSignature(ArrayDimension dimension) => Composite(
        "dimension",
        NullableIntegerSignature(dimension.LowerBound),
        NullableIntegerSignature(dimension.UpperBound));

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
            ModuleReference module => Composite("module", module.Name),
            _ => throw new InvalidDataException("Plugin TypeSpec has an unsupported metadata scope.")
        };
    }

    private static string AssemblyIdentitySignature(AssemblyNameReference reference)
    {
        AssemblyIdentity identity = CanonicalMetadata.GetAssemblyIdentity(reference);
        return Composite("assembly", identity.Name, identity.Version, identity.Culture, identity.PublicKeyToken);
    }

    private static string Sequence(IEnumerable<string> values) => Composite("sequence", values.ToArray());

    private static string Composite(string kind, params string[] values)
    {
        string[] parts = new string[values.Length + 1];
        parts[0] = kind;
        values.CopyTo(parts, 1);
        return string.Concat(parts.Select(LengthPrefixed));
    }

    private static string LengthPrefixed(string value) => $"{value.Length.ToString(CultureInfo.InvariantCulture)}:{value}";

    private static string BooleanSignature(bool value) => value ? "1" : "0";

    private static string IntegerSignature(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string NullableIntegerSignature(int? value) =>
        value.HasValue ? IntegerSignature(value.Value) : "null";
}
