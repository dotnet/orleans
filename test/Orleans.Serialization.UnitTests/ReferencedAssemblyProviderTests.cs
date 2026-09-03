using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Orleans.Serialization.Internal;

namespace Orleans.Serialization.UnitTests;

[Trait("Category", "BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Serialization")]
public sealed class ReferencedAssemblyProviderTests
{
    [Fact]
    public void AddAssemblyDiscoversGeneratedApplicationPartsWithoutAssemblyFiles()
    {
        var referencedAssembly = typeof(ReferencedAssemblyProviderTests).Assembly;
        var assembly = CreateApplicationPartAssembly(referencedAssembly.GetName().Name!);
        var result = new HashSet<Assembly>();

        ReferencedAssemblyProvider.AddAssembly(result, assembly);

        Assert.True(assembly.IsDynamic);
        Assert.Contains(assembly, result);
        Assert.Contains(referencedAssembly, result);
    }

    [Fact]
    public void GetRelevantAssembliesIncludesGeneratedEntryAssemblyMetadata()
    {
        var result = ReferencedAssemblyProvider.GetRelevantAssemblies().ToHashSet();

        Assert.Contains(typeof(ReferencedAssemblyProviderTests).Assembly, result);
    }

    [Fact]
    public void DependencyContextDiscoveryExposesAssemblyFileRequirement()
    {
        var method = typeof(ReferencedAssemblyProvider).GetMethod(
            nameof(ReferencedAssemblyProvider.AddFromDependencyContext));

        var attribute = method!.GetCustomAttribute<RequiresAssemblyFilesAttribute>();

        Assert.NotNull(attribute);
        Assert.Contains(nameof(ReferencedAssemblyProvider.GetRelevantAssemblies), attribute!.Message);
    }

    private static Assembly CreateApplicationPartAssembly(string referencedAssemblyName)
    {
        var name = new AssemblyName($"ReferencedAssemblyProviderTests_{Guid.NewGuid():N}");
        var assembly = AssemblyBuilder.DefineDynamicAssembly(name, AssemblyBuilderAccess.Run);
        var constructor = typeof(ApplicationPartAttribute).GetConstructor([typeof(string)])!;
        assembly.SetCustomAttribute(new CustomAttributeBuilder(constructor, [referencedAssemblyName]));
        assembly.DefineDynamicModule("Main");
        return assembly;
    }
}
