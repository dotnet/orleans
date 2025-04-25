using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Testing;
using Orleans.Serialization;
using Xunit;

namespace Orleans.CodeGenerator.Tests;

public class OrleansSourceGeneratorTests
{
    [Fact]
    public async Task Test1()
    {
        var compilation = await CreateCompilation(
@"using Orleans;

namespace TestProject;

[Serializable, GenerateSerializer]
public class DemoData
{
    [Id(0)]
    public string Value { get; set; } = string.Empty;
}
");

        var generator = new OrleansSerializationSourceGenerator();

        // trackIncrementalGeneratorSteps allows to report info about each step of the generator
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [generator],
            driverOptions: new GeneratorDriverOptions(default, trackIncrementalGeneratorSteps: true));

        // Run the generator
        driver = driver.RunGenerators(compilation);

        // Update the compilation and rerun the generator
        compilation = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("// dummy"));
        driver = driver.RunGenerators(compilation);

        // Assert the driver doesn't recompute the output
        var result = driver.GetRunResult().Results.Single();
        var allOutputs = result.TrackedOutputSteps.SelectMany(outputStep => outputStep.Value).SelectMany(output => output.Outputs);
        Assert.Collection(allOutputs, output => Assert.Equal(IncrementalStepRunReason.Cached, output.Reason));

        // Assert the driver use the cached result from AssemblyName and Syntax
        var assemblyNameOutputs = result.TrackedSteps["AssemblyName"].Single().Outputs;
        Assert.Collection(assemblyNameOutputs, output => Assert.Equal(IncrementalStepRunReason.Unchanged, output.Reason));

        var syntaxOutputs = result.TrackedSteps["Syntax"].Single().Outputs;
        Assert.Collection(syntaxOutputs, output => Assert.Equal(IncrementalStepRunReason.Unchanged, output.Reason));
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
            MetadataReference.CreateFromFile(typeof(GenerateFieldIds).Assembly.Location)
        );

        var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode);

        return CSharpCompilation.Create(assemblyName, [syntaxTree], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    [Serializable, GenerateSerializer]
    public class DemoData
    {
        [Id(0)]
        public string Value { get; set; } = string.Empty;
    }
}
