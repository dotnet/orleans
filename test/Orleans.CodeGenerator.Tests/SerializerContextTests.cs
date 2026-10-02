using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Orleans.CodeGenerator;
using Orleans.Serialization.ContextSmoke;
using Xunit;

namespace Orleans.CodeGenerator.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("CodeGen")]
public sealed class SerializerContextTests
{
    [Fact] public void NestedCollectionsRoundTripAndCopy() => ContextContracts.NestedCollectionsRoundTripAndCopy();
    [Fact] public void GeneratedFactoriesComposeWithMetadataAndReflection() => ContextContracts.GeneratedFactoriesComposeWithMetadataAndReflection();
    [Fact] public void GeneratedModelsTraverseDependencies() => ContextContracts.GeneratedModelsTraverseDependencies();
    [Fact] public void CanonicalValueSerializerUsesGeneratedCodec() => ContextContracts.CanonicalValueSerializerUsesGeneratedCodec();
    [Fact] public void GenericArraysRoundTripAndCopy() => ContextContracts.GenericArraysRoundTripAndCopy();
    [Fact] public void GenericArrayCyclesPreserveIdentity() => ContextContracts.GenericArrayCyclesPreserveIdentity();
    [Fact] public void NullableRootCyclesPreserveCopyIdentity() => ContextContracts.NullableRootCyclesPreserveCopyIdentity();
    [Fact] public void NullableRootFailureRollsBackAndRetriesCanonically() => ContextContracts.NullableRootFailureRollsBackAndRetriesCanonically();
#if NET10_0_OR_GREATER
    [Fact] public void ReferencedGeneratedModelsTraverseDependencies() => ContextContracts.ReferencedGeneratedModelsTraverseDependencies();
#endif
    [Fact] public void NullableArrayAndEnumRoundTrip() => ContextContracts.NullableArrayAndEnumRoundTrip();
    [Fact] public void RecursiveModelsPreserveIdentity() => ContextContracts.RecursiveModelsPreserveIdentity();
    [Fact] public void MutualRecursionPreservesIdentity() => ContextContracts.MutualRecursionPreservesIdentity();
    [Fact] public void CollectionRecursionPreservesIdentity() => ContextContracts.CollectionRecursionPreservesIdentity();
    [Fact] public void DuplicateContextsAndConcurrentResolution() => ContextContracts.DuplicateContextsAndConcurrentResolution();
    [Fact] public void MissingTypesAndCustomComparersFailClearly() => ContextContracts.MissingTypesAndCustomComparersFailClearly();
    [Fact] public void ModelAliasesAndTypeIdsRoundTrip() => ContextContracts.ModelAliasesAndTypeIdsRoundTrip();
    [Fact] public void ExplicitContextsPreserveWireFormat() => ContextContracts.ExplicitContextsPreserveWireFormat();

    [Fact]
    public void AliasTreeDistinguishesPreservingTraversalAndExplicitReset()
    {
        var tree = Orleans.Serialization.TypeSystem.CompoundTypeAliasTree.Create();
        var prefix = tree.Add("shared", typeof(int));
        Assert.Same(prefix, tree.GetOrAdd("shared"));
        Assert.Equal(typeof(int), prefix.Value);
        tree.GetOrAdd("shared").Add("child", typeof(string));
        Assert.Equal(typeof(int), prefix.Value);
        tree.Add("shared");
        Assert.Null(prefix.Value);
        tree.Add("shared", typeof(long));
        Assert.Equal(typeof(long), tree.GetOrAdd("shared").Value);
    }

