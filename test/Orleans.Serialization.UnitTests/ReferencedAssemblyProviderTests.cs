using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
#if NET10_0_OR_GREATER
using System.IO;
#endif
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
#if NET10_0_OR_GREATER
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
#endif
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

    [Fact]
    public void AssemblyFileAvailabilityUsesProviderAssemblyForDynamicEntryAssembly()
    {
        var dynamicAssembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName($"DynamicEntryAssembly_{Guid.NewGuid():N}"),
            AssemblyBuilderAccess.Run);

        Assert.True(ReferencedAssemblyProvider.AreAssemblyFilesAvailable(null));
        Assert.Equal(
            ReferencedAssemblyProvider.AreAssemblyFilesAvailable(null),
            ReferencedAssemblyProvider.AreAssemblyFilesAvailable(dynamicAssembly));
    }

#if NET10_0_OR_GREATER
    [Fact]
    public void NetStandardAssetDependencyContextDiscoveryExposesAssemblyFileRequirement()
    {
        var assemblyPath = Path.Combine(
            AppContext.BaseDirectory,
            "TestAssets",
            "Orleans.Serialization.dll");
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        var metadata = peReader.GetMetadataReader();
        var providerType = metadata.TypeDefinitions
            .Select(metadata.GetTypeDefinition)
            .Single(type =>
                metadata.GetString(type.Namespace) == "Orleans.Serialization.Internal"
                && metadata.GetString(type.Name) == nameof(ReferencedAssemblyProvider));
        var method = providerType.GetMethods()
            .Select(metadata.GetMethodDefinition)
            .Single(method => metadata.GetString(method.Name) == nameof(ReferencedAssemblyProvider.AddFromDependencyContext));
        var attributeNames = method.GetCustomAttributes()
            .Select(handle => GetAttributeTypeName(metadata, metadata.GetCustomAttribute(handle)));

        Assert.Contains(
            "System.Diagnostics.CodeAnalysis.RequiresAssemblyFilesAttribute",
            attributeNames);
    }
#endif

    private static Assembly CreateApplicationPartAssembly(string referencedAssemblyName)
    {
        var name = new AssemblyName($"ReferencedAssemblyProviderTests_{Guid.NewGuid():N}");
        var assembly = AssemblyBuilder.DefineDynamicAssembly(name, AssemblyBuilderAccess.Run);
        var constructor = typeof(ApplicationPartAttribute).GetConstructor([typeof(string)])!;
        assembly.SetCustomAttribute(new CustomAttributeBuilder(constructor, [referencedAssemblyName]));
        assembly.DefineDynamicModule("Main");
        return assembly;
    }

#if NET10_0_OR_GREATER
    private static string GetAttributeTypeName(MetadataReader metadata, CustomAttribute attribute)
    {
        var typeHandle = attribute.Constructor.Kind switch
        {
            HandleKind.MemberReference => metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent,
            HandleKind.MethodDefinition => metadata.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType(),
            _ => throw new InvalidOperationException($"Unsupported attribute constructor handle: {attribute.Constructor.Kind}."),
        };

        return typeHandle.Kind switch
        {
            HandleKind.TypeDefinition => GetTypeName(metadata, metadata.GetTypeDefinition((TypeDefinitionHandle)typeHandle)),
            HandleKind.TypeReference => GetTypeName(metadata, metadata.GetTypeReference((TypeReferenceHandle)typeHandle)),
            _ => throw new InvalidOperationException($"Unsupported attribute type handle: {typeHandle.Kind}."),
        };
    }

    private static string GetTypeName(MetadataReader metadata, TypeDefinition type)
        => $"{metadata.GetString(type.Namespace)}.{metadata.GetString(type.Name)}";

    private static string GetTypeName(MetadataReader metadata, TypeReference type)
        => $"{metadata.GetString(type.Namespace)}.{metadata.GetString(type.Name)}";
#endif
}
