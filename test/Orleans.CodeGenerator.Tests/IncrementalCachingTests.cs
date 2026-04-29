using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Orleans.CodeGenerator.Diagnostics;

namespace Orleans.CodeGenerator.Tests;

/// <summary>
/// Tests that verify the Orleans incremental source generator correctly caches
/// pipeline outputs when inputs have not changed, avoiding unnecessary regeneration.
/// </summary>
public class IncrementalCachingTests
{
    [Fact]
    public async Task UnchangedSource_ProducesCachedOutput()
    {
        const string code = """
            using Orleans;

            [GenerateSerializer]
            public sealed class MyDto
            {
                [Id(0)]
                public string Name { get; set; }
            }
            """;

        var compilation = await CreateCompilation(code);
        var (_, result2) = await RunTwice(compilation, compilation);

        AssertAllOutputsCachedOrUnchanged(result2);
    }

    [Fact]
    public async Task ChangedSerializableType_TriggersRegeneration()
    {
        const string originalCode = """
            using Orleans;

            [GenerateSerializer]
            public sealed class MyDto
            {
                [Id(0)]
                public string Name { get; set; }
            }
            """;

        const string modifiedCode = """
            using Orleans;

            [GenerateSerializer]
            public sealed class MyDto
            {
                [Id(0)]
                public string Name { get; set; }

                [Id(1)]
                public int Age { get; set; }
            }
            """;

        var compilation = await CreateCompilation(originalCode);
        var newCompilation = ReplaceSource(compilation, modifiedCode);
        var (_, result2) = await RunTwice(compilation, newCompilation);

        AssertAnyOutputModifiedOrNew(result2);
    }

    [Fact]
    public async Task UnrelatedChange_DoesNotTriggerRegeneration()
    {
        const string originalCode = """
            using Orleans;

            [GenerateSerializer]
            public sealed class MyDto
            {
                [Id(0)]
                public string Name { get; set; }
            }
            """;

        const string modifiedCode = """
            using Orleans;

            [GenerateSerializer]
            public sealed class MyDto
            {
                [Id(0)]
                public string Name { get; set; }
            }

            public class UnrelatedClass
            {
                public int Value { get; set; }
            }
            """;

        var compilation = await CreateCompilation(originalCode);
        var newCompilation = ReplaceSource(compilation, modifiedCode);
        var (result1, result2) = await RunTwice(compilation, newCompilation);

        AssertTrackedStepsCachedOrUnchanged(
            result2,
            OrleansSerializationSourceGenerator.SerializableTypeResultsTrackingName,
            OrleansSerializationSourceGenerator.CollectedSerializableTypesTrackingName,
            OrleansSerializationSourceGenerator.SerializerOutputsTrackingName,
            OrleansSerializationSourceGenerator.ReferenceAssemblyDataTrackingName,
            OrleansSerializationSourceGenerator.MetadataAggregateTrackingName,
            OrleansSerializationSourceGenerator.MetadataOutputsTrackingName);
        AssertGeneratedSourcesIdentical(result1, result2);
    }

    [Fact]
    public async Task AddingNewSerializableType_TriggersRegeneration()
    {
        const string originalCode = """
            using Orleans;

            [GenerateSerializer]
            public sealed class TypeA
            {
                [Id(0)]
                public string Name { get; set; }
            }
            """;

        const string modifiedCode = """
            using Orleans;

            [GenerateSerializer]
            public sealed class TypeA
            {
                [Id(0)]
                public string Name { get; set; }
            }

            [GenerateSerializer]
            public sealed class TypeB
            {
                [Id(0)]
                public int Value { get; set; }
            }
            """;

        var compilation = await CreateCompilation(originalCode);
        var newCompilation = ReplaceSource(compilation, modifiedCode);
        var (result1, result2) = await RunTwice(compilation, newCompilation);

        AssertAnyOutputModifiedOrNew(result2);

        // Second run should produce more generated sources than the first
        Assert.True(
            result2.GeneratedSources.Length >= result1.GeneratedSources.Length,
            "Adding a new serializable type should produce at least as many generated sources.");
    }

