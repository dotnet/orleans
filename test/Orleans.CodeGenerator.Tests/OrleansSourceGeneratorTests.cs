using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Xunit;

namespace Orleans.CodeGenerator.Tests;

public class OrleansSourceGeneratorTests
{
    [Fact]
    public async Task TestBasicClass()
    {
        var projectName = "TestProject";
        var compilation = await CreateCompilation(
@"using Orleans;

namespace TestProject;

[Serializable, GenerateSerializer]
public class DemoData
{
    [Id(0)]
    public string Value { get; set; } = string.Empty;
}
", projectName);

        var generator = new OrleansSerializationSourceGenerator();

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [generator],
            driverOptions: new GeneratorDriverOptions(default));

        // Run the generator
        driver = driver.RunGenerators(compilation);

        var result = driver.GetRunResult().Results.Single();
        Assert.Empty(result.Diagnostics);

        Assert.Single(result.GeneratedSources);
        Assert.Equal($"{projectName}.orleans.g.cs", result.GeneratedSources[0].HintName);
        var generatedSource = result.GeneratedSources[0].SourceText.ToString();

        await Verify(generatedSource, extension: "cs").UseDirectory("snapshots");
    }

    private static async Task<CSharpCompilation> CreateCompilation(string sourceCode, string assemblyName = "TestProject")
    {
        var references = await ReferenceAssemblies.Net.Net80.ResolveAsync(LanguageNames.CSharp, default);

        // Add the Orleans Orleans.Core.Abstractions assembly
        references = references.AddRange(
            // Orleans.Core.Abstractions
            MetadataReference.CreateFromFile(typeof(GrainId).Assembly.Location),
            // Orleans.Core
            MetadataReference.CreateFromFile(typeof(IClusterClientLifecycle).Assembly.Location),
            // Orleans.Runtime
            MetadataReference.CreateFromFile(typeof(IGrainActivator).Assembly.Location),
            // Orleans.Serialization
            MetadataReference.CreateFromFile(typeof(Serializer).Assembly.Location),
            // Orleans.Serialization.Abstractions
            MetadataReference.CreateFromFile(typeof(GenerateFieldIds).Assembly.Location),
            // Microsoft.Extensions.DependencyInjection.Abstractions
            MetadataReference.CreateFromFile(typeof(ActivatorUtilitiesConstructorAttribute).Assembly.Location)
        );

        var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode);

        return CSharpCompilation.Create(assemblyName, [syntaxTree], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
