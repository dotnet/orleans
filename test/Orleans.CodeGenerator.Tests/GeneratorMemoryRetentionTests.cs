using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Orleans.CodeGenerator.Tests;

public class GeneratorMemoryRetentionTests
{
    [Fact]
    public async Task CompletedGeneratorRunsDoNotRetainCompilations()
    {
        const int compilationCount = 8;
        var template = await TestCompilationHelper.CreateCompilation("internal sealed class Template { }");
        var references = template.References.ToArray();
        var weakReferences = await Task.WhenAll(
            Enumerable.Range(0, compilationCount)
                .Select(index => Task.Run(() => RunGenerator(index, references))));

        ForceFullCollection();

        Assert.Equal(0, weakReferences.Count(static references => references.Compilation.IsAlive));
        Assert.Equal(0, weakReferences.Count(static references => references.Symbol.IsAlive));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Compilation, WeakReference Symbol) RunGenerator(
        int index,
        MetadataReference[] references)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(
            $$"""
            using Orleans;

            namespace RetentionTest{{index}};

            [GenerateSerializer]
            public sealed class Payload
            {
                [Id(0)]
                public Payload? Next { get; set; }
            }
            """);
        var compilation = CSharpCompilation.Create(
            $"RetentionTest{index}",
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var symbol = compilation.GetTypeByMetadataName($"RetentionTest{index}.Payload");
        Assert.NotNull(symbol);

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new OrleansSerializationSourceGenerator().AsSourceGenerator()]);
        driver = driver.RunGenerators(compilation);
        var result = driver.GetRunResult();
        Assert.Empty(result.Diagnostics);
        Assert.NotEmpty(Assert.Single(result.Results).GeneratedSources);

        return (new(compilation), new(symbol));
    }

    private static void ForceFullCollection()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }
}