    [Theory]
    [InlineData("typeof(System.Collections.Generic.List<>)", "closed")]
    [InlineData("typeof(System.IO.Stream)", "GenerateSerializerAttribute")]
    [InlineData("typeof(int[,])", "single-dimensional")]
    [InlineData("null", "root")]
    public void UnsupportedRootsProduceGeneratorErrors(string root, string reason)
    {
        var (_, result) = Generate($$"""
            [Orleans.GenerateSerializerContext({{root}})]
            public partial class DemoContext : Orleans.Serialization.SerializerContext { }
            """);
        var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Id == "ORLEANS0115");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains(reason, diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("public class DemoContext : Orleans.Serialization.SerializerContext { }")]
    [InlineData("public partial class DemoContext<T> : Orleans.Serialization.SerializerContext { }")]
    [InlineData("public partial class DemoContext { }")]
    [InlineData("file partial class DemoContext : Orleans.Serialization.SerializerContext { }")]
    public void InvalidContextsProduceGeneratorErrors(string declaration)
    {
        var (_, result) = Generate($"[Orleans.GenerateSerializerContext(typeof(int))] {declaration}");
        var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Id == "ORLEANS0114");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public void DuplicatePartialDeclarationsGenerateOneClosedGraph()
    {
        var (compilation, result) = Generate("""
            using System.Collections.Generic;
            [Orleans.GenerateSerializerContext(typeof(List<Dictionary<string, int>>))]
            public partial class DemoContext : Orleans.Serialization.SerializerContext { }
            [Orleans.GenerateSerializerContext(typeof(List<Dictionary<string, int>>))]
            public partial class DemoContext { }
            """);
        Assert.Empty(result.Diagnostics);
        Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken).Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var source = Assert.Single(result.Results.SelectMany(result => result.GeneratedSources),
            source => source.HintName.Contains(".context.", StringComparison.Ordinal)).SourceText.ToString();
        Assert.Contains("new global::Orleans.Serialization.Codecs.ListCodec<global::System.Collections.Generic.Dictionary<string, int>>", source, StringComparison.Ordinal);
        Assert.Contains("GetService<global::Orleans.Serialization.Codecs.DictionaryCodec<string, int>>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MakeGenericType", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetService<global::Orleans.Serialization.Codecs.IFieldCodec", source, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedModelFieldsAndFactoriesUseConcreteDependencies()
    {
        var (compilation, result) = Generate("""
            using Orleans;
            using Orleans.Serialization;
            using System.Collections.Generic;
            [GenerateSerializer]
            public sealed class Payload
            {
                [Id(0)] public List<Dictionary<string, int>> Items { get; set; } = new();
            }
            [GenerateSerializerContext(typeof(Payload))]
            public partial class DemoContext : SerializerContext { }
            """);
        Assert.Empty(result.Diagnostics);
        Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken).Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var sources = result.Results.SelectMany(result => result.GeneratedSources).ToArray();
        var context = Assert.Single(sources, source => source.HintName.Contains(".context.", StringComparison.Ordinal)).SourceText.ToString();
        var model = Assert.Single(sources, source => source.HintName.Contains(".ser.", StringComparison.Ordinal)).SourceText.ToString();
        Assert.Contains("ListCodec<global::System.Collections.Generic.Dictionary<string, int>>", model, StringComparison.Ordinal);
        Assert.Contains("ListCopier<global::System.Collections.Generic.Dictionary<string, int>>", model, StringComparison.Ordinal);
        Assert.Contains("GetService<global::Orleans.Serialization.Codecs.DictionaryCodec<string, int>>", context, StringComparison.Ordinal);
        Assert.Contains("GetService<global::Orleans.Serialization.Codecs.DictionaryCopier<string, int>>", context, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateCodecHolder", context, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateCopierHolder", context, StringComparison.Ordinal);
        Assert.DoesNotContain("#pragma warning disable", context, StringComparison.Ordinal);
    }

