using System.Collections.Immutable;
using System.IO;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;

namespace Orleans.CodeGenerator.Tests;

/// <summary>
/// Shared helper for creating Roslyn compilations with the necessary Orleans references.
/// Used across all code generator test files.
/// </summary>
internal static class TestCompilationHelper
{
    private static readonly ImmutableArray<MetadataReference> FrameworkReferences = CreateFrameworkReferences();

    /// <summary>
    /// Creates a <see cref="CSharpCompilation"/> with the .NET framework and Orleans assembly references.
    /// </summary>
    public static Task<CSharpCompilation> CreateCompilation(
        string sourceCode,
        string assemblyName = "TestProject",
        params MetadataReference[] additionalReferences)
    {
        var references = FrameworkReferences.AddRange(
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
        return Task.FromResult(CSharpCompilation.Create(
            assemblyName,
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)));
    }

    private static ImmutableArray<MetadataReference> CreateFrameworkReferences()
    {
        var trustedPlatformAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InvalidOperationException("The test host must provide trusted platform assemblies.");
        var frameworkDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location);

        return trustedPlatformAssemblies
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(path => Path.GetDirectoryName(path) == frameworkDirectory)
            .Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToImmutableArray();
    }
}
