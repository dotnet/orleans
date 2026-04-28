using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Orleans.CodeGenerator.Tests;

/// <summary>
/// Characterization tests for order-sensitive incremental source generator stability.
/// </summary>
public class IncrementalOrderingStabilityTests
{
    private const string SerializableTypeA = """
        using Orleans;

        namespace TestProject;

        [GenerateSerializer]
        public sealed class OrderingDtoA
        {
            [Id(0)]
            public string Name { get; set; }
        }
        """;

    private const string SerializableTypeB = """
        using Orleans;

        namespace TestProject;

        [GenerateSerializer]
        public sealed class OrderingDtoB
        {
            [Id(0)]
            public int Value { get; set; }
        }
        """;

    private const string ProxyInterface = """
        using Orleans;
        using System.Threading.Tasks;

        namespace TestProject;

        public interface IOrderingGrain : IGrainWithIntegerKey
        {
            Task<OrderingDtoA> GetA();
            Task<OrderingDtoB> GetB();
        }
        """;

    private const string UnrelatedType = """
        namespace TestProject;

        public sealed class UnrelatedOrderingType
        {
            public string Format(string value) => value.ToUpperInvariant();
        }
        """;

    [Fact]
    public async Task ReorderedSyntaxTreesWithSameGeneratorTargets_ProducesIdenticalGeneratedSources()
    {
        var compilation = await CreateCompilation(
            "OrderingProject",
            SerializableTypeA,
            ProxyInterface,
            SerializableTypeB);
        var reorderedCompilation = await CreateCompilation(
            "OrderingProject",
            SerializableTypeB,
            SerializableTypeA,
            ProxyInterface);

        var result = RunGenerator(compilation);
        var reorderedResult = RunGenerator(reorderedCompilation);

        AssertGeneratedSourcesIdentical(result, reorderedResult);
    }

    [Fact]
    public async Task AddingUnrelatedSyntaxTree_PreservesGeneratedSourcesAndCachesStableSteps()
    {
        var compilation = await CreateCompilation(
            "OrderingProject",
            SerializableTypeA,
            ProxyInterface,
            SerializableTypeB);
        var updatedCompilation = compilation.AddSyntaxTrees(ParseSource(UnrelatedType));

        var (result, updatedResult) = RunTwice(compilation, updatedCompilation);

        AssertTrackedStepsCachedOrUnchanged(
            updatedResult,
            OrleansSerializationSourceGenerator.SerializableTypeResultsTrackingName,
            OrleansSerializationSourceGenerator.CollectedSerializableTypesTrackingName,
            OrleansSerializationSourceGenerator.SerializerOutputsTrackingName,
            OrleansSerializationSourceGenerator.InheritedProxyInterfacesTrackingName,
            OrleansSerializationSourceGenerator.CollectedProxyInterfacesTrackingName,
            OrleansSerializationSourceGenerator.PreparedProxyOutputsTrackingName,
            OrleansSerializationSourceGenerator.ProxyOutputsTrackingName,
            OrleansSerializationSourceGenerator.ReferenceAssemblyDataTrackingName,
            OrleansSerializationSourceGenerator.MetadataAggregateTrackingName,
            OrleansSerializationSourceGenerator.MetadataOutputsTrackingName);
        AssertGeneratedSourcesIdentical(result, updatedResult);
    }

    private static async Task<CSharpCompilation> CreateCompilation(string assemblyName, params string[] sources)
    {
        Assert.NotEmpty(sources);

        var compilation = await TestCompilationHelper.CreateCompilation(sources[0], assemblyName);
        if (sources.Length == 1)
        {
            return compilation;
        }

        return compilation.AddSyntaxTrees(sources.Skip(1).Select(ParseSource));
    }

    private static SyntaxTree ParseSource(string source)
        => CSharpSyntaxTree.ParseText(source);

    private static GeneratorRunResult RunGenerator(Compilation compilation)
    {
        var generator = new OrleansSerializationSourceGenerator().AsSourceGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [generator],
            driverOptions: new GeneratorDriverOptions(
                disabledOutputs: default,
                trackIncrementalGeneratorSteps: true));

        driver = driver.RunGenerators(compilation);
        var runResult = driver.GetRunResult();
        Assert.Empty(runResult.Diagnostics);

        var result = Assert.Single(runResult.Results);
        Assert.Empty(result.Diagnostics);
        Assert.NotEmpty(result.GeneratedSources);

        return result;
    }

    private static (GeneratorRunResult First, GeneratorRunResult Second) RunTwice(
        Compilation firstCompilation,
        Compilation secondCompilation)
    {
        var generator = new OrleansSerializationSourceGenerator().AsSourceGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [generator],
            driverOptions: new GeneratorDriverOptions(
                disabledOutputs: default,
                trackIncrementalGeneratorSteps: true));

        driver = driver.RunGenerators(firstCompilation);
        var result1 = GetSingleGeneratorResult(driver);

        driver = driver.RunGenerators(secondCompilation);
        var result2 = GetSingleGeneratorResult(driver);

        return (result1, result2);
    }

    private static GeneratorRunResult GetSingleGeneratorResult(GeneratorDriver driver)
    {
        var runResult = driver.GetRunResult();
        Assert.Empty(runResult.Diagnostics);

        var result = Assert.Single(runResult.Results);
        Assert.Empty(result.Diagnostics);
        Assert.NotEmpty(result.GeneratedSources);

        return result;
    }

    private static void AssertTrackedStepsCachedOrUnchanged(GeneratorRunResult result, params string[] stepNames)
    {
        var trackedSteps = result.TrackedSteps;
        Assert.NotEmpty(trackedSteps);

        foreach (var stepName in stepNames)
        {
            Assert.True(trackedSteps.TryGetValue(stepName, out var steps), $"Missing tracked step '{stepName}'.");
            Assert.NotEmpty(steps);

            foreach (var step in steps)
            {
                foreach (var (_, reason) in step.Outputs)
                {
                    Assert.True(
                        reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged,
                        $"Step '{stepName}' had reason '{reason}' — expected Cached or Unchanged.");
                }
            }
        }
    }

    private static void AssertGeneratedSourcesIdentical(GeneratorRunResult result, GeneratorRunResult other)
    {
        var sources = GetGeneratedSourceMap(result);
        var otherSources = GetGeneratedSourceMap(other);

        Assert.Equal(sources.Count, otherSources.Count);

        foreach (var (hintName, sourceText) in sources)
        {
            Assert.True(otherSources.TryGetValue(hintName, out var otherSourceText), $"Missing generated source '{hintName}'.");
            Assert.Equal(sourceText, otherSourceText);
        }
    }

    private static SortedDictionary<string, string> GetGeneratedSourceMap(GeneratorRunResult result)
        => new(
            result.GeneratedSources.ToDictionary(source => source.HintName, source => source.SourceText.ToString(), StringComparer.Ordinal),
            StringComparer.Ordinal);
}
