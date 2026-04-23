using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

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

        AssertAttributeStepsCachedOrUnchanged(result2);
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

    #region Helpers

    private static CSharpCompilation ReplaceSource(CSharpCompilation compilation, string newSource)
    {
        var newTree = CSharpSyntaxTree.ParseText(newSource);
        return compilation.ReplaceSyntaxTree(compilation.SyntaxTrees.First(), newTree);
    }

    private static async Task<(GeneratorRunResult First, GeneratorRunResult Second)> RunTwice(
        CSharpCompilation firstCompilation,
        CSharpCompilation secondCompilation)
    {
        var generator = new OrleansSerializationSourceGenerator().AsSourceGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [generator],
            driverOptions: new GeneratorDriverOptions(
                disabledOutputs: default,
                trackIncrementalGeneratorSteps: true));

        driver = driver.RunGenerators(firstCompilation);
        var result1 = driver.GetRunResult();
        Assert.Empty(result1.Diagnostics);
        Assert.NotEmpty(result1.Results[0].GeneratedSources);

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

    private static void AssertAttributeStepsCachedOrUnchanged(GeneratorRunResult result)
    {
        var outputSteps = result.TrackedOutputSteps;
        Assert.NotEmpty(outputSteps);

        var attributeStepReasons = outputSteps
            .Where(kvp => kvp.Key.Contains("ForAttributeWithMetadataName", StringComparison.OrdinalIgnoreCase))
            .SelectMany(kvp => kvp.Value)
            .SelectMany(step => step.Outputs)
            .Select(o => o.Reason)
            .ToList();

        if (attributeStepReasons.Count > 0)
        {
            Assert.All(attributeStepReasons, reason =>
                Assert.True(
                    reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged,
                    $"Attribute step had reason '{reason}' — expected Cached or Unchanged."));
        }
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

    private static Task<CSharpCompilation> CreateCompilation(string sourceCode, string assemblyName = "TestProject")
        => TestCompilationHelper.CreateCompilation(sourceCode, assemblyName);

    #endregion
}
