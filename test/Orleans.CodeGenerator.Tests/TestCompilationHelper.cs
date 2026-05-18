using System.Collections.Immutable;
using System.IO;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;

namespace Orleans.CodeGenerator.Tests;

/// <summary>
/// Shared helper for creating Roslyn compilations with the necessary Orleans references.
/// Used across all code generator test files.
/// </summary>
internal static class TestCompilationHelper
{
    /// <summary>
    /// Creates a <see cref="CSharpCompilation"/> with the .NET framework and Orleans assembly references.
    /// </summary>
    public static async Task<CSharpCompilation> CreateCompilation(
        string sourceCode,
        string assemblyName = "TestProject",
        params MetadataReference[] additionalReferences)
    {
#if NET10_0_OR_GREATER
        var net10References = new ReferenceAssemblies(
            "net10.0",
            new PackageIdentity("Microsoft.NETCore.App.Ref", "10.0.0"),
            Path.Combine("ref", "net10.0"));

        var references = await net10References.ResolveAsync(LanguageNames.CSharp, default);
#else
        var references = await ReferenceAssemblies.Net.Net80.ResolveAsync(LanguageNames.CSharp, default);
#endif

        references = references.AddRange(
            MetadataReference.CreateFromFile(typeof(GrainId).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(IClusterClientLifecycle).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(IGrainActivator).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Serializer).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(GenerateFieldIds).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(ActivatorUtilitiesConstructorAttribute).Assembly.Location));

        if (additionalReferences.Length > 0)
        {
            references = references.AddRange(additionalReferences);
        }

        var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode);
        return CSharpCompilation.Create(assemblyName, [syntaxTree], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
