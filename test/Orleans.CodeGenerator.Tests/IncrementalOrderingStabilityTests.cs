using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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

    [Fact]
    public async Task ReorderedInterfaceInheritanceGraph_ProducesIdenticalProxyAndMetadata()
    {
        const string featureInterfaces = """
            using System.Threading.Tasks;

            namespace TestProject;

            public interface IFirstOrderingFeature
            {
                Task First();
            }

            public interface ISecondOrderingFeature
            {
                Task Second();
            }
            """;

        const string derivedInterface = """
            using Orleans;
            using System.Threading.Tasks;

            namespace TestProject;

            public interface ICompositeOrderingGrain : IGrainWithIntegerKey, IFirstOrderingFeature, ISecondOrderingFeature
            {
                Task Own();
            }
            """;

        const string reorderedDerivedInterface = """
            using Orleans;
            using System.Threading.Tasks;

            namespace TestProject;

            public interface ICompositeOrderingGrain : IGrainWithIntegerKey, IFirstOrderingFeature, ISecondOrderingFeature
            {
                Task Own();
            }
            """;

        var compilation = await CreateCompilation(
            "OrderingProject",
            featureInterfaces,
            derivedInterface);
        var reorderedCompilation = await CreateCompilation(
            "OrderingProject",
            reorderedDerivedInterface,
            featureInterfaces);

        var result = RunGenerator(compilation);
        var reorderedResult = RunGenerator(reorderedCompilation);

        AssertGeneratedSourcesIdentical(result, reorderedResult);
        Assert.Contains(GetGeneratedSourceMap(result).Keys, static hint => hint.Contains(".orleans.proxy.", StringComparison.Ordinal));
        Assert.Contains(GetGeneratedSourceMap(result).Keys, static hint => hint.EndsWith(".orleans.metadata.g.cs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReorderedMetadataInputsWithAliasesCodecsAndApplicationParts_ProducesIdenticalMetadata()
    {
        var compilation = await CreateMetadataStabilityCompilation(reverseReferenceOrder: false);
        var reorderedCompilation = await CreateMetadataStabilityCompilation(reverseReferenceOrder: true);

        var result = RunGenerator(compilation);
        var reorderedResult = RunGenerator(reorderedCompilation);

        var metadataSource = GetMetadataSource(result);
        var reorderedMetadataSource = GetMetadataSource(reorderedResult);
        Assert.Equal(metadataSource, reorderedMetadataSource);
        Assert.Equal(1, CountOccurrences(metadataSource, "WellKnownTypeAliases.Add(\"A.Alias\""));
        Assert.Equal(1, CountOccurrences(metadataSource, "WellKnownTypeAliases.Add(\"B.Alias\""));
        Assert.Equal(1, CountOccurrences(metadataSource, "Serializers.Add(typeof(global::LibraryB.SerializerType))"));
        Assert.Equal(1, CountOccurrences(metadataSource, "Copiers.Add(typeof(global::LibraryB.CopierType))"));
        Assert.Equal(1, CountOccurrences(metadataSource, "Activators.Add(typeof(global::LibraryB.ActivatorType))"));
        Assert.Equal(1, CountOccurrences(metadataSource, "Converters.Add(typeof(global::LibraryB.ConverterType))"));
        Assert.Equal(1, CountOccurrences(metadataSource, "InterfaceImplementations.Add(typeof(global::LibraryB.GeneratedInterfaceImplementation))"));
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

    private static async Task<CSharpCompilation> CreateMetadataStabilityCompilation(bool reverseReferenceOrder)
    {
        const string libraryBCode = """
            using Orleans;
            using Orleans.Runtime;
            using System.Threading.Tasks;

            namespace LibraryB;

            [Id(200)]
            [Alias("B.Alias")]
            [CompoundTypeAlias("B", typeof(LibraryB.BetaType))]
            public sealed class BetaType
            {
            }

            [RegisterSerializer]
            public sealed class SerializerType
            {
            }

            [RegisterCopier]
            public sealed class CopierType
            {
            }

            [RegisterActivator]
            public sealed class ActivatorType
            {
            }

            [RegisterConverter]
            public sealed class ConverterType
            {
            }

            [GenerateMethodSerializers(typeof(GrainReference))]
            public interface IGeneratedInterface
            {
                Task Ping();
            }

            public sealed class GeneratedInterfaceImplementation : IGeneratedInterface
            {
                public Task Ping() => Task.CompletedTask;
            }
            """;

        const string libraryACode = """
            using Orleans;
            using LibraryB;

            [assembly: ApplicationPart("Zeta.Part")]
            [assembly: ApplicationPart("Alpha.Part")]
            [assembly: GenerateCodeForDeclaringAssembly(typeof(LibraryB.BetaType))]

            namespace LibraryA;

            [Id(100)]
            [Alias("A.Alias")]
            public sealed class AlphaType
            {
            }
            """;

        const string consumerCode = """
            using Orleans;

            [assembly: GenerateCodeForDeclaringAssembly(typeof(LibraryA.AlphaType))]

            namespace ConsumerProject;

            public sealed class ConsumerMarker
            {
            }
            """;

        var libraryBCompilation = await TestCompilationHelper.CreateCompilation(libraryBCode, "LibraryB");
        Assert.Empty(libraryBCompilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        var libraryACompilation = await TestCompilationHelper.CreateCompilation(
            libraryACode,
            "LibraryA",
            libraryBCompilation.ToMetadataReference());
        Assert.Empty(libraryACompilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        var libraryAReference = libraryACompilation.ToMetadataReference();
        var libraryBReference = libraryBCompilation.ToMetadataReference();
        var consumerCompilation = reverseReferenceOrder
            ? await TestCompilationHelper.CreateCompilation(consumerCode, "ConsumerProject", libraryBReference, libraryAReference)
            : await TestCompilationHelper.CreateCompilation(consumerCode, "ConsumerProject", libraryAReference, libraryBReference);

        Assert.Empty(consumerCompilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        return consumerCompilation;
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

    private static string GetMetadataSource(GeneratorRunResult result)
    {
        var source = Assert.Single(result.GeneratedSources, static source => source.HintName.EndsWith(".orleans.metadata.g.cs", StringComparison.Ordinal));
        return CSharpSyntaxTree.ParseText(source.SourceText.ToString().TrimStart('\uFEFF')).GetCompilationUnitRoot().NormalizeWhitespace().ToFullString();
    }

    private static int CountOccurrences(string value, string substring)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(substring, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += substring.Length;
        }

        return count;
    }
}
