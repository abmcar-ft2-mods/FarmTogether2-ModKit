using Mono.Cecil;
using Xunit;

namespace FarmTogether2.ModKit.Tests;

public sealed class StubAssemblyTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));

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
        Assert.Equal("UnityEngine.Behaviour", module.GetType("UnityEngine.MonoBehaviour").BaseType.FullName);
        Assert.Contains(module.GetType("UnityEngine.Component").Methods,
            x => x.Name == "GetComponentInParent" && x.HasGenericParameters && x.Parameters.Count == 0);
        Assert.Contains(module.GetType("UnityEngine.Vector2").Properties, x => x.Name == "magnitude");
        Assert.Contains(module.GetType("UnityEngine.Texture2D").Properties, x => x.Name == "whiteTexture");
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
}
