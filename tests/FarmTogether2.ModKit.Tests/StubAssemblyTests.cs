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
        Assert.Equal(63, contract.DirectTypes.Count);
        Assert.Equal(137, contract.DirectMembers.Count);

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
                Assert.True(actual.IsPublic || actual.IsNestedPublic,
                    $"Contract type '{expected.Assembly}:{expected.Type}' is not public.");
                Assert.Equal(expected.Kind, GetTypeKind(actual));
                Assert.Equal(expected.BaseType, NormalizeTypeName(actual.BaseType));
            }

            foreach (DirectMemberContract expected in contract.DirectMembers)
            {
                TypeDefinition declaringType = FindType(modules[expected.Assembly], expected.DeclaringType);
                IMetadataTokenProvider actual = Assert.Single(FindMembers(declaringType, expected.Kind),
                    member => GetMemberSignature(member) == expected.Signature);
                AssertPublicContractMember(actual, expected);
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
        Assert.Equal(52, gameTypes.Count);

        IReadOnlyDictionary<string, string> expectedBases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Core.StageParameters"] = "Il2CppSystem.Object",
            ["Logic.ActionPerformedType"] = "System.Enum",
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
            ["Logic.Farm.FarmData.ActionPerformedHandler"] = "Il2CppSystem.MulticastDelegate",
            ["Logic.Farm.FarmTileContents"] = "Il2CppSystem.Object",
            ["Logic.Town.Items.TownUpgradeableInstance"] = "Logic.Town.TownItemInstance",
            ["Logic.Town.Items.TownShopInstance"] = "Logic.Town.Items.TownUpgradeableInstance",
            ["SelectedTiles"] = "WidgetOwner",
            ["SelectedTilesTractorWork"] = "Il2CppSystem.Object",
            ["WidgetOwner"] = "UnityEngine.MonoBehaviour"
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
        AssertEnumValue(module, "Logic.ActionPerformedType", "Purchase", 0);
        AssertEnumValue(module, "Logic.ActionPerformedType", "Harvest", 1);
        AssertEnumValue(module, "Logic.ActionPerformedType", "Refill", 2);
        AssertEnumValue(module, "Logic.ActionPerformedType", "Recycle", 3);
        AssertEnumValue(module, "Logic.ActionPerformedType", "Exchange", 4);
        AssertEnumValue(module, "Logic.ActionPerformedType", "Terraform", 5);
        Assert.True(FindType(module, "Logic.FailedAction").IsSealed);
        TypeDefinition farmTileId = FindType(module, "Logic.FarmTileId");
        Assert.True(Assert.Single(farmTileId.Fields, field => field.Name == "_TileX").IsInitOnly);
        Assert.True(Assert.Single(farmTileId.Fields, field => field.Name == "_TileY").IsInitOnly);
        Assert.Equal("System.Byte", FindType(module, "Logic.ActionPerformedType").Fields.Single(field => field.Name == "value__").FieldType.FullName);
        Assert.Equal("System.Byte", FindType(module, "Logic.FarmFloorType").Fields.Single(field => field.Name == "value__").FieldType.FullName);
        Assert.Equal("System.Byte", FindType(module, "Logic.FarmResourceType").Fields.Single(field => field.Name == "value__").FieldType.FullName);
        Assert.Equal("System.Byte", FindType(module, "Logic.FarmTileState").Fields.Single(field => field.Name == "value__").FieldType.FullName);
    }

    [Fact]
    public void AssemblyCSharpHasExactFinalModHostedSurface()
    {
        using ModuleDefinition module = ReadStub("Assembly-CSharp");

        TypeDefinition stageParameters = FindType(module, "Core.StageParameters");
        Assert.True(stageParameters.IsPublic);
        Assert.True(stageParameters.IsAbstract);
        // C# cannot preserve both the IL2CPP Object base and abstract+sealed metadata.
        Assert.False(stageParameters.IsSealed);
        Assert.Equal("Il2CppSystem.Object", NormalizeTypeName(stageParameters.BaseType));
        PropertyDefinition isOnline = Assert.Single(stageParameters.Properties, property =>
            GetMemberSignature(property) == "System.Boolean IsOnline");
        Assert.True(GetPropertyStaticness(isOnline));
        Assert.NotNull(isOnline.GetMethod);
        Assert.NotNull(isOnline.SetMethod);

        TypeDefinition farmData = FindType(module, "Logic.Farm.FarmData");
        TypeDefinition farmMoney = FindType(module, "Logic.FarmMoney");
        AssertMethod(
            farmMoney,
            "Logic.FarmMoney op_Multiply(Logic.FarmMoney,System.Single)",
            isStatic: true,
            isSpecialName: true);
        TypeDefinition handler = FindType(module, "Logic.Farm.FarmData.ActionPerformedHandler");
        Assert.True(handler.IsNestedPublic);
        Assert.True(handler.IsSealed);
        Assert.Equal("Il2CppSystem.MulticastDelegate", NormalizeTypeName(handler.BaseType));
        AssertMethod(handler, "System.Void .ctor(Il2CppSystem.Object,System.IntPtr)", isStatic: false);
        AssertMethod(handler, "System.Void .ctor(System.IntPtr)", isStatic: false);
        MethodDefinition handlerInvoke = AssertMethod(
            handler,
            "System.Void Invoke(UnityEngine.Vector3,Logic.ActionPerformedType,System.UInt64,Logic.FarmMoney,Il2CppSystem.Collections.Generic.List<Logic.FarmResource>,System.Boolean)",
            isStatic: false);
        // A sealed C# type cannot declare the virtual NewSlot method used by the generated interop wrapper.
        Assert.False(handlerInvoke.IsVirtual);
        AssertMethod(
            handler,
            "Logic.Farm.FarmData.ActionPerformedHandler op_Implicit(System.Action<UnityEngine.Vector3,Logic.ActionPerformedType,System.UInt64,Logic.FarmMoney,Il2CppSystem.Collections.Generic.List<Logic.FarmResource>,System.Boolean>)",
            isStatic: true,
            isSpecialName: true);
        AssertMethod(
            handler,
            "Logic.Farm.FarmData.ActionPerformedHandler op_Addition(Logic.Farm.FarmData.ActionPerformedHandler,Logic.Farm.FarmData.ActionPerformedHandler)",
            isStatic: true,
            isSpecialName: true);
        AssertMethod(
            handler,
            "Logic.Farm.FarmData.ActionPerformedHandler op_Subtraction(Logic.Farm.FarmData.ActionPerformedHandler,Logic.Farm.FarmData.ActionPerformedHandler)",
            isStatic: true,
            isSpecialName: true);

        PropertyDefinition townAction = Assert.Single(farmData.Properties, property =>
            GetMemberSignature(property) == "Logic.Farm.FarmData.ActionPerformedHandler OnTownActionPerformed");
        Assert.False(GetPropertyStaticness(townAction));
        Assert.NotNull(townAction.GetMethod);
        Assert.NotNull(townAction.SetMethod);
        Assert.DoesNotContain(farmData.Events, candidate => candidate.Name == "OnTownActionPerformed");

        TypeDefinition baseBuilding = FindType(module, "Logic.Farm.Buildings.BaseBuilding");
        PropertyDefinition id = Assert.Single(baseBuilding.Properties, property =>
            GetMemberSignature(property) == "System.UInt32 Id");
        Assert.False(GetPropertyStaticness(id));
        Assert.NotNull(id.GetMethod);
        AssertVirtualSlot(Assert.IsType<MethodDefinition>(id.GetMethod), newSlot: true);
        Assert.Null(id.SetMethod);

        AssertVirtualSlot(AssertMethod(baseBuilding, "System.Void Tick(System.UInt32)", isStatic: false), newSlot: true);
        AssertVirtualSlot(AssertMethod(baseBuilding, "System.Void Removed()", isStatic: false), newSlot: true);

        TypeDefinition rangeBuilding = FindType(module, "Logic.Farm.Buildings.RangeBuilding");
        AssertVirtualSlot(AssertMethod(rangeBuilding, "Milkstone.Utils.Int2 get_Range()", isStatic: false), newSlot: true);

        TypeDefinition farmhandBuilding = FindType(module, "Logic.Farm.Buildings.FarmhandBuilding");
        AssertVirtualSlot(AssertMethod(farmhandBuilding, "System.Void Tick(System.UInt32)", isStatic: false), newSlot: false);
        AssertVirtualSlot(AssertMethod(farmhandBuilding, "System.Void Removed()", isStatic: false), newSlot: false);
        AssertVirtualSlot(AssertMethod(farmhandBuilding, "Milkstone.Utils.Int2 get_Range()", isStatic: false), newSlot: false);

        TypeDefinition player = FindType(module, "Player");
        TypeDefinition localPlayer = FindType(module, "LocalPlayer");
        Assert.True(localPlayer.IsSealed);
        string didPerformWork = "System.Boolean DidPerformWork(Logic.WorkType,Logic.Definition.FarmItemDefinition,Il2CppSystem.Collections.Generic.List<Logic.Farm.FarmTile>,System.Int32,Logic.WorkFlags,System.Double)";
        foreach (string signature in new[]
                 {
                     "System.Void Update()",
                     "System.Void updateCharacterControllerParameters()",
                     didPerformWork
                 })
        {
            AssertVirtualSlot(AssertMethod(player, signature, isStatic: false), newSlot: true);
            AssertVirtualSlot(AssertMethod(localPlayer, signature, isStatic: false), newSlot: false);
        }

        PropertyDefinition isRemote = Assert.Single(player.Properties, property =>
            GetMemberSignature(property) == "System.Boolean IsRemote");
        AssertVirtualSlot(Assert.IsType<MethodDefinition>(isRemote.GetMethod), newSlot: true);

        TypeDefinition controller = FindType(module, "MilkCharacterController");
        AssertVirtualSlot(AssertMethod(
            controller,
            "System.Void KinematicCharacterController_ICharacterController_UpdateVelocity(UnityEngine.Vector3&,System.Single)",
            isStatic: false), newSlot: true);

        TypeDefinition stageScript = FindType(module, "StageScript");
        PropertyDefinition isLoaded = Assert.Single(stageScript.Properties, property =>
            GetMemberSignature(property) == "System.Boolean IsLoaded");
        AssertVirtualSlot(Assert.IsType<MethodDefinition>(isLoaded.GetMethod), newSlot: true);

        TypeDefinition tractorWork = FindType(module, "SelectedTilesTractorWork");
        AssertVirtualSlot(AssertMethod(
            tractorWork,
            "System.Void Apply(SelectedTiles,LocalPlayer)",
            isStatic: false), newSlot: true);
        AssertVirtualSlot(AssertMethod(
            tractorWork,
            "System.Boolean Check(SelectedTiles,LocalPlayer)",
            isStatic: false), newSlot: true);
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
        PropertyDefinition count = Assert.Single(list.Properties, x => x.Name == "Count" && x.PropertyType.FullName == "System.Int32");
        AssertVirtualSlot(Assert.IsType<MethodDefinition>(count.GetMethod), newSlot: true);
        PropertyDefinition item = Assert.Single(list.Properties, x => x.Name == "Item" && x.Parameters.Count == 1);
        AssertVirtualSlot(Assert.IsType<MethodDefinition>(item.GetMethod), newSlot: true);
        MethodDefinition add = Assert.Single(list.Methods, x => x.Name == "Add" && x.Parameters.Count == 1);
        AssertVirtualSlot(add, newSlot: true);
        MethodDefinition removeAt = AssertMethod(list, "System.Void RemoveAt(System.Int32)", isStatic: false);
        AssertVirtualSlot(removeAt, newSlot: true);

        TypeDefinition @delegate = Assert.Single(module.Types, type => type.FullName == "Il2CppSystem.Delegate");
        Assert.Equal("Il2CppSystem.Object", NormalizeTypeName(@delegate.BaseType));
        AssertMethod(@delegate, "System.Void .ctor(System.IntPtr)", isStatic: false);
        AssertMethod(
            @delegate,
            "System.Boolean op_Equality(Il2CppSystem.Delegate,Il2CppSystem.Delegate)",
            isStatic: true,
            isSpecialName: true);
        AssertMethod(
            @delegate,
            "System.Boolean op_Inequality(Il2CppSystem.Delegate,Il2CppSystem.Delegate)",
            isStatic: true,
            isSpecialName: true);

        TypeDefinition multicastDelegate = Assert.Single(module.Types, type => type.FullName == "Il2CppSystem.MulticastDelegate");
        Assert.Equal("Il2CppSystem.Delegate", NormalizeTypeName(multicastDelegate.BaseType));
        AssertMethod(multicastDelegate, "System.Void .ctor(System.IntPtr)", isStatic: false);
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
        AssertMethod(unityObject, "System.Void Destroy(UnityEngine.Object)", isStatic: true);
        Assert.Equal(1, unityObject.Methods.Count(method => method.Name == "Destroy"));

        TypeDefinition behaviour = module.GetType("UnityEngine.Behaviour");
        PropertyDefinition enabled = Assert.Single(behaviour.Properties, property =>
            GetMemberSignature(property) == "System.Boolean enabled");
        Assert.False(GetPropertyStaticness(enabled));
        Assert.NotNull(enabled.GetMethod);
        Assert.NotNull(enabled.SetMethod);
        Assert.Equal("UnityEngine.Behaviour", module.GetType("UnityEngine.MonoBehaviour").BaseType.FullName);
        MethodDefinition getComponentInParent = Assert.Single(module.GetType("UnityEngine.Component").Methods,
            x => x.Name == "GetComponentInParent" && x.HasGenericParameters && x.Parameters.Count == 0);
        Assert.Empty(Assert.Single(getComponentInParent.GenericParameters).Constraints);
        Assert.Contains(module.GetType("UnityEngine.Vector2").Properties, x => x.Name == "magnitude");
        TypeDefinition texture2D = module.GetType("UnityEngine.Texture2D");
        Assert.True(texture2D.IsSealed);
        Assert.Contains(texture2D.Properties, x => x.Name == "whiteTexture");
        Assert.True(module.GetType("UnityEngine.Screen").IsSealed);
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
        TypeDefinition guiSkin = imgui.GetType("UnityEngine.GUISkin");
        TypeDefinition guiStyle = imgui.GetType("UnityEngine.GUIStyle");
        TypeDefinition guiStyleState = imgui.GetType("UnityEngine.GUIStyleState");
        Assert.True(guiSkin.IsSealed);
        Assert.True(guiStyle.IsSealed);
        Assert.True(guiStyleState.IsSealed);
        Assert.Contains(guiSkin.Properties, x => x.Name == "label");
        Assert.Contains(guiStyle.Properties, x => x.Name == "normal");
        Assert.Contains(guiStyleState.Properties, x => x.Name == "textColor");
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

    private static void AssertPublicContractMember(
        IMetadataTokenProvider member,
        DirectMemberContract expected)
    {
        string context = $"Contract member '{expected.Assembly}:{expected.DeclaringType}:{expected.Signature}'";
        switch (member)
        {
            case FieldDefinition field:
                Assert.True(field.IsPublic, $"{context} is not public.");
                break;
            case MethodDefinition method:
                Assert.True(method.IsPublic, $"{context} is not public.");
                break;
            case PropertyDefinition property:
                MethodDefinition[] accessors = new[] { property.GetMethod, property.SetMethod }
                    .OfType<MethodDefinition>()
                    .ToArray();
                Assert.NotEmpty(accessors);
                Assert.All(accessors, accessor =>
                    Assert.True(accessor.IsPublic, $"{context} accessor '{accessor.Name}' is not public."));
                break;
            default:
                throw new InvalidDataException($"Unsupported metadata member '{member.GetType().FullName}'.");
        }
    }

    private static bool GetPropertyStaticness(PropertyDefinition property)
    {
        MethodDefinition accessor = property.GetMethod ?? property.SetMethod ??
            throw new InvalidDataException($"Property '{property.FullName}' has no accessor.");
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

    private static void AssertVirtualSlot(MethodDefinition method, bool newSlot)
    {
        Assert.True(method.IsVirtual, $"{method.FullName} is not virtual.");
        Assert.False(method.IsFinal, $"{method.FullName} is final.");
        Assert.Equal(newSlot, method.IsNewSlot);
    }

    private static MethodDefinition AssertMethod(
        TypeDefinition type,
        string signature,
        bool isStatic,
        bool? isSpecialName = null)
    {
        MethodDefinition method = Assert.Single(type.Methods, candidate => GetMemberSignature(candidate) == signature);
        Assert.Equal(isStatic, method.IsStatic);
        if (isSpecialName.HasValue)
            Assert.Equal(isSpecialName.Value, method.IsSpecialName);
        return method;
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
