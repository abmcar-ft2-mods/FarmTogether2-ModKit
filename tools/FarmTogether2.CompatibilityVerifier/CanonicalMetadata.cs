using Mono.Cecil;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace FarmTogether2.CompatibilityVerifier;

internal static class CanonicalMetadata
{
    internal static InteropSnapshot ReadInteropDirectory(string directory)
    {
        string root = Path.GetFullPath(directory);
        if (!Directory.Exists(root))
            throw new InvalidDataException("Interop directory does not exist.");

        SortedDictionary<string, string> hashes = new(StringComparer.Ordinal);
        SortedDictionary<string, AssemblyIdentity> identities = new(StringComparer.Ordinal);
        foreach ((string name, string version) in ApprovedApi.Assemblies)
        {
            string path = Path.Combine(root, name + ".dll");
            if (!File.Exists(path))
                throw new InvalidDataException($"Interop assembly '{name}' is missing.");

            try
            {
                using AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters
                {
                    ReadingMode = ReadingMode.Deferred,
                    ReadSymbols = false
                });
                AssemblyIdentity identity = GetAssemblyIdentity(assembly.Name);
                if (!string.Equals(identity.Name, name, StringComparison.Ordinal) ||
                    !string.Equals(identity.Version, version, StringComparison.Ordinal) ||
                    identity.Culture.Length != 0 || identity.PublicKeyToken.Length != 0)
                {
                    throw new InvalidDataException($"Interop assembly '{name}' has an unapproved identity.");
                }
                if (!assembly.MainModule.AssemblyReferences.Any(reference => string.Equals(reference.Name, "Il2CppInterop.Runtime", StringComparison.Ordinal)))
                    throw new InvalidDataException($"Interop assembly '{name}' is not a LocalInterop assembly.");
                identities.Add(name, identity);
                hashes.Add(name, ComputePublicMetadataFingerprint(assembly));
            }
            catch (BadImageFormatException exception)
            {
                throw new InvalidDataException($"Interop assembly '{name}' is not a valid managed assembly.", exception);
            }
        }

