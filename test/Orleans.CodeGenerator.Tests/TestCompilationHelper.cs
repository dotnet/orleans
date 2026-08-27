using System.Collections.Immutable;
using System.IO;
using System.IO.Pipelines;
using System.Text.Json;
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
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static readonly ImmutableArray<MetadataReference> FrameworkReferences = CreateFrameworkReferences();

    /// <summary>
    /// Creates a <see cref="CSharpCompilation"/> with the .NET framework and Orleans assembly references.
    /// </summary>
    public static Task<CSharpCompilation> CreateCompilation(
        string sourceCode,
        string assemblyName = "TestProject",
        params MetadataReference[] additionalReferences)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
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

        var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode, cancellationToken: cancellationToken);
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

        return GetFrameworkAssemblyPaths(
                trustedPlatformAssemblies,
                typeof(object).Assembly.Location,
                typeof(ImmutableArray<>).Assembly.Location,
                typeof(Pipe).Assembly.Location,
                typeof(JsonSerializer).Assembly.Location)
            .Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToImmutableArray();
    }

    internal static ImmutableArray<string> GetFrameworkAssemblyPaths(
        string trustedPlatformAssemblies,
        string frameworkAssemblyPath,
        params string[] runtimeAssemblyPaths)
    {
        if (string.IsNullOrWhiteSpace(trustedPlatformAssemblies))
        {
            throw new InvalidOperationException("The test host must provide trusted platform assemblies.");
        }

        var frameworkDirectory = Path.GetDirectoryName(frameworkAssemblyPath);
        if (string.IsNullOrWhiteSpace(frameworkDirectory))
        {
            throw new InvalidOperationException("The test host framework directory must be available.");
        }

        frameworkDirectory = Path.GetFullPath(frameworkDirectory);
        var frameworkAssemblyPaths = trustedPlatformAssemblies
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(path => PathComparer.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), frameworkDirectory))
            .ToImmutableArray();

        if (frameworkAssemblyPaths.IsEmpty)
        {
            throw new InvalidOperationException(
                $"The trusted platform assemblies must include references from '{frameworkDirectory}'.");
        }

        var runtimeAssemblies = runtimeAssemblyPaths.ToDictionary(static path => Path.GetFileName(path)!, PathComparer);
        return frameworkAssemblyPaths
            .Where(path => !runtimeAssemblies.ContainsKey(Path.GetFileName(path)!))
            .Concat(runtimeAssemblies.Values)
            .ToImmutableArray();
    }
}