    [Fact]
    public async Task RemovingSerializableType_TriggersRegeneration()
    {
        const string originalCode = """
            using Orleans;

            [GenerateSerializer]
            public sealed class TypeA
            {
                [Id(0)]
                public string Name { get; set; }
            }

            [GenerateSerializer]
            public sealed class TypeB
            {
                [Id(0)]
                public int Value { get; set; }
            }
            """;

        const string modifiedCode = """
            using Orleans;

            [GenerateSerializer]
            public sealed class TypeA
            {
                [Id(0)]
                public string Name { get; set; }
            }
            """;

        var compilation = await CreateCompilation(originalCode);
        var newCompilation = ReplaceSource(compilation, modifiedCode);
        var (result1, result2) = await RunTwice(compilation, newCompilation);

        AssertAnyOutputModifiedOrNew(result2);
        Assert.True(
            result2.GeneratedSources.Length <= result1.GeneratedSources.Length,
            "Removing a serializable type should produce fewer or equal generated sources.");
    }

    [Fact]
    public async Task ChangedProxyInterface_TriggersRegeneration()
    {
        const string originalCode = """
            using Orleans;
            using System.Threading.Tasks;

            namespace TestProject;

            public interface IMyGrain : IGrainWithIntegerKey
            {
                Task<string> SayHello(string name);
            }
            """;

        const string modifiedCode = """
            using Orleans;
            using System.Threading.Tasks;

            namespace TestProject;

            public interface IMyGrain : IGrainWithIntegerKey
            {
                Task<string> SayHello(string name);
                Task<int> GetCount();
            }
            """;

        var compilation = await CreateCompilation(originalCode);
        var newCompilation = ReplaceSource(compilation, modifiedCode);
        var (_, result2) = await RunTwice(compilation, newCompilation);

        AssertAnyOutputModifiedOrNew(result2);
    }

    [Fact]
    public async Task UnchangedProxyInterface_ProducesCachedOutput()
    {
        const string code = """
            using Orleans;
            using System.Threading.Tasks;

            namespace TestProject;

            public interface IMyGrain : IGrainWithIntegerKey
            {
                Task<string> SayHello(string name);
            }
            """;

        var compilation = await CreateCompilation(code);
        var (_, result2) = await RunTwice(compilation, compilation);

        AssertAllOutputsCachedOrUnchanged(result2);
    }

    [Fact]
    public async Task AddedSyntaxTreeWithoutProxyInterfaces_ProducesIdenticalOutput()
    {
        const string code = """
            using Orleans;
            using System.Threading.Tasks;

            namespace TestProject;

            public interface IMyGrain : IGrainWithIntegerKey
            {
                Task<string> SayHello(string name);
            }
            """;

        const string additionalFile = """
            namespace TestProject;

            public static class Helpers
            {
                public static string Format(string input) => input.ToUpperInvariant();
            }
            """;

        var compilation = await CreateCompilation(code);
        var newCompilation = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(additionalFile));
        var (result1, result2) = await RunTwice(compilation, newCompilation);

