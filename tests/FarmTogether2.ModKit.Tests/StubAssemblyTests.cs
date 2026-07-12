using FarmTogether2.ContractModel;
using Mono.Cecil;
using Xunit;

namespace FarmTogether2.ModKit.Tests;

public sealed class StubAssemblyTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));

    [Fact]
    public void EveryContractEntryExistsInStubs()
    {
        GameApiContract contract = ContractJson.Load(Path.Combine(Root, "contracts/game-api.contract.json"));
        Assert.Equal(57, contract.DirectTypes.Count);
        Assert.Equal(110, contract.DirectMembers.Count);

        Dictionary<string, ModuleDefinition> modules = ApprovedAssemblyVersions().ToDictionary(
            entry => entry.Key,
            entry => ReadStub(entry.Key),
            StringComparer.Ordinal);
        try
        {
            foreach ((string name, Version version) in ApprovedAssemblyVersions())
            {
                AssemblyNameDefinition identity = modules[name].Assembly.Name;
                Assert.Equal(name, identity.Name);
                Assert.Equal(version, identity.Version);
                Assert.Empty(identity.PublicKeyToken);
                Assert.True(string.IsNullOrEmpty(identity.Culture));
            }

            foreach (DirectTypeContract expected in contract.DirectTypes)
            {
                TypeDefinition actual = FindType(modules[expected.Assembly], expected.Type);
                Assert.Equal(expected.Kind, GetTypeKind(actual));
                Assert.Equal(expected.BaseType, NormalizeTypeName(actual.BaseType));
            }

            foreach (DirectMemberContract expected in contract.DirectMembers)
            {
                TypeDefinition declaringType = FindType(modules[expected.Assembly], expected.DeclaringType);
                IMetadataTokenProvider actual = Assert.Single(FindMembers(declaringType, expected.Kind),
                    member => GetMemberSignature(member) == expected.Signature);
                Assert.Equal(expected.IsStatic, IsStatic(actual));
            }

            foreach (EnumValueContract expected in contract.EnumValues)
            {
                TypeDefinition enumType = FindType(modules[expected.Assembly], expected.EnumType);
                FieldDefinition field = Assert.Single(enumType.Fields, candidate => candidate.Name == expected.Name);
                Assert.True(field.IsStatic);
                Assert.True(field.HasConstant);
                Assert.Equal(expected.Value, Convert.ToInt64(field.Constant, System.Globalization.CultureInfo.InvariantCulture));
            }

            foreach (RuntimeTargetContract expected in contract.RuntimeTargets.Where(target => target.Required))
            {
                Assert.Contains(contract.DirectMembers, member =>
                    member.Assembly == expected.Assembly &&
                    member.DeclaringType == expected.Type &&
                    member.Kind == expected.Kind &&
                    member.Signature == expected.Signature);
            }
        }
        finally
        {
            foreach (ModuleDefinition module in modules.Values)
                module.Dispose();
        }
    }

    [Fact]
    public void AssemblyCSharpHasExactSupportGraphAndThrowingBodies()
    {
        using ModuleDefinition module = ReadStub("Assembly-CSharp");
        IReadOnlyList<TypeDefinition> gameTypes = GetAllTypes(module)
            .Where(type => type.IsPublic || type.IsNestedPublic)
            .ToList();
        Assert.Equal(46, gameTypes.Count);

        IReadOnlyDictionary<string, string> expectedBases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Logic.FarmFeatureLevel"] = "System.Enum",
            ["Logic.ItemDefinition"] = "UnityEngine.ScriptableObject",
            ["Logic.Definition.ShopItemDefinition"] = "Logic.ItemDefinition",
            ["Logic.Definition.FarmItemDefinition"] = "Logic.Definition.ShopItemDefinition",
            ["Logic.Definition.HarvestItemDefinition"] = "Logic.Definition.FarmItemDefinition",
            ["Logic.Definition.CropDefinition"] = "Logic.Definition.HarvestItemDefinition",
            ["Logic.Definition.FlowerDefinition"] = "Logic.Definition.HarvestItemDefinition",
            ["Logic.Definition.TreeDefinition"] = "Logic.Definition.HarvestItemDefinition",
            ["Logic.Definition.Town.TownItemDefinition"] = "Logic.Definition.ShopItemDefinition",
            ["Logic.Definition.Town.TownUpgradeableItemDefinition"] = "Logic.Definition.Town.TownItemDefinition",
            ["Logic.Definition.Town.TownShopDefinition"] = "Logic.Definition.Town.TownUpgradeableItemDefinition",
            ["Logic.Farm.Buildings.BaseBuilding"] = "Il2CppSystem.Object",
            ["Logic.Farm.Buildings.Building"] = "Logic.Farm.Buildings.BaseBuilding",
            ["Logic.Farm.Buildings.RangeBuilding"] = "Logic.Farm.Buildings.Building",
            ["Logic.Farm.Buildings.FarmhandBuilding"] = "Logic.Farm.Buildings.RangeBuilding",
            ["Logic.Farm.FarmTileContents"] = "Il2CppSystem.Object",
            ["Logic.Town.Items.TownUpgradeableInstance"] = "Logic.Town.TownItemInstance",
            ["Logic.Town.Items.TownShopInstance"] = "Logic.Town.Items.TownUpgradeableInstance"
        };
        foreach ((string name, string expectedBase) in expectedBases)
            Assert.Equal(expectedBase, NormalizeTypeName(FindType(module, name).BaseType));

        foreach (TypeDefinition wrapper in gameTypes.Where(type => !type.IsValueType))
        {
            Assert.True(wrapper.IsPublic || wrapper.IsNestedPublic, $"{wrapper.FullName} is not public.");
            MethodDefinition constructor = Assert.Single(wrapper.Methods, method =>
                method.IsPublic &&
                method.IsConstructor &&
                !method.IsStatic &&
                method.Parameters.Count == 1 &&
                method.Parameters[0].ParameterType.FullName == "System.IntPtr");
            AssertThrowsNotSupported(constructor);
        }

        foreach (MethodDefinition method in gameTypes.SelectMany(type => type.Methods)
                     .Where(method => method.IsPublic && method.HasBody))
        {
            AssertThrowsNotSupported(method);
        }

        AssertEnumValue(module, "Logic.WorkFlags", "None", 0);
        AssertEnumValue(module, "Logic.WorkFlags", "UsingVehicle", 1);
        AssertEnumValue(module, "Logic.WorkFlags", "TileModPriority", 2);
        AssertEnumValue(module, "Logic.WorkType", "Recycle", 4);
        AssertEnumValue(module, "Player.PlayerState", "Idle", 0);
        Assert.Equal("System.Byte", FindType(module, "Logic.FarmFloorType").Fields.Single(field => field.Name == "value__").FieldType.FullName);
        Assert.Equal("System.Byte", FindType(module, "Logic.FarmResourceType").Fields.Single(field => field.Name == "value__").FieldType.FullName);
        Assert.Equal("System.Byte", FindType(module, "Logic.FarmTileState").Fields.Single(field => field.Name == "value__").FieldType.FullName);
    }

    [Fact]
    public void Il2CppmscorlibHasExactIdentityAndListSurface()
    {
        string path = Path.Combine(Root, "src/Stubs/Il2Cppmscorlib/bin/Release/net6.0/Il2Cppmscorlib.dll");
        using ModuleDefinition module = ModuleDefinition.ReadModule(path);
        Assert.Equal("Il2Cppmscorlib", module.Assembly.Name.Name);
        Assert.Equal(new Version(4, 0, 0, 0), module.Assembly.Name.Version);
        Assert.Empty(module.Assembly.Name.PublicKeyToken);
        TypeDefinition list = Assert.Single(module.Types, x => x.FullName == "Il2CppSystem.Collections.Generic.List`1");
        Assert.Contains(list.Properties, x => x.Name == "Count" && x.PropertyType.FullName == "System.Int32");
        Assert.Contains(list.Properties, x => x.Name == "Item" && x.Parameters.Count == 1);
        Assert.Contains(list.Methods, x => x.Name == "Add" && x.Parameters.Count == 1);
    }

    [Fact]
    public void CoreModuleHasRequiredObjectMathAndInputTypes()
    {
        using ModuleDefinition module = ModuleDefinition.ReadModule(Path.Combine(
            Root, "src/Stubs/UnityEngine.CoreModule/bin/Release/net6.0/UnityEngine.CoreModule.dll"));
        Assert.Equal(new Version(0, 0, 0, 0), module.Assembly.Name.Version);
        TypeDefinition unityObject = module.GetType("UnityEngine.Object");
        Assert.Contains(unityObject.Methods, x => x.Name == "op_Equality" && x.IsSpecialName && x.IsStatic);
        Assert.Contains(unityObject.Methods, x => x.Name == "op_Inequality" && x.IsSpecialName && x.IsStatic);
        Assert.Equal("UnityEngine.Behaviour", module.GetType("UnityEngine.MonoBehaviour").BaseType.FullName);
        MethodDefinition getComponentInParent = Assert.Single(module.GetType("UnityEngine.Component").Methods,
            x => x.Name == "GetComponentInParent" && x.HasGenericParameters && x.Parameters.Count == 0);
        Assert.Empty(Assert.Single(getComponentInParent.GenericParameters).Constraints);
        Assert.Contains(module.GetType("UnityEngine.Vector2").Properties, x => x.Name == "magnitude");
        Assert.Contains(module.GetType("UnityEngine.Texture2D").Properties, x => x.Name == "whiteTexture");
        TypeDefinition mathf = module.GetType("UnityEngine.Mathf");
        Assert.True(mathf.IsValueType);
        Assert.Equal("System.ValueType", mathf.BaseType.FullName);
        Assert.Equal(289, module.GetType("UnityEngine.KeyCode").Fields.Single(x => x.Name == "F8").Constant);
    }

    [Theory]
    [InlineData("UnityEngine.InputLegacyModule", "0.0.0.0")]
    [InlineData("UnityEngine.TextRenderingModule", "0.0.0.0")]
    [InlineData("UnityEngine.IMGUIModule", "0.0.0.0")]
    [InlineData("MilkstoneUnityExtensions", "1.0.0.0")]
    public void RemainingStubHasExactIdentity(string name, string version)
    {
        string path = Path.Combine(Root, $"src/Stubs/{name}/bin/Release/net6.0/{name}.dll");
        using ModuleDefinition module = ModuleDefinition.ReadModule(path);
        Assert.Equal(name, module.Assembly.Name.Name);
        Assert.Equal(Version.Parse(version), module.Assembly.Name.Version);
        Assert.Empty(module.Assembly.Name.PublicKeyToken);
    }

    [Fact]
    public void RemainingStubsHaveRequiredSurface()
    {
        using ModuleDefinition input = ReadStub("UnityEngine.InputLegacyModule");
        MethodDefinition getKeyDown = Assert.Single(input.GetType("UnityEngine.Input").Methods,
            x => x.Name == "GetKeyDown" && x.IsStatic && x.Parameters.Count == 1);
        Assert.Equal("System.Boolean", getKeyDown.ReturnType.FullName);
        Assert.Equal("UnityEngine.KeyCode", getKeyDown.Parameters[0].ParameterType.FullName);

        using ModuleDefinition text = ReadStub("UnityEngine.TextRenderingModule");
        Assert.Equal(1, text.GetType("UnityEngine.FontStyle").Fields.Single(x => x.Name == "Bold").Constant);
        Assert.Equal(4, text.GetType("UnityEngine.TextAnchor").Fields.Single(x => x.Name == "MiddleCenter").Constant);

        using ModuleDefinition milkstone = ReadStub("MilkstoneUnityExtensions");
        TypeDefinition int2 = milkstone.GetType("Milkstone.Utils.Int2");
        Assert.Contains(int2.Fields, x => x.Name == "x" && x.FieldType.FullName == "System.Int32");
        Assert.Contains(int2.Fields, x => x.Name == "y" && x.FieldType.FullName == "System.Int32");

        using ModuleDefinition imgui = ReadStub("UnityEngine.IMGUIModule");
        TypeDefinition gui = imgui.GetType("UnityEngine.GUI");
        Assert.Equal(2, gui.Methods.Count(x => x.Name == "Label" && x.IsStatic));
        Assert.Contains(gui.Methods, x => x.Name == "DrawTexture" && x.IsStatic && x.Parameters.Count == 2);
        Assert.Contains(gui.Properties, x => x.Name == "color" && x.GetMethod?.IsStatic == true && x.SetMethod?.IsStatic == true);
        Assert.Contains(gui.Properties, x => x.Name == "skin" && x.GetMethod?.IsStatic == true);
        Assert.Contains(imgui.GetType("UnityEngine.GUISkin").Properties, x => x.Name == "label");
        Assert.Contains(imgui.GetType("UnityEngine.GUIStyle").Properties, x => x.Name == "normal");
        Assert.Contains(imgui.GetType("UnityEngine.GUIStyleState").Properties, x => x.Name == "textColor");
    }

    private static ModuleDefinition ReadStub(string name) => ModuleDefinition.ReadModule(
        Path.Combine(Root, $"src/Stubs/{name}/bin/Release/net6.0/{name}.dll"));

    private static IReadOnlyDictionary<string, Version> ApprovedAssemblyVersions() =>
        new Dictionary<string, Version>(StringComparer.Ordinal)
        {
            ["Assembly-CSharp"] = new(0, 0, 0, 0),
            ["Il2Cppmscorlib"] = new(4, 0, 0, 0),
            ["MilkstoneUnityExtensions"] = new(1, 0, 0, 0),
            ["UnityEngine.CoreModule"] = new(0, 0, 0, 0),
            ["UnityEngine.IMGUIModule"] = new(0, 0, 0, 0),
            ["UnityEngine.InputLegacyModule"] = new(0, 0, 0, 0),
            ["UnityEngine.TextRenderingModule"] = new(0, 0, 0, 0)
        };

    private static TypeDefinition FindType(ModuleDefinition module, string contractName)
    {
        TypeDefinition? direct = module.GetType(contractName);
        if (direct is not null)
            return direct;

        TypeDefinition? nested = GetAllTypes(module).SingleOrDefault(type =>
            type.FullName.Replace('/', '.') == contractName);
        return Assert.IsType<TypeDefinition>(nested);
    }

    private static IEnumerable<TypeDefinition> GetAllTypes(ModuleDefinition module)
    {
        Stack<TypeDefinition> pending = new(module.Types.Reverse());
        while (pending.TryPop(out TypeDefinition? type))
        {
            yield return type;
            foreach (TypeDefinition nested in type.NestedTypes.Reverse())
                pending.Push(nested);
        }
    }

    private static string GetTypeKind(TypeDefinition type)
    {
        if (type.IsEnum)
            return "enum";
        if (type.IsInterface)
            return "interface";
        if (type.BaseType?.FullName is "System.MulticastDelegate" or "System.Delegate")
            return "delegate";
        if (type.IsValueType)
            return "struct";
        return "class";
    }

    private static IEnumerable<IMetadataTokenProvider> FindMembers(TypeDefinition type, string kind) => kind switch
    {
        "field" => type.Fields.Cast<IMetadataTokenProvider>(),
        "method" => type.Methods.Cast<IMetadataTokenProvider>(),
        "property" => type.Properties.Cast<IMetadataTokenProvider>(),
        _ => throw new InvalidDataException($"Unsupported contract member kind '{kind}'.")
    };

    private static string GetMemberSignature(IMetadataTokenProvider member) => member switch
    {
        FieldDefinition field => $"{NormalizeTypeName(field.FieldType)} {field.Name}",
        MethodDefinition method =>
            $"{NormalizeTypeName(method.ReturnType)} {method.Name}({string.Join(',', method.Parameters.Select(parameter => NormalizeTypeName(parameter.ParameterType)))})",
        PropertyDefinition property =>
            $"{NormalizeTypeName(property.PropertyType)} {property.Name}{GetPropertyParameterSuffix(property)}",
        _ => throw new InvalidDataException($"Unsupported metadata member '{member.GetType().FullName}'.")
    };

    private static string GetPropertyParameterSuffix(PropertyDefinition property) => property.Parameters.Count == 0
        ? string.Empty
        : $"({string.Join(',', property.Parameters.Select(parameter => NormalizeTypeName(parameter.ParameterType)))})";

    private static bool IsStatic(IMetadataTokenProvider member) => member switch
    {
        FieldDefinition field => field.IsStatic,
        MethodDefinition method => method.IsStatic,
        PropertyDefinition property => GetPropertyStaticness(property),
        _ => throw new InvalidDataException($"Unsupported metadata member '{member.GetType().FullName}'.")
    };

    private static bool GetPropertyStaticness(PropertyDefinition property)
    {
        MethodDefinition? accessor = property.GetMethod ?? property.SetMethod;
        Assert.NotNull(accessor);
        if (property.GetMethod is not null && property.SetMethod is not null)
            Assert.Equal(property.GetMethod.IsStatic, property.SetMethod.IsStatic);
        return accessor.IsStatic;
    }

    private static string? NormalizeTypeName(TypeReference? type)
    {
        if (type is null)
            return null;
        if (type is GenericInstanceType generic)
        {
            string element = generic.ElementType.FullName.Replace('/', '.');
            int arityIndex = element.LastIndexOf('`');
            if (arityIndex >= 0)
                element = element[..arityIndex];
            return $"{element}<{string.Join(',', generic.GenericArguments.Select(NormalizeTypeName))}>";
        }
        if (type is ByReferenceType byReference)
            return $"{NormalizeTypeName(byReference.ElementType)}&";
        if (type is PointerType pointer)
            return $"{NormalizeTypeName(pointer.ElementType)}*";
        if (type is ArrayType array)
            return $"{NormalizeTypeName(array.ElementType)}[{new string(',', array.Rank - 1)}]";
        if (type is GenericParameter genericParameter)
        {
            string prefix = genericParameter.Type == GenericParameterType.Method ? "!!" : "!";
            return $"{prefix}{genericParameter.Position}";
        }
        return type.FullName.Replace('/', '.');
    }

    private static void AssertEnumValue(ModuleDefinition module, string typeName, string fieldName, long expected)
    {
        FieldDefinition field = Assert.Single(FindType(module, typeName).Fields, candidate => candidate.Name == fieldName);
        Assert.True(field.HasConstant);
        Assert.Equal(expected, Convert.ToInt64(field.Constant, System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void AssertThrowsNotSupported(MethodDefinition method)
    {
        Assert.Contains(method.Body.Instructions, instruction =>
            instruction.OpCode == Mono.Cecil.Cil.OpCodes.Newobj &&
            instruction.Operand is MethodReference constructor &&
            constructor.DeclaringType.FullName == "System.NotSupportedException");
        Assert.Contains(method.Body.Instructions, instruction => instruction.OpCode == Mono.Cecil.Cil.OpCodes.Throw);
        Assert.DoesNotContain(method.Body.Instructions, instruction => instruction.OpCode == Mono.Cecil.Cil.OpCodes.Ret);
    }
}
