using FarmTogether2.CompatibilityVerifier;
using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Xunit;
using Verifier = FarmTogether2.CompatibilityVerifier.CompatibilityVerifier;

namespace FarmTogether2.ModKit.Tests;

public sealed class CompatibilityVerifierTests : IClassFixture<CompatibilityVerifierFixture>
{
    private readonly CompatibilityVerifierFixture fixture;

    public CompatibilityVerifierTests(CompatibilityVerifierFixture fixture) => this.fixture = fixture;

    [Fact]
    public void ValidSyntheticInputsProduceAtomicFactOnlyReport()
    {
        using CompatibilityVerifierCase testCase = fixture.CreateCase();

        VerificationReport report = Verifier.Verify(testCase.Options);

        Assert.Equal("90000001", report.SteamBuildId);
        Assert.Equal(7, report.Facts.AssemblyIdentitiesVerified);
        Assert.Equal(2, report.Facts.ContractTypesVerified);
        Assert.Equal(4, report.Facts.ContractMembersVerified);
        Assert.Equal(1, report.Facts.EnumValuesVerified);
        Assert.Equal(1, report.Facts.RuntimeTargetsVerified);
        string json = File.ReadAllText(testCase.Report, Encoding.UTF8);
        Assert.DoesNotContain(testCase.Root, json, StringComparison.Ordinal);
        Assert.DoesNotContain("Synthetic.PublicApi", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Use(System.Int32)", json, StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(9, document.RootElement.EnumerateObject().Count());
        Assert.Equal(9, document.RootElement.GetProperty("facts").EnumerateObject().Count());
        Assert.Empty(Directory.EnumerateFiles(testCase.Root, ".report.json.tmp.*"));
    }

    [Fact]
    public void DocumentedCliShapeRunsTheVerifier()
    {
        using CompatibilityVerifierCase testCase = fixture.CreateCase();

        int exitCode = Program.Main(testCase.ToArguments());

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(testCase.Report));
    }

    [Fact]
    public void CommandLineRequiresExactVerifyArguments()
    {
        using CompatibilityVerifierCase testCase = fixture.CreateCase();
        string[] valid = testCase.ToArguments();

        VerifyOptions parsed = VerifierCommand.Parse(valid);
        Assert.Equal(testCase.Options, parsed);

        Assert.Throws<ArgumentException>(() => VerifierCommand.Parse(valid.Concat(["--unknown", "value"]).ToArray()));
        Assert.Throws<ArgumentException>(() => VerifierCommand.Parse(valid[..^2]));
        Assert.Throws<ArgumentException>(() => VerifierCommand.Parse(valid.Concat(["--mod-id", testCase.Options.ModId]).ToArray()));
        Assert.Throws<ArgumentException>(() => VerifierCommand.Parse(["check", .. valid[1..]]));
    }

