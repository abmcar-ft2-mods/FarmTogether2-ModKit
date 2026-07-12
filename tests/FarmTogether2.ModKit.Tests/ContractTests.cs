using FarmTogether2.ContractModel;
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
            WriteSnapshot(second, "90000002", '2');

            ProcessStartInfo startInfo = new("pwsh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add("& $env:FT2_EXPORT_SCRIPT -BuildSnapshot @($env:FT2_SNAPSHOT_A,$env:FT2_SNAPSHOT_B) -ModBuild @('com.abmcar.farmtogether2.autosellmod=90000002','com.abmcar.farmtogether2.qolmod=90000001','com.abmcar.farmtogether2.automodrangemod=90000001','com.abmcar.farmtogether2.farmhandspeedmod=90000001') -SupportedBuildsOutput $env:FT2_SUPPORTED_OUTPUT");
            startInfo.Environment["FT2_EXPORT_SCRIPT"] = Path.Combine(Root, "scripts/Export-GameApiContract.ps1");
            startInfo.Environment["FT2_SNAPSHOT_A"] = first;
            startInfo.Environment["FT2_SNAPSHOT_B"] = second;
            startInfo.Environment["FT2_SUPPORTED_OUTPUT"] = output;

            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start pwsh.");
            string standardOutput = process.StandardOutput.ReadToEnd();
            string standardError = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, $"pwsh exited {process.ExitCode}. stdout: {standardOutput} stderr: {standardError}");

            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(output));
            JsonElement root = document.RootElement;
            Assert.Equal(4, root.EnumerateObject().Count());
            Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(4, root.GetProperty("mods").EnumerateObject().Count());
            Assert.Equal(2, root.GetProperty("builds").EnumerateObject().Count());
            Assert.Equal(7, root.GetProperty("assemblies").GetArrayLength());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void WriteSnapshot(string path, string buildId, char hashDigit)
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
            assemblies = names.Select(x => new { name = x, version = versions[x], culture = "", publicKeyToken = "" }).ToArray()
        };
        File.WriteAllText(path, JsonSerializer.Serialize(snapshot), new UTF8Encoding(false));
    }
}