        AssertTrackedStepsCachedOrUnchanged(
            result2,
            OrleansSerializationSourceGenerator.InheritedProxyInterfacesTrackingName,
            OrleansSerializationSourceGenerator.CollectedProxyInterfacesTrackingName,
            OrleansSerializationSourceGenerator.PreparedProxyOutputsTrackingName,
            OrleansSerializationSourceGenerator.ProxyOutputsTrackingName,
            OrleansSerializationSourceGenerator.MetadataAggregateTrackingName,
            OrleansSerializationSourceGenerator.MetadataOutputsTrackingName);
        AssertGeneratedSourcesIdentical(result1, result2);
    }

    [Fact]
    public async Task AddedSyntaxTreeWithoutSerializableTypes_ProducesIdenticalOutput()
    {
        const string code = """
            using Orleans;

            [GenerateSerializer]
            public sealed class MyDto
            {
                [Id(0)]
                public string Name { get; set; }
            }
            """;

        const string additionalFile = """
            namespace TestProject;

            public static class Helpers
            {
                public static string Format(string input) => input.ToUpperInvariant();
            }
            """;

        var compilation = await CreateCompilation(code);
        var newCompilation = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(additionalFile));
        var (result1, result2) = await RunTwice(compilation, newCompilation);

        AssertTrackedStepsCachedOrUnchanged(
            result2,
            OrleansSerializationSourceGenerator.SerializableTypeResultsTrackingName,
            OrleansSerializationSourceGenerator.CollectedSerializableTypesTrackingName,
            OrleansSerializationSourceGenerator.SerializerOutputsTrackingName,
            OrleansSerializationSourceGenerator.ReferenceAssemblyDataTrackingName,
            OrleansSerializationSourceGenerator.MetadataAggregateTrackingName,
            OrleansSerializationSourceGenerator.MetadataOutputsTrackingName);
        AssertGeneratedSourcesIdentical(result1, result2);
    }

    [Fact]
    public async Task MixedSerializableAndProxy_BothCachedWhenUnchanged()
    {
        const string code = """
            using Orleans;
            using System.Threading.Tasks;

            namespace TestProject;

            [GenerateSerializer]
            public sealed class MyDto
            {
                [Id(0)]
                public string Name { get; set; }
            }

            public interface IMyGrain : IGrainWithIntegerKey
            {
                Task<MyDto> GetDto();
            }
            """;

        var compilation = await CreateCompilation(code);
        var (_, result2) = await RunTwice(compilation, compilation);

        AssertAllOutputsCachedOrUnchanged(result2);
    }

    [Fact]
    public async Task MixedSerializableAndProxy_OnlySerializableChanged_ProducesModifiedOutput()
    {
        const string originalCode = """
            using Orleans;
            using System.Threading.Tasks;

            namespace TestProject;

            [GenerateSerializer]
            public sealed class MyDto
            {
                [Id(0)]
                public string Name { get; set; }
            }

            public interface IMyGrain : IGrainWithIntegerKey
            {
                Task<MyDto> GetDto();
            }
            """;

        const string modifiedCode = """
            using Orleans;
            using System.Threading.Tasks;

            namespace TestProject;

            [GenerateSerializer]
            public sealed class MyDto
            {
                [Id(0)]
                public string Name { get; set; }

                [Id(1)]
                public int Age { get; set; }
            }

            public interface IMyGrain : IGrainWithIntegerKey
            {
                Task<MyDto> GetDto();
            }
            """;

        var compilation = await CreateCompilation(originalCode);
        var newCompilation = ReplaceSource(compilation, modifiedCode);
        var (_, result2) = await RunTwice(compilation, newCompilation);

        AssertAnyOutputModifiedOrNew(result2);
    }

    [Fact]
    public async Task MixedSerializableAndProxy_OnlySerializableChanged_LeavesProxyOutputsUnchanged()
    {
        const string originalCode = """
            using Orleans;
            using System.Threading.Tasks;

            namespace TestProject;

            [GenerateSerializer]
            public sealed class MyDto
            {
                [Id(0)]
                public string Name { get; set; }
            }

            public interface IMyGrain : IGrainWithIntegerKey
            {
                Task<MyDto> GetDto();
            }
            """;

        const string modifiedCode = """
            using Orleans;
            using System.Threading.Tasks;

            namespace TestProject;

            [GenerateSerializer]
            public sealed class MyDto
            {
                [Id(0)]
                public string Name { get; set; }

                [Id(1)]
                public int Age { get; set; }
            }

            public interface IMyGrain : IGrainWithIntegerKey
            {
                Task<MyDto> GetDto();
            }
            """;

        var compilation = await CreateCompilation(originalCode);
        var newCompilation = ReplaceSource(compilation, modifiedCode);
        var (result1, result2) = await RunTwice(compilation, newCompilation);

        AssertSourcesChanged(result1, result2, static hint => hint.Contains(".orleans.ser.", StringComparison.Ordinal));
        AssertSourcesUnchanged(result1, result2, static hint => hint.Contains(".orleans.proxy.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MixedSerializableAndProxy_OnlyProxyChanged_LeavesSerializerOutputsUnchanged()
    {
        const string originalCode = """
            using Orleans;
            using System.Threading.Tasks;

            namespace TestProject;

            [GenerateSerializer]
            public sealed class MyDto
            {
                [Id(0)]
                public string Name { get; set; }
            }

            public interface IMyGrain : IGrainWithIntegerKey
            {
                Task<MyDto> GetDto();
            }
            """;

        const string modifiedCode = """
            using Orleans;
            using System.Threading.Tasks;

            namespace TestProject;

            [GenerateSerializer]
            public sealed class MyDto
            {
                [Id(0)]
                public string Name { get; set; }
            }

            public interface IMyGrain : IGrainWithIntegerKey
            {
                Task<MyDto> GetDto();
                Task Ping();
            }
            """;

        var compilation = await CreateCompilation(originalCode);
        var newCompilation = ReplaceSource(compilation, modifiedCode);
        var (result1, result2) = await RunTwice(compilation, newCompilation);

        AssertSourcesChanged(result1, result2, static hint => hint.Contains(".orleans.proxy.", StringComparison.Ordinal));
        AssertSourcesUnchanged(result1, result2, static hint => hint.Contains(".orleans.ser.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChangedReferenceAssembly_InvalidatesReferenceAssemblyPipelineAndDropsStaleOutputs()
    {
        const string libraryV1Code = """
            using Orleans;

            namespace LibraryProject;

            public sealed class Marker
            {
            }

            [GenerateSerializer]
            public sealed class ReferencedDto
            {
                [Id(0)]
                public string LegacyValue { get; set; } = string.Empty;
            }
            """;

        const string libraryV2Code = """
            using Orleans;

            namespace LibraryProject;

            public sealed class Marker
            {
            }

            [GenerateSerializer]
            public sealed class ReferencedDto
            {
                [Id(0)]
                public string CurrentValue { get; set; } = string.Empty;

                [Id(1)]
                public int Version { get; set; }
            }
            """;

        const string consumerCode = """
            using Orleans;

            [assembly: GenerateCodeForDeclaringAssembly(typeof(LibraryProject.Marker))]
            """;

        var consumerV1 = await CreateConsumerCompilationWithLibrary(libraryV1Code, consumerCode);
        var consumerV2 = await CreateConsumerCompilationWithLibrary(libraryV2Code, consumerCode);
        var (result1, result2) = await RunTwice(consumerV1, consumerV2);

        Assert.Empty(result1.Diagnostics);
        Assert.Empty(result2.Diagnostics);
        AssertTrackedStepModifiedOrNew(result2, OrleansSerializationSourceGenerator.ReferenceAssemblyDataTrackingName);
        AssertTrackedStepModifiedOrNew(result2, OrleansSerializationSourceGenerator.ReferencedSerializerOutputsTrackingName);

        var firstGeneratedSource = ConcatenateGeneratedSources(result1);
        var secondGeneratedSource = ConcatenateGeneratedSources(result2);
        Assert.Contains("LegacyValue", firstGeneratedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentValue", firstGeneratedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("LegacyValue", secondGeneratedSource, StringComparison.Ordinal);
        Assert.Contains("CurrentValue", secondGeneratedSource, StringComparison.Ordinal);
        Assert.Contains("Version", secondGeneratedSource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SameDriverSameCompilation_ProducesIdenticalDiagnosticsAndSources()
    {
        const string code = """
            using Orleans;
            using System.Threading.Tasks;

            namespace TestProject;

            [GenerateSerializer]
            public sealed class StableDto
            {
                [Id(0)]
                public string Name { get; set; } = string.Empty;
            }

            public interface IStableGrain : IGrainWithIntegerKey
            {
                Task<StableDto> Get();
            }
            """;

        var compilation = await CreateCompilation(code);
        var (result1, result2) = await RunTwice(compilation, compilation);

        AssertNoDuplicateHintNames(result1);
        AssertNoDuplicateHintNames(result2);
        AssertDiagnosticsIdentical(result1.Diagnostics, result2.Diagnostics);
        AssertGeneratedSourcesIdentical(result1, result2);
        AssertAllOutputsCachedOrUnchanged(result2);
    }

    [Fact]
    public async Task UnrelatedAnalyzerConfigOption_DoesNotInvalidateGeneratedModels()
    {
        const string code = """
            using Orleans;
            using System.Threading.Tasks;

            namespace TestProject;

            [GenerateSerializer]
            public sealed class StableDto
            {
                [Id(0)]
                public string Name { get; set; } = string.Empty;
            }

            public interface IStableGrain : IGrainWithIntegerKey
            {
                Task<StableDto> Get();
            }
            """;

        var compilation = await CreateCompilation(code);
        var (result1, result2) = await RunTwice(
            compilation,
            compilation,
            new Dictionary<string, string>
            {
                ["build_property.unrelated_option"] = "before",
            },
            new Dictionary<string, string>
            {
                ["build_property.unrelated_option"] = "after",
            });

        Assert.Empty(result1.Diagnostics);
        Assert.Empty(result2.Diagnostics);
        AssertGeneratedSourcesIdentical(result1, result2);
        AssertTrackedStepsCachedOrUnchanged(
            result2,
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
    }

    [Fact]
    public async Task GenerateFieldIdsOption_ChangesSerializerDiagnosticsWithoutChangingProxyOutputs()
    {
        const string code = """
            using Orleans;
            using System.Threading.Tasks;

            namespace TestProject;

            [GenerateSerializer]
            public sealed class OptionDto
            {
                public string Name { get; set; } = string.Empty;
                public int Age { get; set; }
            }

            public interface IOptionGrain : IGrainWithIntegerKey
            {
                Task Ping();
            }
            """;

        var compilation = await CreateCompilation(code);
        var (baselineResult, configuredResult) = await RunTwice(
            compilation,
            compilation,
            firstGlobalOptions: null,
            secondGlobalOptions: new Dictionary<string, string>
            {
                ["build_property.orleans_generatefieldids"] = "PublicProperties",
            });

        Assert.Contains(baselineResult.Diagnostics, diagnostic => diagnostic.Id == DiagnosticRuleId.CanNotGenerateImplicitFieldIds);
        Assert.Empty(configuredResult.Diagnostics);
        Assert.Contains(configuredResult.GeneratedSources, static source => source.HintName.Contains(".orleans.ser.", StringComparison.Ordinal));
        AssertSourcesUnchanged(baselineResult, configuredResult, static hint => hint.Contains(".orleans.proxy.", StringComparison.Ordinal));
        AssertTrackedStepModifiedOrNew(configuredResult, OrleansSerializationSourceGenerator.SerializableTypeResultsTrackingName);
        AssertTrackedStepModifiedOrNew(configuredResult, OrleansSerializationSourceGenerator.SerializerOutputsTrackingName);
    }

    [Fact]
    public async Task CompatibilityInvokersOption_ChangesProxyOutputsWithoutChangingSerializerOutputs()
    {
        const string code = """
            using Orleans;
            using System.Threading.Tasks;

            namespace TestProject;

            [GenerateSerializer]
            public sealed class StableDto
            {
                [Id(0)]
                public string Name { get; set; } = string.Empty;
            }

            public interface IBaseOptionGrain : IGrainWithIntegerKey
            {
                Task<StableDto> Get();
            }

            public interface IDerivedOptionGrain : IBaseOptionGrain
            {
            }
            """;

        var compilation = await CreateCompilation(code);
        var (baselineResult, configuredResult) = await RunTwice(
            compilation,
            compilation,
            firstGlobalOptions: null,
            secondGlobalOptions: new Dictionary<string, string>
            {
                ["build_property.orleansgeneratecompatibilityinvokers"] = "true",
            });

        Assert.Empty(baselineResult.Diagnostics);
        Assert.Empty(configuredResult.Diagnostics);
        AssertSourcesUnchanged(baselineResult, configuredResult, static hint => hint.Contains(".orleans.ser.", StringComparison.Ordinal));
        AssertSourcesChanged(baselineResult, configuredResult, static hint => hint.Contains(".orleans.proxy.", StringComparison.Ordinal));
        AssertTrackedStepModifiedOrNew(configuredResult, OrleansSerializationSourceGenerator.PreparedProxyOutputsTrackingName);
        AssertTrackedStepModifiedOrNew(configuredResult, OrleansSerializationSourceGenerator.ProxyOutputsTrackingName);
    }

    #region Helpers

    private static CSharpCompilation ReplaceSource(CSharpCompilation compilation, string newSource)
    {
        var newTree = CSharpSyntaxTree.ParseText(newSource);
        return compilation.ReplaceSyntaxTree(compilation.SyntaxTrees.First(), newTree);
    }

    private static async Task<(GeneratorRunResult First, GeneratorRunResult Second)> RunTwice(
        CSharpCompilation firstCompilation,
        CSharpCompilation secondCompilation,
        IReadOnlyDictionary<string, string>? firstGlobalOptions = null,
        IReadOnlyDictionary<string, string>? secondGlobalOptions = null)
    {
        var generator = new OrleansSerializationSourceGenerator().AsSourceGenerator();
        var hasOptions = firstGlobalOptions is not null || secondGlobalOptions is not null;
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [generator],
            optionsProvider: hasOptions ? new TestAnalyzerConfigOptionsProvider(firstGlobalOptions) : null,
            driverOptions: new GeneratorDriverOptions(
                disabledOutputs: default,
                trackIncrementalGeneratorSteps: true));

        driver = driver.RunGenerators(firstCompilation);
        var result1 = driver.GetRunResult();
        Assert.NotEmpty(result1.Results[0].GeneratedSources);

        if (hasOptions)
        {
            driver = driver.WithUpdatedAnalyzerConfigOptions(new TestAnalyzerConfigOptionsProvider(secondGlobalOptions));
        }

        driver = driver.RunGenerators(secondCompilation);
        var result2 = driver.GetRunResult();

        await Task.CompletedTask;
        return (result1.Results[0], result2.Results[0]);
    }

    private static void AssertAllOutputsCachedOrUnchanged(GeneratorRunResult result)
    {
        var outputSteps = result.TrackedOutputSteps;
        Assert.NotEmpty(outputSteps);

        foreach (var (stepName, steps) in outputSteps)
        {
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

    private static void AssertAnyOutputModifiedOrNew(GeneratorRunResult result)
    {
        var outputSteps = result.TrackedOutputSteps;
        Assert.NotEmpty(outputSteps);

        var allReasons = outputSteps
            .SelectMany(kvp => kvp.Value)
            .SelectMany(step => step.Outputs)
            .Select(o => o.Reason)
            .ToList();

        Assert.Contains(allReasons, reason =>
            reason is IncrementalStepRunReason.Modified or IncrementalStepRunReason.New);
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

    private static void AssertTrackedStepModifiedOrNew(GeneratorRunResult result, string stepName)
    {
        var trackedSteps = result.TrackedSteps;
        Assert.NotEmpty(trackedSteps);
        Assert.True(trackedSteps.TryGetValue(stepName, out var steps), $"Missing tracked step '{stepName}'.");

        var reasons = steps
            .SelectMany(static step => step.Outputs)
            .Select(static output => output.Reason)
            .ToArray();
        Assert.Contains(reasons, static reason => reason is IncrementalStepRunReason.Modified or IncrementalStepRunReason.New);
    }

    private static void AssertGeneratedSourcesIdentical(GeneratorRunResult result1, GeneratorRunResult result2)
    {
        Assert.Equal(result1.GeneratedSources.Length, result2.GeneratedSources.Length);

        var sources1 = result1.GeneratedSources.OrderBy(s => s.HintName).ToList();
        var sources2 = result2.GeneratedSources.OrderBy(s => s.HintName).ToList();

        for (int i = 0; i < sources1.Count; i++)
        {
            Assert.Equal(sources1[i].HintName, sources2[i].HintName);
            Assert.Equal(sources1[i].SourceText.ToString(), sources2[i].SourceText.ToString());
        }
    }

    private static void AssertDiagnosticsIdentical(IEnumerable<Diagnostic> diagnostics, IEnumerable<Diagnostic> otherDiagnostics)
        => Assert.Equal(
            diagnostics.Select(GetDiagnosticShape).OrderBy(static value => value, StringComparer.Ordinal),
            otherDiagnostics.Select(GetDiagnosticShape).OrderBy(static value => value, StringComparer.Ordinal));

    private static string GetDiagnosticShape(Diagnostic diagnostic)
    {
        var lineSpan = diagnostic.Location.GetLineSpan();
        return string.Join(
            "|",
            diagnostic.Id,
            diagnostic.Severity.ToString(),
            diagnostic.GetMessage(),
            lineSpan.Path ?? string.Empty,
            lineSpan.StartLinePosition.Line.ToString(),
            lineSpan.StartLinePosition.Character.ToString());
    }

    private static void AssertNoDuplicateHintNames(GeneratorRunResult result)
    {
        var duplicateHintNames = result.GeneratedSources
            .GroupBy(static source => source.HintName, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToArray();

        Assert.True(duplicateHintNames.Length == 0, $"Duplicate generated source hint names: {string.Join(", ", duplicateHintNames)}");
    }

    private static void AssertSourcesChanged(
        GeneratorRunResult result1,
        GeneratorRunResult result2,
        Func<string, bool> predicate)
    {
        var sourceMap1 = GetGeneratedSourceMap(result1);
        var sourceMap2 = GetGeneratedSourceMap(result2);
        var matchingHints = sourceMap1.Keys.Intersect(sourceMap2.Keys).Where(predicate).ToList();

        Assert.NotEmpty(matchingHints);
        Assert.Contains(matchingHints, hint => !string.Equals(sourceMap1[hint], sourceMap2[hint], StringComparison.Ordinal));
    }

    private static void AssertSourcesUnchanged(
        GeneratorRunResult result1,
        GeneratorRunResult result2,
        Func<string, bool> predicate)
    {
        var sourceMap1 = GetGeneratedSourceMap(result1);
        var sourceMap2 = GetGeneratedSourceMap(result2);
        var matchingHints = sourceMap1.Keys.Intersect(sourceMap2.Keys).Where(predicate).ToList();

        Assert.NotEmpty(matchingHints);
        Assert.All(matchingHints, hint => Assert.Equal(sourceMap1[hint], sourceMap2[hint]));
    }

    private static Dictionary<string, string> GetGeneratedSourceMap(GeneratorRunResult result)
        => result.GeneratedSources.ToDictionary(source => source.HintName, source => source.SourceText.ToString(), StringComparer.Ordinal);

    private static string ConcatenateGeneratedSources(GeneratorRunResult result)
        => string.Join(
            Environment.NewLine,
            result.GeneratedSources
                .OrderBy(static source => source.HintName, StringComparer.Ordinal)
                .Select(static source => source.SourceText.ToString()));

    private static async Task<CSharpCompilation> CreateConsumerCompilationWithLibrary(
        string libraryCode,
        string consumerCode)
    {
        var libraryCompilation = await CreateCompilation(libraryCode, "LibraryProject");
        Assert.Empty(libraryCompilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        var consumerCompilation = await TestCompilationHelper.CreateCompilation(
            consumerCode,
            "ConsumerProject",
            libraryCompilation.ToMetadataReference());
        Assert.Empty(consumerCompilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        return consumerCompilation;
    }

    private static Task<CSharpCompilation> CreateCompilation(string sourceCode, string assemblyName = "TestProject")
        => TestCompilationHelper.CreateCompilation(sourceCode, assemblyName);

    private sealed class TestAnalyzerConfigOptionsProvider : AnalyzerConfigOptionsProvider
    {
        private static readonly AnalyzerConfigOptions EmptyOptions = new TestAnalyzerConfigOptions(new Dictionary<string, string>());
        private readonly AnalyzerConfigOptions _globalOptions;

        public TestAnalyzerConfigOptionsProvider(IReadOnlyDictionary<string, string>? globalOptions)
        {
            _globalOptions = new TestAnalyzerConfigOptions(globalOptions ?? new Dictionary<string, string>());
        }

        public override AnalyzerConfigOptions GlobalOptions => _globalOptions;

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => EmptyOptions;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => EmptyOptions;
    }

    private sealed class TestAnalyzerConfigOptions : AnalyzerConfigOptions
    {
        private readonly IReadOnlyDictionary<string, string> _options;

        public TestAnalyzerConfigOptions(IReadOnlyDictionary<string, string> options)
        {
            _options = options;
        }

        public override bool TryGetValue(string key, out string value) => _options.TryGetValue(key, out value!);
    }

    #endregion
}
