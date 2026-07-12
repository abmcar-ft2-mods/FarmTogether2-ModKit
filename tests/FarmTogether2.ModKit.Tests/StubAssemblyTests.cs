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
}