    [Fact]
    public void InteropFromAnotherSupportedBuildIsRejectedForTheMod()
    {
        using CompatibilityVerifierCase testCase = fixture.CreateCase();
        VerifyOptions options = testCase.Options with { InteropDirectory = testCase.OtherInterop };

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => Verifier.Verify(options));
        Assert.Contains("fingerprint", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SameAssemblyVersionWithDifferentMetadataIsRejected()
    {
        using CompatibilityVerifierCase testCase = fixture.CreateCase();
        MutateAssembly(testCase.AssemblyCSharp, module =>
        {
            TypeDefinition type = module.GetType("Synthetic.PublicApi`1");
            type.Methods.Add(NewVoidMethod(module, "AddedPublicMethod"));
        });

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => Verifier.Verify(testCase.Options));
        Assert.Contains("Assembly-CSharp", exception.Message, StringComparison.Ordinal);
        Assert.Contains("fingerprint", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RuntimeTargetsAreScopedToRequestedMod()
    {
        using CompatibilityVerifierCase testCase = fixture.CreateCase();

        VerificationReport report = Verifier.Verify(testCase.Options);

        Assert.Equal(1, report.Facts.RuntimeTargetsVerified);
    }

    [Fact]
    public void MissingContractMemberIsRejectedAfterFingerprintMatches()
    {
        using CompatibilityVerifierCase testCase = fixture.CreateCase();
        MutateAssembly(testCase.AssemblyCSharp, module =>
        {
            TypeDefinition type = module.GetType("Synthetic.PublicApi`1");
            type.Methods.Remove(type.Methods.Single(method => method.Name == "Use"));
        });
        testCase.RefreshSupportedBuilds();

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => Verifier.Verify(testCase.Options));
        Assert.Contains("contract member", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WrongContractBaseTypeIsRejectedAfterFingerprintMatches()
    {
        using CompatibilityVerifierCase testCase = fixture.CreateCase();
        MutateAssembly(testCase.AssemblyCSharp, module =>
            module.GetType("Synthetic.PublicApi`1").BaseType = module.TypeSystem.Object);
        testCase.RefreshSupportedBuilds();

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => Verifier.Verify(testCase.Options));
        Assert.Contains("base type", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WrongEnumValueIsRejectedAfterFingerprintMatches()
    {
        using CompatibilityVerifierCase testCase = fixture.CreateCase();
        MutateAssembly(testCase.AssemblyCSharp, module =>
        {
            TypeDefinition mode = module.GetType("Synthetic.Mode");
            mode.Fields.Single(field => field.Name == "One").Constant = 2;
        });
        testCase.RefreshSupportedBuilds();

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => Verifier.Verify(testCase.Options));
        Assert.Contains("enum", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChangedGenericConstraintIsRejectedDespiteUnchangedAssemblyVersion()
    {
        using CompatibilityVerifierCase testCase = fixture.CreateCase();
        MutateAssembly(testCase.AssemblyCSharp, module =>
        {
            GenericParameter parameter = module.GetType("Synthetic.PublicApi`1").GenericParameters.Single();
            parameter.Attributes = (GenericParameterAttributes)0;
            parameter.Constraints.Clear();
        });

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => Verifier.Verify(testCase.Options));
        Assert.Contains("fingerprint", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlayerPackageRejectsGameAssemblyAndSecondDllLeakage()
    {
        using CompatibilityVerifierCase testCase = fixture.CreateCase();
        using (FileStream stream = File.Create(testCase.PlayerPackage))
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create))
        {
            AddZipEntry(archive, "BepInEx/plugins/FarmTogether2.AutoSellMod/FarmTogether2.AutoSellMod.dll", File.ReadAllBytes(testCase.RealPlugin));
            AddZipEntry(archive, "Assembly-CSharp.dll", [1, 2, 3]);
        }

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => Verifier.Verify(testCase.Options));
        Assert.Contains("game or stub assembly", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("extra")]
    [InlineData("wrong-type")]
    public void SupportedBuildsLoaderRejectsNonClosedJson(string mutation)
    {
        using CompatibilityVerifierCase testCase = fixture.CreateCase();
        string json = File.ReadAllText(testCase.SupportedBuilds, Encoding.UTF8);
        json = mutation switch
        {
            "duplicate" => json.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal),
            "extra" => json[..^1] + ",\"extra\":true}",
            "wrong-type" => json.Replace("\"steamBuildId\":\"90000001\"", "\"steamBuildId\":90000001", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        File.WriteAllText(testCase.SupportedBuilds, json, new UTF8Encoding(false));

        Assert.Throws<InvalidDataException>(() => Verifier.Verify(testCase.Options));
    }

    [Fact]
    public void SupportedBuildsRejectsAggregateThatDoesNotMatchAssemblyFingerprints()
    {
        using CompatibilityVerifierCase testCase = fixture.CreateCase();
        string json = File.ReadAllText(testCase.SupportedBuilds, Encoding.UTF8);
        using JsonDocument document = JsonDocument.Parse(json);
        string aggregate = document.RootElement.GetProperty("builds").GetProperty("90000001").GetProperty("aggregateSha256").GetString()!;
        string replacement = (aggregate[0] == 'a' ? "b" : "a") + aggregate[1..];
        File.WriteAllText(testCase.SupportedBuilds, json.Replace(aggregate, replacement, StringComparison.Ordinal), new UTF8Encoding(false));

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => Verifier.Verify(testCase.Options));
        Assert.Contains("aggregate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StubAndRealPluginMemberReferencesMustBeEquivalent()
    {
        using CompatibilityVerifierCase testCase = fixture.CreateCase();
        MutateAssembly(testCase.RealPlugin, module =>
        {
            MethodReference reference = module.GetMemberReferences().OfType<MethodReference>().Single(method => method.Name == "Use");
            reference.Name = "DifferentUse";
        });

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => Verifier.Verify(testCase.Options));
        Assert.Contains("MemberRef", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StubAndRealPluginAssemblyReferencesMustBeEquivalent()
    {
        using CompatibilityVerifierCase testCase = fixture.CreateCase();
        MutateAssembly(testCase.RealPlugin, module =>
            module.AssemblyReferences.Single(reference => reference.Name == "Assembly-CSharp").Version = new Version(9, 0, 0, 0));

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => Verifier.Verify(testCase.Options));
        Assert.Contains("AssemblyRef", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StubAndRealPluginTypeReferencesMustBeEquivalent()
    {
        using CompatibilityVerifierCase testCase = fixture.CreateCase();
        MutateAssembly(testCase.RealPlugin, module =>
            module.GetTypeReferences().Single(type => type.FullName == "Synthetic.PublicApi`1").Name = "DifferentApi`1");

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => Verifier.Verify(testCase.Options));
        Assert.Contains("TypeRef", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StubAndRealPluginAssemblyIdentitiesMustBeEquivalent()
    {
        using CompatibilityVerifierCase testCase = fixture.CreateCase();
        MutateAssembly(testCase.RealPlugin, module => module.Assembly.Name.Version = new Version(1, 1, 2, 0));

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => Verifier.Verify(testCase.Options));
        Assert.Contains("assembly identities", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EquivalentPluginsCannotReferenceMemberOutsideContract()
    {
        using CompatibilityVerifierCase testCase = fixture.CreateCase();
        foreach (string path in new[] { testCase.Options.StubPluginPath, testCase.RealPlugin })
        {
            MutateAssembly(path, module =>
                module.GetMemberReferences().OfType<MethodReference>().Single(method => method.Name == "Use").Name = "UncontractedUse");
        }

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => Verifier.Verify(testCase.Options));
        Assert.Contains("MemberRef", exception.Message, StringComparison.Ordinal);
        Assert.Contains("contract", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EquivalentPluginsCannotReferenceTypeOutsideContract()
    {
        using CompatibilityVerifierCase testCase = fixture.CreateCase();
        foreach (string path in new[] { testCase.Options.StubPluginPath, testCase.RealPlugin })
        {
            MutateAssembly(path, module =>
                module.GetTypeReferences().Single(type => type.FullName == "Synthetic.PublicApi`1").Name = "UncontractedApi`1");
        }

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => Verifier.Verify(testCase.Options));
        Assert.Contains("TypeRef", exception.Message, StringComparison.Ordinal);
        Assert.Contains("contract", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InteropAssemblyIdentityMustRemainApproved()
    {
        using CompatibilityVerifierCase testCase = fixture.CreateCase();
        MutateAssembly(Path.Combine(testCase.Options.InteropDirectory, "Il2Cppmscorlib.dll"),
            module => module.Assembly.Name.Version = new Version(4, 0, 0, 1));

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => Verifier.Verify(testCase.Options));
        Assert.Contains("identity", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FailureDoesNotReplaceExistingReport()
    {
        using CompatibilityVerifierCase testCase = fixture.CreateCase();
        const string sentinel = "existing-report";
        File.WriteAllText(testCase.Report, sentinel, new UTF8Encoding(false));
        File.Delete(testCase.PlayerPackage);

        Assert.Throws<InvalidDataException>(() => Verifier.Verify(testCase.Options));
        Assert.Equal(sentinel, File.ReadAllText(testCase.Report, Encoding.UTF8));
        Assert.Empty(Directory.EnumerateFiles(testCase.Root, ".report.json.tmp.*"));
    }

    private static MethodDefinition NewVoidMethod(ModuleDefinition module, string name)
    {
        MethodDefinition method = new(name, MethodAttributes.Public, module.TypeSystem.Void);
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        return method;
    }

    private static void MutateAssembly(string path, Action<ModuleDefinition> mutation)
    {
        string temporary = path + ".tmp";
        using (ModuleDefinition module = ModuleDefinition.ReadModule(path))
        {
            mutation(module);
            module.Write(temporary);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private static void AddZipEntry(ZipArchive archive, string name, byte[] bytes)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using Stream output = entry.Open();
        output.Write(bytes);
    }
}

public sealed class CompatibilityVerifierFixture : IDisposable
{
    private static readonly IReadOnlyDictionary<string, Version> ApprovedAssemblies =
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

    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private readonly string root = Directory.CreateTempSubdirectory("ft2-compat-fixture-").FullName;

    public CompatibilityVerifierFixture()
    {
        string current = Path.Combine(root, "current");
        string other = Path.Combine(root, "other");
        Directory.CreateDirectory(current);
        Directory.CreateDirectory(other);
        CreateInterop(current, addOtherBuildSurface: false);
        CreateInterop(other, addOtherBuildSurface: true);
        CreatePlugin(Path.Combine(root, "stub-plugin.dll"));
        CreatePlugin(Path.Combine(root, "real-plugin.dll"));
        WriteContract(Path.Combine(root, "contract.json"));
        WritePlayerPackage(Path.Combine(root, "player.zip"), Path.Combine(root, "real-plugin.dll"));
        RefreshSupportedBuilds(current, other, Path.Combine(root, "supported-builds.json"));
    }

    public CompatibilityVerifierCase CreateCase()
    {
        string destination = Directory.CreateTempSubdirectory("ft2-compat-case-").FullName;
        CopyTree(root, destination);
        return new CompatibilityVerifierCase(destination);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    internal static void RefreshSupportedBuilds(string current, string other, string output)
    {
        string currentSnapshot = Path.Combine(Path.GetDirectoryName(output)!, "current.snapshot.json");
        string otherSnapshot = Path.Combine(Path.GetDirectoryName(output)!, "other.snapshot.json");
        RunPowerShell(
            "& $env:FT2_EXPORT -SnapshotInteropDirectory $env:FT2_INTEROP -SteamBuildId 90000001 -SnapshotOutput $env:FT2_SNAPSHOT",
            ("FT2_EXPORT", Path.Combine(Root, "scripts/Export-GameApiContract.ps1")),
            ("FT2_INTEROP", current),
            ("FT2_SNAPSHOT", currentSnapshot));
        if (!File.Exists(otherSnapshot))
        {
            RunPowerShell(
                "& $env:FT2_EXPORT -SnapshotInteropDirectory $env:FT2_INTEROP -SteamBuildId 90000002 -SnapshotOutput $env:FT2_SNAPSHOT",
                ("FT2_EXPORT", Path.Combine(Root, "scripts/Export-GameApiContract.ps1")),
                ("FT2_INTEROP", other),
                ("FT2_SNAPSHOT", otherSnapshot));
        }
        RunPowerShell(
            "& $env:FT2_EXPORT -BuildSnapshot @($env:FT2_CURRENT,$env:FT2_OTHER) -ModBuild @('com.abmcar.farmtogether2.autosellmod=90000001','com.abmcar.farmtogether2.qolmod=90000002','com.abmcar.farmtogether2.automodrangemod=90000002','com.abmcar.farmtogether2.farmhandspeedmod=90000002') -SupportedBuildsOutput $env:FT2_OUTPUT",
            ("FT2_EXPORT", Path.Combine(Root, "scripts/Export-GameApiContract.ps1")),
            ("FT2_CURRENT", currentSnapshot),
            ("FT2_OTHER", otherSnapshot),
            ("FT2_OUTPUT", output));
    }

    private static void CreateInterop(string directory, bool addOtherBuildSurface)
    {
        foreach ((string name, Version version) in ApprovedAssemblies)
        {
            using AssemblyDefinition assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(name, version), name, ModuleKind.Dll);
            ModuleDefinition module = assembly.MainModule;
            module.AssemblyReferences.Add(new AssemblyNameReference("Il2CppInterop.Runtime", new Version(1, 5, 1, 0)));
            if (name == "Assembly-CSharp")
                AddGameSurface(module, addOtherBuildSurface);
            else
                AddMarkerSurface(module, name);
            assembly.Write(Path.Combine(directory, name + ".dll"));
        }
    }

    private static void AddGameSurface(ModuleDefinition module, bool addOtherBuildSurface)
    {
        TypeDefinition baseType = new("Synthetic", "BaseApi", TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object);
        module.Types.Add(baseType);

        TypeDefinition api = new("Synthetic", "PublicApi`1", TypeAttributes.Public | TypeAttributes.Class, baseType);
        GenericParameter typeParameter = new("T", api) { Attributes = GenericParameterAttributes.ReferenceTypeConstraint };
        typeParameter.Constraints.Add(new GenericParameterConstraint(module.ImportReference(typeof(IDisposable))));
        api.GenericParameters.Add(typeParameter);
        module.Types.Add(api);

        api.Fields.Add(new FieldDefinition("Counter", FieldAttributes.Public | FieldAttributes.Static, module.TypeSystem.Int32));
        MethodDefinition constructor = new(
            ".ctor",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            module.TypeSystem.Void);
        constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        api.Methods.Add(constructor);
        MethodDefinition getter = new("get_Value", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig, module.TypeSystem.Int32);
        getter.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
        getter.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        MethodDefinition setter = new("set_Value", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig, module.TypeSystem.Void);
        setter.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, module.TypeSystem.Int32));
        setter.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        api.Methods.Add(getter);
        api.Methods.Add(setter);
        PropertyDefinition property = new("Value", PropertyAttributes.None, module.TypeSystem.Int32) { GetMethod = getter, SetMethod = setter };
        api.Properties.Add(property);

        MethodDefinition use = new("Use", MethodAttributes.Public, module.TypeSystem.Void);
        use.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, module.TypeSystem.Int32));
        use.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        api.Methods.Add(use);

        MethodDefinition map = new("Map", MethodAttributes.Public, module.TypeSystem.Void);
        GenericParameter methodParameter = new("TMethod", map) { Attributes = GenericParameterAttributes.ReferenceTypeConstraint };
        methodParameter.Constraints.Add(new GenericParameterConstraint(module.ImportReference(typeof(IComparable))));
        map.GenericParameters.Add(methodParameter);
        map.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, methodParameter));
        map.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        api.Methods.Add(map);

        TypeDefinition mode = new("Synthetic", "Mode", TypeAttributes.Public | TypeAttributes.Sealed, module.ImportReference(typeof(Enum)));
        mode.Fields.Add(new FieldDefinition("value__", FieldAttributes.Public | FieldAttributes.SpecialName | FieldAttributes.RTSpecialName, module.TypeSystem.Int32));
        mode.Fields.Add(new FieldDefinition("One", FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal, mode) { Constant = 1 });
        module.Types.Add(mode);

        if (addOtherBuildSurface)
        {
            api.Methods.Add(NewVoidMethod(module, "OtherBuildOnly"));
            module.Types.Add(new TypeDefinition("Synthetic", "OtherModOnly", TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object));
        }
    }

    private static void AddMarkerSurface(ModuleDefinition module, string assemblyName)
    {
        string safeName = assemblyName.Replace("-", string.Empty, StringComparison.Ordinal).Replace(".", string.Empty, StringComparison.Ordinal);
        module.Types.Add(new TypeDefinition("Synthetic", safeName + "Api", TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object));
    }

    private static void CreatePlugin(string path)
    {
        using AssemblyDefinition assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("FarmTogether2.AutoSellMod", new Version(1, 1, 1, 0)),
            "FarmTogether2.AutoSellMod",
            ModuleKind.Dll);
        ModuleDefinition module = assembly.MainModule;
        AssemblyNameReference game = new("Assembly-CSharp", new Version(0, 0, 0, 0));
        module.AssemblyReferences.Add(game);
        TypeReference api = new("Synthetic", "PublicApi`1", module, game);
        TypeReference mode = new("Synthetic", "Mode", module, game, valueType: true);
        MethodReference constructor = new(".ctor", module.TypeSystem.Void, api) { HasThis = true };
        FieldReference counter = new("Counter", module.TypeSystem.Int32, api);
        MethodReference getter = new("get_Value", module.TypeSystem.Int32, api) { HasThis = true };
        MethodReference use = new("Use", module.TypeSystem.Void, api) { HasThis = true };
        use.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
        TypeDefinition plugin = new("SyntheticMod", "Plugin", TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object);
        MethodDefinition acceptMode = new("AcceptMode", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
        acceptMode.Parameters.Add(new ParameterDefinition("mode", ParameterAttributes.None, mode));
        acceptMode.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        MethodDefinition run = new("Run", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
        run.Body.Instructions.Add(Instruction.Create(OpCodes.Newobj, constructor));
        run.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
        run.Body.Instructions.Add(Instruction.Create(OpCodes.Ldsfld, counter));
        run.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
        run.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
        run.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, getter));
        run.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
        run.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
        run.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
        run.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, use));
        run.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        plugin.Methods.Add(acceptMode);
        plugin.Methods.Add(run);
        module.Types.Add(plugin);
        assembly.Write(path);
    }

    private static void WriteContract(string path)
    {
        object[] types =
        [
            new { assembly = "Assembly-CSharp", type = "Synthetic.BaseApi", kind = "class", baseType = "System.Object", supportOnly = false },
            new { assembly = "Assembly-CSharp", type = "Synthetic.Mode", kind = "enum", baseType = "System.Enum", supportOnly = false },
            new { assembly = "Assembly-CSharp", type = "Synthetic.OtherModOnly", kind = "class", baseType = "System.Object", supportOnly = false },
            new { assembly = "Assembly-CSharp", type = "Synthetic.PublicApi`1", kind = "class", baseType = "Synthetic.BaseApi", supportOnly = false }
        ];
        object[] members =
        [
            new { assembly = "Assembly-CSharp", declaringType = "Synthetic.PublicApi`1", kind = "field", signature = "System.Int32 Counter", isStatic = true },
            new { assembly = "Assembly-CSharp", declaringType = "Synthetic.PublicApi`1", kind = "property", signature = "System.Int32 Value", isStatic = false },
            new { assembly = "Assembly-CSharp", declaringType = "Synthetic.PublicApi`1", kind = "method", signature = "System.Void .ctor()", isStatic = false },
            new { assembly = "Assembly-CSharp", declaringType = "Synthetic.PublicApi`1", kind = "method", signature = "System.Void Map(TMethod)", isStatic = false },
            new { assembly = "Assembly-CSharp", declaringType = "Synthetic.PublicApi`1", kind = "method", signature = "System.Void Use(System.Int32)", isStatic = false }
        ];
        object[] runtimeTargets =
        [
            new { assembly = "Assembly-CSharp", type = "Synthetic.DoesNotExist", kind = "method", signature = "System.Void Missing()", required = false, modIds = new[] { "com.abmcar.farmtogether2.qolmod" } },
            new { assembly = "Assembly-CSharp", type = "Synthetic.PublicApi`1", kind = "method", signature = "System.Void Use(System.Int32)", required = true, modIds = new[] { "com.abmcar.farmtogether2.autosellmod" } }
        ];
        var contract = new
        {
            schemaVersion = 1,
            directTypes = types,
            directMembers = members,
            enumValues = new[] { new { assembly = "Assembly-CSharp", enumType = "Synthetic.Mode", name = "One", value = 1L } },
            runtimeTargets
        };
        File.WriteAllText(path, JsonSerializer.Serialize(contract), new UTF8Encoding(false));
    }

    private static void WritePlayerPackage(string path, string realPlugin)
    {
        using FileStream stream = File.Create(path);
        using ZipArchive archive = new(stream, ZipArchiveMode.Create);
        AddZipEntry(archive, "BepInEx/plugins/FarmTogether2.AutoSellMod/FarmTogether2.AutoSellMod.dll", File.ReadAllBytes(realPlugin));
        AddZipEntry(archive, "README.md", Encoding.UTF8.GetBytes("install"));
        AddZipEntry(archive, "LICENSE", Encoding.UTF8.GetBytes("license"));
    }

    private static MethodDefinition NewVoidMethod(ModuleDefinition module, string name)
    {
        MethodDefinition method = new(name, MethodAttributes.Public, module.TypeSystem.Void);
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        return method;
    }

    private static void AddZipEntry(ZipArchive archive, string name, byte[] bytes)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using Stream output = entry.Open();
        output.Write(bytes);
    }

    private static void RunPowerShell(string command, params (string Name, string Value)[] environment)
    {
        ProcessStartInfo startInfo = new("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);
        foreach ((string name, string value) in environment)
            startInfo.Environment[name] = value;
        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start pwsh.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"pwsh exited {process.ExitCode}. stdout: {stdout} stderr: {stderr}");
    }

    private static void CopyTree(string source, string destination)
    {
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
    }
}

public sealed class CompatibilityVerifierCase : IDisposable
{
    public CompatibilityVerifierCase(string root)
    {
        Root = root;
        Options = new VerifyOptions(
            Path.Combine(root, "contract.json"),
            Path.Combine(root, "supported-builds.json"),
            "com.abmcar.farmtogether2.autosellmod",
            Path.Combine(root, "current"),
            Path.Combine(root, "stub-plugin.dll"),
            Path.Combine(root, "real-plugin.dll"),
            Path.Combine(root, "player.zip"),
            Path.Combine(root, "report.json"));
    }

    public string Root { get; }
    public VerifyOptions Options { get; }
    public string SupportedBuilds => Options.SupportedBuildsPath;
    public string OtherInterop => Path.Combine(Root, "other");
    public string AssemblyCSharp => Path.Combine(Options.InteropDirectory, "Assembly-CSharp.dll");
    public string RealPlugin => Options.RealPluginPath;
    public string PlayerPackage => Options.PlayerPackagePath;
    public string Report => Options.ReportPath;

    public string[] ToArguments() =>
    [
        "verify",
        "--contract", Options.ContractPath,
        "--supported-builds", Options.SupportedBuildsPath,
        "--mod-id", Options.ModId,
        "--interop-dir", Options.InteropDirectory,
        "--stub-plugin", Options.StubPluginPath,
        "--real-plugin", Options.RealPluginPath,
        "--player-package", Options.PlayerPackagePath,
        "--report", Options.ReportPath
    ];

    public void RefreshSupportedBuilds() => CompatibilityVerifierFixture.RefreshSupportedBuilds(
        Options.InteropDirectory,
        OtherInterop,
        SupportedBuilds);

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