        return new InteropSnapshot(ComputeAggregate(hashes), hashes, identities);
    }

    internal static string ComputeAggregate(IReadOnlyDictionary<string, string> hashes)
    {
        string text = string.Join('\n', ApprovedApi.Assemblies.Keys.Select(name => $"{name}\t{hashes[name]}"));
        return LowerSha256(Encoding.UTF8.GetBytes(text));
    }

    internal static string ComputePublicMetadataFingerprint(AssemblyDefinition assembly)
    {
        List<string> lines = [];
        AssemblyIdentity identity = GetAssemblyIdentity(assembly.Name);
        lines.Add($"assembly\t{identity.Name}\t{identity.Version}\t{identity.Culture}\t{identity.PublicKeyToken}");

        foreach (TypeDefinition type in GetAllTypeDefinitions(assembly.MainModule))
        {
            if (!IsPubliclyVisible(type))
                continue;

            string kind = type.IsEnum ? "enum" : type.IsInterface ? "interface" : type.IsValueType ? "struct" : "class";
            string baseType = type.BaseType is null ? string.Empty : ContractTypeName(type.BaseType);
            string interfaces = string.Join(',', type.Interfaces.Select(item => ContractTypeName(item.InterfaceType)).Order(StringComparer.Ordinal));
            lines.Add($"type\t{kind}\t{ContractTypeName(type)}\t{baseType}\t{interfaces}\t{(int)type.Attributes}\t{type.PackingSize}\t{type.ClassSize}\t{GenericParameterListSignature(type)}");

            foreach (FieldDefinition field in type.Fields.Where(field => field.IsPublic))
            {
                string constant = field.HasConstant ? CanonicalConstant(field.Constant) : string.Empty;
                lines.Add($"field\t{ContractTypeName(type)}\t{(int)field.Attributes}\t{ContractTypeName(field.FieldType)}\t{field.Name}\t{constant}");
            }
            foreach (MethodDefinition method in type.Methods.Where(method => method.IsPublic))
            {
                string returnDefault = method.MethodReturnType.HasConstant ? CanonicalConstant(method.MethodReturnType.Constant) : string.Empty;
                string parameters = string.Join(',', method.Parameters.Select(parameter =>
                {
                    string defaultValue = parameter.HasConstant ? CanonicalConstant(parameter.Constant) : string.Empty;
                    return $"{ContractTypeName(parameter.ParameterType)}:{(int)parameter.Attributes}:{defaultValue}";
                }));
                lines.Add($"method\t{ContractTypeName(type)}\t{(int)method.Attributes}\t{(int)method.ImplAttributes}\t{ContractTypeName(method.ReturnType)}:{(int)method.MethodReturnType.Attributes}:{returnDefault}\t{method.Name}\t{GenericParameterListSignature(method)}\t{parameters}");
            }
            foreach (PropertyDefinition property in type.Properties)
            {
                MethodDefinition[] accessors = new[] { property.GetMethod, property.SetMethod }
                    .Where(method => method is not null && method.IsPublic)
                    .Cast<MethodDefinition>()
                    .ToArray();
                if (accessors.Length == 0)
                    continue;
                string parameters = string.Join(',', property.Parameters.Select(parameter => ContractTypeName(parameter.ParameterType)));
                bool isStatic = accessors.Any(method => method.IsStatic);
                lines.Add($"property\t{ContractTypeName(type)}\t{isStatic}\t{(int)property.Attributes}\t{ContractTypeName(property.PropertyType)}\t{property.Name}\t{parameters}");
            }
            foreach (EventDefinition eventDefinition in type.Events)
            {
                MethodDefinition[] accessors = new[] { eventDefinition.AddMethod, eventDefinition.RemoveMethod }
                    .Where(method => method is not null && method.IsPublic)
                    .Cast<MethodDefinition>()
                    .ToArray();
                if (accessors.Length == 0)
                    continue;
                bool isStatic = accessors.Any(method => method.IsStatic);
                lines.Add($"event\t{ContractTypeName(type)}\t{isStatic}\t{(int)eventDefinition.Attributes}\t{ContractTypeName(eventDefinition.EventType)}\t{eventDefinition.Name}");
            }
        }

        lines.Sort(StringComparer.Ordinal);
        return LowerSha256(Encoding.UTF8.GetBytes(string.Join('\n', lines)));
    }

    internal static AssemblyIdentity GetAssemblyIdentity(AssemblyNameReference name)
    {
        string culture = string.IsNullOrEmpty(name.Culture) ? string.Empty : name.Culture;
        string token = name.PublicKeyToken is null || name.PublicKeyToken.Length == 0
            ? string.Empty
            : Convert.ToHexString(name.PublicKeyToken).ToLowerInvariant();
        return new AssemblyIdentity(name.Name, name.Version.ToString(), culture, token);
    }

    internal static string ContractTypeName(TypeReference type)
    {
        switch (type)
        {
            case GenericInstanceType generic:
                string element = generic.ElementType.FullName.Replace('/', '.').Replace('+', '.');
                element = Regex.Replace(element, @"`\d+", string.Empty, RegexOptions.CultureInvariant);
                return $"{element}<{string.Join(',', generic.GenericArguments.Select(ContractTypeName))}>";
            case ByReferenceType byReference:
                return ContractTypeName(byReference.ElementType) + "&";
            case PointerType pointer:
                return ContractTypeName(pointer.ElementType) + "*";
            case ArrayType array:
                return ContractTypeName(array.ElementType) + "[" + new string(',', Math.Max(0, array.Rank - 1)) + "]";
            default:
                return type.FullName.Replace('/', '.').Replace('+', '.');
        }
    }

    internal static IReadOnlyList<TypeDefinition> GetAllTypeDefinitions(ModuleDefinition module)
    {
        List<TypeDefinition> result = [];
        Stack<TypeDefinition> pending = new();
        foreach (TypeDefinition type in module.Types)
        {
            if (!string.Equals(type.Name, "<Module>", StringComparison.Ordinal))
                pending.Push(type);
        }
        while (pending.Count != 0)
        {
            TypeDefinition type = pending.Pop();
            result.Add(type);
            foreach (TypeDefinition nested in type.NestedTypes)
                pending.Push(nested);
        }
        return result;
    }

    internal static bool IsPubliclyVisible(TypeDefinition type) =>
        type.DeclaringType is null ? type.IsPublic : type.IsNestedPublic && IsPubliclyVisible(type.DeclaringType);

    internal static string LowerSha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static string LowerFileSha256(string path)
    {
        if (!File.Exists(path))
            throw new InvalidDataException("Verifier input file does not exist.");
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string GenericParameterListSignature(IGenericParameterProvider provider) =>
        string.Join(';', provider.GenericParameters.Select(GenericParameterSignature));

    private static string GenericParameterSignature(GenericParameter parameter)
    {
        string constraints = string.Join(',', parameter.Constraints
            .Select(constraint => ContractTypeName(constraint.ConstraintType))
            .Order(StringComparer.Ordinal));
        return $"{parameter.Position}:{parameter.Name}:{(int)parameter.Attributes}:{constraints}";
    }

    private static string CanonicalConstant(object? value)
    {
        if (value is null)
            return "null";
        return value switch
        {
            string text => "string:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)),
            char character => $"char:{(int)character}",
            bool boolean => $"bool:{boolean.ToString().ToLowerInvariant()}",
            float single => "single:" + BitConverter.SingleToInt32Bits(single).ToString("x8", CultureInfo.InvariantCulture),
            double number => "double:" + BitConverter.DoubleToInt64Bits(number).ToString("x16", CultureInfo.InvariantCulture),
            _ => $"{value.GetType().FullName}:{Convert.ToString(value, CultureInfo.InvariantCulture)}"
        };
    }
}