    [Fact]
    public void GenericValueAndArrayFactoriesCloseCanonicalServices()
    {
        var (compilation, result) = Generate("""
            using Orleans;
            using Orleans.Serialization;
            [GenerateSerializer]
            public struct ValuePayload<T> { [Id(0)] public T Value { get; set; } }
            [GenerateSerializer]
            public sealed class Box<T> { [Id(0)] public T[] Values { get; set; } }
            [GenerateSerializerContext(typeof(ValuePayload<int>))]
            [GenerateSerializerContext(typeof(Box<byte>))]
            [GenerateSerializerContext(typeof(Box<int>))]
            public partial class DemoContext : SerializerContext { }
            """);
        Assert.Empty(result.Diagnostics);
        Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken).Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var context = Assert.Single(result.Results.SelectMany(result => result.GeneratedSources),
            source => source.HintName.Contains(".context.", StringComparison.Ordinal)).SourceText.ToString();
        Assert.Contains("AddSerializerService<global::Orleans.Serialization.Serializers.IValueSerializer<global::ValuePayload<int>>>(static provider =>", context, StringComparison.Ordinal);
        Assert.Contains("AddSerializerService<global::Orleans.Serialization.Codecs.ArrayCodec<byte>>(static provider => new global::Orleans.Serialization.Codecs.ArrayCodec<byte>(", context, StringComparison.Ordinal);
        Assert.Contains("AddSerializerService<global::Orleans.Serialization.Codecs.ArrayCopier<byte>>(static provider => new global::Orleans.Serialization.Codecs.ArrayCopier<byte>(", context, StringComparison.Ordinal);
        Assert.Contains("AddSerializerService<global::Orleans.Serialization.Codecs.ArrayCodec<int>>", context, StringComparison.Ordinal);
        Assert.Contains("AddSerializerService<global::Orleans.Serialization.Codecs.ByteArrayCodec>", context, StringComparison.Ordinal);
        Assert.Contains("AddSerializerService<global::Orleans.Serialization.Codecs.ByteArrayCopier>", context, StringComparison.Ordinal);
        Assert.Contains("AddSerializer<byte[]>", context, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateCodecHolder", context, StringComparison.Ordinal);
        Assert.DoesNotContain("MakeGenericType", context, StringComparison.Ordinal);
        Assert.DoesNotContain("#pragma warning disable", context, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Orleans")]
    [InlineData("Serialization")]
    [InlineData("Codecs")]
    [InlineData("Cloning")]
    [InlineData("System")]
    [InlineData("global")]
    public void GenericArrayParameterNamesPreserveQualifiedNamespaces(string parameter)
    {
        var (compilation, result) = Generate($$"""
            [global::Orleans.GenerateSerializer]
            public sealed class Box<{{parameter}}>
            {
                [global::Orleans.Id(0)] public {{parameter}}[] Values { get; set; }
                [global::Orleans.Id(1)] public {{parameter}}[][] Nested { get; set; }
            }
            [global::Orleans.GenerateSerializerContext(typeof(Box<byte>))]
            public partial class DemoContext : global::Orleans.Serialization.SerializerContext { }
            """);
        Assert.Empty(result.Diagnostics);
        Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var context = Assert.Single(result.Results.SelectMany(static result => result.GeneratedSources),
            static source => source.HintName.Contains(".context.", StringComparison.Ordinal)).SourceText.ToString();
        Assert.Contains("AddSerializerService<global::Orleans.Serialization.Codecs.ArrayCodec<byte>>", context, StringComparison.Ordinal);
        Assert.Contains("AddSerializerService<global::Orleans.Serialization.Codecs.ArrayCopier<byte>>", context, StringComparison.Ordinal);
        Assert.Contains("AddSerializerService<global::Orleans.Serialization.Codecs.ArrayCodec<byte[]>>", context, StringComparison.Ordinal);
        Assert.Contains("AddSerializerService<global::Orleans.Serialization.Codecs.ByteArrayCodec>", context, StringComparison.Ordinal);
    }

    [Fact]
    public void RecursiveCollectionFactoriesUseTypedCycleEdges()
    {
        var (_, result) = Generate("""
            using Orleans;
            using Orleans.Serialization;
            using System.Collections.Generic;
            [GenerateSerializer]
            public sealed class Node
            {
                [Id(0)] public List<Node> Children { get; set; } = new();
            }
            [GenerateSerializerContext(typeof(List<Node>))]
            public partial class DemoContext : SerializerContext { }
            """);
        Assert.Empty(result.Diagnostics);
        var sources = result.Results.SelectMany(result => result.GeneratedSources).ToArray();
        var context = Assert.Single(sources, source => source.HintName.Contains(".context.", StringComparison.Ordinal)).SourceText.ToString();
        var model = Assert.Single(sources, source => source.HintName.Contains(".ser.", StringComparison.Ordinal)).SourceText.ToString();
        Assert.Contains("CreateCodecHolder<global::Node>(provider)", context, StringComparison.Ordinal);
        Assert.Contains("CreateCopierHolder<global::Node>(provider)", context, StringComparison.Ordinal);
        Assert.Contains("OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Codecs.ListCodec<global::Node>>(this, codecProvider)", model, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[Id(0)] public System.IO.Stream Value { get; set; }", "GenerateSerializerAttribute")]
    public void UnsupportedModelMembersProduceGeneratorErrors(string member, string reason)
    {
        var (_, result) = Generate($$"""
            using Orleans;
            [GenerateSerializer]
            public sealed class Payload { {{member}} }
            [GenerateSerializerContext(typeof(Payload))]
            public partial class DemoContext : Orleans.Serialization.SerializerContext { }
            """);
        var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Id == "ORLEANS0115");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains(reason, diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[Id(0)] private int _value;")]
    [InlineData("[Id(0)] public int Value { get; }")]
    public void ModelMembersRequireStaticallyGeneratedAccessors(string member)
    {
        var (_, result) = Generate($$"""
            using Orleans;
            [GenerateSerializer]
            public sealed class Payload { {{member}} }
            [GenerateSerializerContext(typeof(Payload))]
            public partial class DemoContext : Orleans.Serialization.SerializerContext { }
            """);
        var diagnostics = result.Diagnostics.Where(diagnostic => diagnostic.Id == "ORLEANS0115").ToArray();
        if (diagnostics.Length > 0)
        {
            Assert.Contains("statically generated field accessor", Assert.Single(diagnostics).GetMessage(), StringComparison.Ordinal);
        }
        else
        {
            Assert.Empty(result.Diagnostics);
            var model = Assert.Single(result.Results.SelectMany(result => result.GeneratedSources),
                source => source.HintName.Contains(".ser.", StringComparison.Ordinal)).SourceText.ToString();
            Assert.Contains("UnsafeAccessor", model, StringComparison.Ordinal);
            Assert.DoesNotContain("Utilities.FieldAccessor", model, StringComparison.Ordinal);
        }
    }

    private static (Compilation Compilation, GeneratorDriverRunResult Result) Generate(string source)
    {
        var references = TestCompilationHelper.CreateCompilation(source, "ContextGeneratorTests").GetAwaiter().GetResult().References;
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create("ContextGeneratorTests",
            [CSharpSyntaxTree.ParseText(source, parseOptions)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new OrleansSerializationSourceGenerator().AsSourceGenerator()], parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _, TestContext.Current.CancellationToken);
        return (output, driver.GetRunResult());
    }
}
