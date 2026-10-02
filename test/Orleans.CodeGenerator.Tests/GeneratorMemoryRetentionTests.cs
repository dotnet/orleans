using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Orleans.CodeGenerator.Model;

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

    [Fact]
    public Task RetainedIdentitiesDoNotRetainCompilationsOrSymbols()
        => VerifyCacheLifetimes(collectDependencies: false);

    [Fact]
    public Task RetainedCollectionsDoNotRetainCompilationsOrSymbols()
        => VerifyCacheLifetimes(collectDependencies: true);

    private static async Task VerifyCacheLifetimes(bool collectDependencies)
    {
        const int compilationCount = 32;
        var template = await TestCompilationHelper.CreateCompilation("internal sealed class Template { }");
        var references = template.References.ToArray();
        var probes = Enumerable.Range(0, compilationCount)
            .Select(index => PopulateMetadataCaches(index, references, collectDependencies))
            .ToArray();

        ForceFullCollection();

        Assert.All(probes, probe =>
        {
            Assert.False(probe.Compilation.IsAlive, "A metadata cache retained a completed compilation.");
            Assert.All(probe.Symbols, symbol =>
                Assert.False(symbol.IsAlive, "A metadata cache retained a source or constructed generic symbol."));
            Assert.Contains(probe.Identities, static identity => identity.MetadataName.EndsWith(".Codec`1", StringComparison.Ordinal));
            if (collectDependencies)
            {
                Assert.Equal(probe.Identities.Length * 2, probe.Collections.Length);
                Assert.Contains(probe.Collections.SelectMany(static collection => collection),
                    static identity => identity.MetadataName.EndsWith(".Outer`1+Nested`1", StringComparison.Ordinal));
            }
        });

        GC.KeepAlive(probes);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static MetadataCacheProbe PopulateMetadataCaches(
        int index,
        MetadataReference[] references,
        bool collectDependencies)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(
            $$"""
            namespace MetadataLifetime{{index}};
            public interface IContract<T> { }
            public class First { }
            public class Second { }
            public class Target<T> : IContract<T> { }
            public class Outer<T>
            {
                public class Nested<TItem> : IContract<Target<TItem>> { }
            }
            public class Codec<T> : IContract<Outer<T>.Nested<Target<T>>> { }
            """,
            cancellationToken: TestContext.Current.CancellationToken);
        var compilation = CSharpCompilation.Create(
            $"MetadataLifetime{index}",
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var definition = compilation.GetTypeByMetadataName($"MetadataLifetime{index}.Codec`1");
        var first = compilation.GetTypeByMetadataName($"MetadataLifetime{index}.First");
        var second = compilation.GetTypeByMetadataName($"MetadataLifetime{index}.Second");
        var outer = compilation.GetTypeByMetadataName($"MetadataLifetime{index}.Outer`1");
        Assert.NotNull(definition);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(outer);

        var closedOuter = outer.Construct(first);
        var nested = Assert.Single(closedOuter.GetTypeMembers("Nested"));
        INamedTypeSymbol[] symbols =
        [
            definition, first, second, outer, closedOuter, nested,
            nested.Construct(second), definition.Construct(first), definition.Construct(second)
        ];
        var identities = symbols.Select(TypeMetadataIdentity.Create).ToArray();
        foreach (var symbol in symbols)
        {
            Assert.Same(TypeMetadataIdentity.Create(symbol).MetadataName, TypeMetadataIdentity.Create(symbol).MetadataName);
        }

        var collections = new List<EquatableArray<TypeMetadataIdentity>>();
        if (collectDependencies)
        {
            foreach (var symbol in symbols)
            {
                foreach (var includeType in new[] { false, true })
                {
                    var metadata = TypeMetadataDependencyCollector.Collect(compilation, symbol, includeType);
                    Assert.True(metadata.Values == TypeMetadataDependencyCollector.Collect(compilation, symbol, includeType).Values);
                    collections.Add(metadata);
                }
            }
        }

        return new MetadataCacheProbe(
            new WeakReference(compilation),
            symbols.Select(static symbol => new WeakReference(symbol)).ToArray(),
            identities,
            collections.ToArray());
    }

    private sealed record MetadataCacheProbe(
        WeakReference Compilation,
        WeakReference[] Symbols,
        TypeMetadataIdentity[] Identities,
        EquatableArray<TypeMetadataIdentity>[] Collections);

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

        var metadata = TypeMetadataDependencyCollector.Collect(compilation, symbol, includeType: true);
        Assert.Contains(metadata, static type => type.MetadataName.EndsWith(".Payload", StringComparison.Ordinal));
        Assert.True(metadata.Values == TypeMetadataDependencyCollector.Collect(compilation, symbol, includeType: true).Values);
        var identity = TypeMetadataIdentity.Create(symbol);
        Assert.Same(identity.MetadataName, TypeMetadataIdentity.Create(symbol).MetadataName);

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
