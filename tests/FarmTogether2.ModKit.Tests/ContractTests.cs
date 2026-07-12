using FarmTogether2.ContractModel;
using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace FarmTogether2.ModKit.Tests;

public sealed class ContractTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));

    [Fact]
    public void ContractHasApprovedDirectSurface()
    {
        GameApiContract contract = ContractJson.Load(Path.Combine(Root, "contracts/game-api.contract.json"));
        Assert.Equal(1, contract.SchemaVersion);
        Assert.Equal(57, contract.DirectTypes.Count);
        Assert.Equal(110, contract.DirectMembers.Count);
        Assert.Equal(57, contract.DirectTypes.Select(x => $"{x.Assembly}:{x.Type}").Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(110, contract.DirectMembers.Select(x => $"{x.Assembly}:{x.DeclaringType}:{x.Signature}").Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("com.abmcar.farmtogether2.qolmod", "GameGlobals.Game", "System.Single TractorWorkSpeed(Logic.FarmFeatureLevel)")]
    [InlineData("com.abmcar.farmtogether2.automodrangemod", "LocalPlayer", "System.Void UpdateFarmTiles(Il2CppSystem.Collections.Generic.List<Logic.Farm.FarmTile>,System.Boolean,System.Boolean,System.Boolean)")]
    [InlineData("com.abmcar.farmtogether2.automodrangemod", "LocalPlayer", "System.Void ReDoAutoTractor(Logic.FarmTileId,Il2CppSystem.Collections.Generic.List<Logic.Farm.FarmTile>)")]
    [InlineData("com.abmcar.farmtogether2.automodrangemod", "SelectedTilesTractorWork", "System.Void Apply(SelectedTiles,LocalPlayer)")]
    [InlineData("com.abmcar.farmtogether2.automodrangemod", "SelectedTilesTractorWork", "System.Boolean Check(SelectedTiles,LocalPlayer)")]
    [InlineData("com.abmcar.farmtogether2.farmhandspeedmod", "Logic.Farm.Buildings.FarmhandBuilding", "System.Void Tick(System.UInt32)")]
    [InlineData("com.abmcar.farmtogether2.farmhandspeedmod", "View.Farmhands.LocalFarmhandView", "System.Void UpdateIdle()")]
    public void RuntimeTargetIsExplicit(string modId, string type, string signature)
    {
        GameApiContract contract = ContractJson.Load(Path.Combine(Root, "contracts/game-api.contract.json"));
        Assert.Contains(contract.RuntimeTargets, x => x.Type == type && x.Signature == signature && x.ModIds.Contains(modId, StringComparer.Ordinal));
    }

    [Fact]
    public void RequiredRuntimeTargetsArePartOfDirectSurface()
    {
        GameApiContract contract = ContractJson.Load(Path.Combine(Root, "contracts/game-api.contract.json"));
        Assert.Equal(30, contract.RuntimeTargets.Count);
        Assert.Equal(9, contract.RuntimeTargets.Count(x => x.Required));
        foreach (RuntimeTargetContract target in contract.RuntimeTargets.Where(x => x.Required))
        {
            Assert.Contains(contract.DirectMembers, member =>
                member.Assembly == target.Assembly &&
                member.DeclaringType == target.Type &&
                member.Kind == target.Kind &&
                member.Signature == target.Signature);
        }
    }

    [Fact]
    public void ContractLoaderRejectsUnsupportedSchema()
    {
        string directory = Directory.CreateTempSubdirectory("ft2-contract-loader-").FullName;
        try
        {
            string path = Path.Combine(directory, "contract.json");
            File.WriteAllText(path, "{\"schemaVersion\":2,\"directTypes\":[],\"directMembers\":[],\"enumValues\":[],\"runtimeTargets\":[]}", new UTF8Encoding(false));
            InvalidDataException exception = Assert.Throws<InvalidDataException>(() => ContractJson.Load(path));
            Assert.Contains("Unsupported contract schema 2", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    public static IEnumerable<object[]> InvalidClosedContracts()
    {
        yield return ["{\"schemaVersion\":1,\"schemaVersion\":1,\"directTypes\":[],\"directMembers\":[],\"enumValues\":[],\"runtimeTargets\":[]}"];
        yield return ["{\"SchemaVersion\":1,\"directTypes\":[],\"directMembers\":[],\"enumValues\":[],\"runtimeTargets\":[]}"];
        yield return ["{\"schemaVersion\":\"1\",\"directTypes\":[],\"directMembers\":[],\"enumValues\":[],\"runtimeTargets\":[]}"];
        yield return ["{\"schemaVersion\":1,\"directTypes\":null,\"directMembers\":[],\"enumValues\":[],\"runtimeTargets\":[]}"];
        yield return ["{\"schemaVersion\":1,\"directTypes\":[{\"assembly\":\"A\",\"type\":\"T\",\"kind\":\"class\",\"baseType\":null,\"supportOnly\":false,\"extra\":true}],\"directMembers\":[],\"enumValues\":[],\"runtimeTargets\":[]}"];
        yield return ["{\"schemaVersion\":1,\"directTypes\":[{\"assembly\":\"A\",\"type\":\"T\",\"kind\":\"class\",\"baseType\":null,\"supportOnly\":\"false\"}],\"directMembers\":[],\"enumValues\":[],\"runtimeTargets\":[]}"];
    }

    [Theory]
    [MemberData(nameof(InvalidClosedContracts))]
    public void ContractLoaderRejectsNonClosedOrWrongTypedJson(string json)
    {
        string directory = Directory.CreateTempSubdirectory("ft2-contract-loader-").FullName;
        try
        {
            string path = Path.Combine(directory, "contract.json");
            File.WriteAllText(path, json, new UTF8Encoding(false));
            Assert.Throws<InvalidDataException>(() => ContractJson.Load(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SupportedBuildGeneratorWritesClosedValidatedShape()
    {
        string directory = Directory.CreateTempSubdirectory("ft2-supported-builds-").FullName;
        try
        {
            string first = Path.Combine(directory, "first.json");
            string second = Path.Combine(directory, "second.json");
            string output = Path.Combine(directory, "supported-builds.json");
            WriteSnapshot(first, "90000001", '1');
            WriteSnapshot(second, "90000002", '2', reverseIdentities: true);

            ProcessResult result = RunSupportedBuildGenerator(first, second, output);
            Assert.True(result.ExitCode == 0, $"pwsh exited {result.ExitCode}. stdout: {result.StandardOutput} stderr: {result.StandardError}");

            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(output));
            JsonElement root = document.RootElement;
            Assert.Equal(4, root.EnumerateObject().Count());
            Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(4, root.GetProperty("mods").EnumerateObject().Count());
            Assert.Equal(2, root.GetProperty("builds").EnumerateObject().Count());
            Assert.Equal(7, root.GetProperty("assemblies").GetArrayLength());
            Assert.Empty(Directory.EnumerateFiles(directory, ".supported-builds.json.tmp.*"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("extra")]
    [InlineData("wrong-type")]
    public void SupportedBuildGeneratorRejectsNonClosedOrWrongTypedSnapshot(string mutation)
    {
        string directory = Directory.CreateTempSubdirectory("ft2-supported-builds-invalid-").FullName;
        try
        {
            string first = Path.Combine(directory, "first.json");
            string second = Path.Combine(directory, "second.json");
            string output = Path.Combine(directory, "supported-builds.json");
            WriteSnapshot(first, "90000001", '1');
            WriteSnapshot(second, "90000002", '2');
            const string sentinel = "preexisting-output";
            File.WriteAllText(output, sentinel, new UTF8Encoding(false));

            string json = File.ReadAllText(first, Encoding.UTF8);
            json = mutation switch
            {
                "duplicate" => json.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal),
                "extra" => json[..^1] + ",\"extra\":true}",
                "wrong-type" => json.Replace("\"steamBuildId\":\"90000001\"", "\"steamBuildId\":90000001", StringComparison.Ordinal),
                _ => throw new ArgumentOutOfRangeException(nameof(mutation))
            };
            File.WriteAllText(first, json, new UTF8Encoding(false));

            ProcessResult result = RunSupportedBuildGenerator(first, second, output);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Equal(sentinel, File.ReadAllText(output, Encoding.UTF8));
            Assert.Empty(Directory.EnumerateFiles(directory, ".supported-builds.json.tmp.*"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CanonicalMetadataFingerprintIncludesGenericParameterAttributesAndConstraints()
    {
        string directory = Directory.CreateTempSubdirectory("ft2-fingerprint-").FullName;
        try
        {
            SyntheticSurfaceOptions baselineOptions = new();
            CreateSyntheticInterop(directory, baselineOptions);
            string baseline = RunSnapshotAndReadAssemblyHash(directory, "baseline.json");

            CreateSyntheticInterop(directory, baselineOptions with { TypeParameterAttributes = GenericParameterAttributes.ReferenceTypeConstraint });
            string typeAttribute = RunSnapshotAndReadAssemblyHash(directory, "type-attribute.json");

            CreateSyntheticInterop(directory, baselineOptions with { TypeHasConstraint = true });
            string typeConstraint = RunSnapshotAndReadAssemblyHash(directory, "type-constraint.json");

            CreateSyntheticInterop(directory, baselineOptions with { MethodParameterAttributes = GenericParameterAttributes.ReferenceTypeConstraint });
            string methodAttribute = RunSnapshotAndReadAssemblyHash(directory, "method-attribute.json");

            CreateSyntheticInterop(directory, baselineOptions with { MethodHasConstraint = true });
            string methodConstraint = RunSnapshotAndReadAssemblyHash(directory, "method-constraint.json");

            Assert.Equal(5, new[] { baseline, typeAttribute, typeConstraint, methodAttribute, methodConstraint }.Distinct(StringComparer.Ordinal).Count());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CanonicalMetadataFingerprintIncludesContainingVisibilityAndSpecialNameFlags()
    {
        string directory = Directory.CreateTempSubdirectory("ft2-fingerprint-flags-").FullName;
        try
        {
            SyntheticSurfaceOptions baselineOptions = new();
            CreateSyntheticInterop(directory, baselineOptions);
            string baseline = RunSnapshotAndReadAssemblyHash(directory, "baseline.json");

            CreateSyntheticInterop(directory, baselineOptions with { OuterTypeIsPublic = false });
            string hiddenOuter = RunSnapshotAndReadAssemblyHash(directory, "hidden-outer.json");

            CreateSyntheticInterop(directory, baselineOptions with { OperatorIsSpecialName = true });
            string specialOperator = RunSnapshotAndReadAssemblyHash(directory, "special-operator.json");

            CreateSyntheticInterop(directory, baselineOptions with { AccessorIsSpecialName = true });
            string specialAccessor = RunSnapshotAndReadAssemblyHash(directory, "special-accessor.json");

            Assert.Equal(4, new[] { baseline, hiddenOuter, specialOperator, specialAccessor }.Distinct(StringComparer.Ordinal).Count());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ProcessResult RunSupportedBuildGenerator(string first, string second, string output)
    {
        ProcessStartInfo startInfo = NewPowerShellStartInfo();
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("& $env:FT2_EXPORT_SCRIPT -BuildSnapshot @($env:FT2_SNAPSHOT_A,$env:FT2_SNAPSHOT_B) -ModBuild @('com.abmcar.farmtogether2.autosellmod=90000002','com.abmcar.farmtogether2.qolmod=90000001','com.abmcar.farmtogether2.automodrangemod=90000001','com.abmcar.farmtogether2.farmhandspeedmod=90000001') -SupportedBuildsOutput $env:FT2_SUPPORTED_OUTPUT");
        startInfo.Environment["FT2_EXPORT_SCRIPT"] = Path.Combine(Root, "scripts/Export-GameApiContract.ps1");
        startInfo.Environment["FT2_SNAPSHOT_A"] = first;
        startInfo.Environment["FT2_SNAPSHOT_B"] = second;
        startInfo.Environment["FT2_SUPPORTED_OUTPUT"] = output;
        return RunProcess(startInfo);
    }

    private static string RunSnapshotAndReadAssemblyHash(string directory, string outputName)
    {
        string output = Path.Combine(directory, outputName);
        ProcessStartInfo startInfo = NewPowerShellStartInfo();
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("& $env:FT2_EXPORT_SCRIPT -SnapshotInteropDirectory $env:FT2_INTEROP -SteamBuildId 90000001 -SnapshotOutput $env:FT2_SNAPSHOT_OUTPUT");
        startInfo.Environment["FT2_EXPORT_SCRIPT"] = Path.Combine(Root, "scripts/Export-GameApiContract.ps1");
        startInfo.Environment["FT2_INTEROP"] = directory;
        startInfo.Environment["FT2_SNAPSHOT_OUTPUT"] = output;
        ProcessResult result = RunProcess(startInfo);
        Assert.True(result.ExitCode == 0, $"pwsh exited {result.ExitCode}. stdout: {result.StandardOutput} stderr: {result.StandardError}");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(output));
        return document.RootElement.GetProperty("assemblyMetadataSha256").GetProperty("Assembly-CSharp").GetString()!;
    }

    private static ProcessStartInfo NewPowerShellStartInfo() => new("pwsh")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };

    private static ProcessResult RunProcess(ProcessStartInfo startInfo)
    {
        startInfo.ArgumentList.Insert(0, "-NoProfile");
        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start pwsh.");
        string standardOutput = process.StandardOutput.ReadToEnd();
        string standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, standardOutput, standardError);
    }

    private static void CreateSyntheticInterop(string directory, SyntheticSurfaceOptions options)
    {
        Dictionary<string, Version> versions = ApprovedAssemblies().ToDictionary(
            entry => entry.Key,
            entry => Version.Parse(entry.Value),
            StringComparer.Ordinal);

        foreach ((string name, Version version) in versions)
        {
            using AssemblyDefinition assembly = AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition(name, version),
                name,
                ModuleKind.Dll);
            ModuleDefinition module = assembly.MainModule;
            module.AssemblyReferences.Add(new AssemblyNameReference("Il2CppInterop.Runtime", new Version(1, 5, 1, 0)));

            TypeDefinition type = new(
                "Synthetic",
                name == "Assembly-CSharp" ? "PublicApi`1" : "PublicApi",
                TypeAttributes.Public | TypeAttributes.Class,
                module.TypeSystem.Object);
            module.Types.Add(type);

            if (name == "Assembly-CSharp")
            {
                GenericParameter typeParameter = new("T", type) { Attributes = options.TypeParameterAttributes };
                if (options.TypeHasConstraint)
                    typeParameter.Constraints.Add(new GenericParameterConstraint(module.ImportReference(typeof(IDisposable))));
                type.GenericParameters.Add(typeParameter);

                MethodDefinition method = new("Map", MethodAttributes.Public, module.TypeSystem.Void);
                GenericParameter methodParameter = new("TMethod", method) { Attributes = options.MethodParameterAttributes };
                if (options.MethodHasConstraint)
                    methodParameter.Constraints.Add(new GenericParameterConstraint(module.ImportReference(typeof(IComparable))));
                method.GenericParameters.Add(methodParameter);
                method.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, methodParameter));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                type.Methods.Add(method);

                MethodAttributes operatorAttributes = MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig;
                if (options.OperatorIsSpecialName)
                    operatorAttributes |= MethodAttributes.SpecialName;
                MethodDefinition equality = new("op_Equality", operatorAttributes, module.TypeSystem.Boolean);
                equality.Parameters.Add(new ParameterDefinition("left", ParameterAttributes.None, type));
                equality.Parameters.Add(new ParameterDefinition("right", ParameterAttributes.None, type));
                equality.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
                equality.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                type.Methods.Add(equality);

                MethodAttributes accessorAttributes = MethodAttributes.Public | MethodAttributes.HideBySig;
                if (options.AccessorIsSpecialName)
                    accessorAttributes |= MethodAttributes.SpecialName;
                MethodDefinition getter = new("get_Value", accessorAttributes, module.TypeSystem.Int32);
                getter.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
                getter.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                type.Methods.Add(getter);
                type.Properties.Add(new PropertyDefinition("Value", PropertyAttributes.None, module.TypeSystem.Int32) { GetMethod = getter });

                TypeAttributes outerAttributes = (options.OuterTypeIsPublic ? TypeAttributes.Public : TypeAttributes.NotPublic) | TypeAttributes.Class;
                TypeDefinition outer = new("Synthetic", "Outer", outerAttributes, module.TypeSystem.Object);
                TypeDefinition nested = new(string.Empty, "Nested", TypeAttributes.NestedPublic | TypeAttributes.Class, module.TypeSystem.Object);
                outer.NestedTypes.Add(nested);
                module.Types.Add(outer);
            }

            assembly.Write(Path.Combine(directory, name + ".dll"));
        }
    }

    private static IReadOnlyDictionary<string, string> ApprovedAssemblies() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Assembly-CSharp"] = "0.0.0.0",
        ["Il2Cppmscorlib"] = "4.0.0.0",
        ["MilkstoneUnityExtensions"] = "1.0.0.0",
        ["UnityEngine.CoreModule"] = "0.0.0.0",
        ["UnityEngine.IMGUIModule"] = "0.0.0.0",
        ["UnityEngine.InputLegacyModule"] = "0.0.0.0",
        ["UnityEngine.TextRenderingModule"] = "0.0.0.0"
    };

    private static void WriteSnapshot(string path, string buildId, char hashDigit, bool reverseIdentities = false)
    {
        string[] names =
        [
            "Assembly-CSharp",
            "Il2Cppmscorlib",
            "MilkstoneUnityExtensions",
            "UnityEngine.CoreModule",
            "UnityEngine.IMGUIModule",
            "UnityEngine.InputLegacyModule",
            "UnityEngine.TextRenderingModule"
        ];
        Dictionary<string, string> versions = new(StringComparer.Ordinal)
        {
            ["Assembly-CSharp"] = "0.0.0.0",
            ["Il2Cppmscorlib"] = "4.0.0.0",
            ["MilkstoneUnityExtensions"] = "1.0.0.0",
            ["UnityEngine.CoreModule"] = "0.0.0.0",
            ["UnityEngine.IMGUIModule"] = "0.0.0.0",
            ["UnityEngine.InputLegacyModule"] = "0.0.0.0",
            ["UnityEngine.TextRenderingModule"] = "0.0.0.0"
        };
        string hash = new(hashDigit, 64);
        Dictionary<string, string> hashes = names.ToDictionary(x => x, _ => hash, StringComparer.Ordinal);
        string aggregateInput = string.Join('\n', names.Select(x => $"{x}\t{hashes[x]}"));
        string aggregate = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(aggregateInput))).ToLowerInvariant();
        var snapshot = new
        {
            schemaVersion = 1,
            steamBuildId = buildId,
            aggregateSha256 = aggregate,
            assemblyMetadataSha256 = hashes,
            assemblies = (reverseIdentities ? names.Reverse() : names).Select(x => new { name = x, version = versions[x], culture = "", publicKeyToken = "" }).ToArray()
        };
        File.WriteAllText(path, JsonSerializer.Serialize(snapshot), new UTF8Encoding(false));
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed record SyntheticSurfaceOptions(
        GenericParameterAttributes TypeParameterAttributes = (GenericParameterAttributes)0,
        bool TypeHasConstraint = false,
        GenericParameterAttributes MethodParameterAttributes = (GenericParameterAttributes)0,
        bool MethodHasConstraint = false,
        bool OuterTypeIsPublic = true,
        bool OperatorIsSpecialName = false,
        bool AccessorIsSpecialName = false);
}
