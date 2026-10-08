using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator;
using Orleans.Serialization.ContextSmoke;
using Xunit;

namespace Orleans.CodeGenerator.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("CodeGen")]
public sealed class SerializerContextTests(ITestOutputHelper output)
{
    [Fact] public void NestedCollectionsRoundTripAndCopy() => ContextContracts.NestedCollectionsRoundTripAndCopy();
    [Fact] public void NonSealedModelBaseServicesAliasCanonicalInstances() => ContextContracts.NonSealedModelBaseServicesAliasCanonicalInstances();
    [Fact] public void GeneratedFactoriesComposeWithMetadataAndReflection() => ContextContracts.GeneratedFactoriesComposeWithMetadataAndReflection();
    [Fact] public void GeneratedModelsTraverseDependencies() => ContextContracts.GeneratedModelsTraverseDependencies();
    [Fact] public void CanonicalValueSerializerUsesGeneratedCodec() => ContextContracts.CanonicalValueSerializerUsesGeneratedCodec();
    [Fact] public void GenericArraysRoundTripAndCopy() => ContextContracts.GenericArraysRoundTripAndCopy();
    [Fact] public void GenericArrayCyclesPreserveIdentity() => ContextContracts.GenericArrayCyclesPreserveIdentity();
    [Fact] public void CollectionDependenciesHonorSelectedCustomRegistrations() => ContextContracts.CollectionDependenciesHonorSelectedCustomRegistrations();
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
    [Fact] public void ContextAndAutomaticMetadataRegistrationAreIdempotent() => ContextContracts.ContextAndAutomaticMetadataRegistrationAreIdempotent();

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
    [InlineData("System.IO.Stream", "GenerateSerializerAttribute")]
    [InlineData("System.Collections.Generic.HashSet<int>", "GenerateSerializerAttribute")]
    [InlineData("int[,]", "single-dimensional")]
    public void UnsupportedRootsProduceGeneratorErrors(string root, string reason)
    {
        var (_, result) = Generate($$"""
            [Orleans.GenerateSerializerContext<{{root}}>]
            public partial class DemoContext : Orleans.Serialization.SerializerContext { }
            """);
        var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Id == "ORLEANS0115");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains(reason, diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("System.Collections.Generic.List<>", "DemoContext", "CS7003")]
    [InlineData("T", "DemoContext<T>", "CS8968")]
    [InlineData("System.Collections.Generic.List<T>", "DemoContext<T>", "CS8968")]
    [InlineData("MissingType", "DemoContext", "CS0246")]
    public void InvalidAttributeArgumentsProduceCompilerErrorsBeforeGeneration(string root, string declaration, string diagnosticId)
    {
        var compilation = CreateCompilation($$"""
            [Orleans.GenerateSerializerContext<{{root}}>]
            public partial class {{declaration}} : Orleans.Serialization.SerializerContext { }
            """);
        var errors = compilation.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        output.WriteLine(string.Join(Environment.NewLine, errors.Select(static diagnostic => diagnostic.ToString())));
        Assert.Contains(errors, diagnostic => diagnostic.Id == diagnosticId);

        var (generated, result) = Generate(compilation);
        Assert.Null(Assert.Single(result.Results).Exception);
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Id == "CS8785");
        Assert.DoesNotContain(result.Results.SelectMany(static result => result.GeneratedSources),
            static source => source.HintName.Contains(".context.", StringComparison.Ordinal));
        Assert.Contains(generated.GetDiagnostics(TestContext.Current.CancellationToken),
            diagnostic => diagnostic.Id == diagnosticId && diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void GenericAttributeExposesOnlyParameterlessConstruction()
    {
        var attribute = typeof(GenerateSerializerContextAttribute<int>);
        Assert.Empty(Assert.Single(attribute.GetConstructors()).GetParameters());
        Assert.Null(attribute.GetProperty("Type"));
        Assert.Null(attribute.Assembly.GetType("Orleans.GenerateSerializerContextAttribute"));
    }

    [Fact]
    public void GenericAttributesAcceptClosedRootKindsWithCSharp11()
    {
        var (compilation, result) = Generate(CreateCompilation("""
            [Orleans.GenerateSerializer] public struct Value { [Orleans.Id(0)] public int Number; }
            [Orleans.GenerateSerializer] public enum Flavor { First, Second }
            [Orleans.GenerateSerializerContext<int>]
            [Orleans.GenerateSerializerContext<int?>]
            [Orleans.GenerateSerializerContext<int[]>]
            [Orleans.GenerateSerializerContext<Value>]
            [Orleans.GenerateSerializerContext<Flavor>]
            public partial class DemoContext : Orleans.Serialization.SerializerContext { }
            """, LanguageVersion.CSharp11));
        Assert.Empty(result.Diagnostics);
        Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var source = Assert.Single(result.Results.SelectMany(static result => result.GeneratedSources),
            static source => source.HintName.Contains(".context.", StringComparison.Ordinal)).SourceText.ToString();
        foreach (var root in new[] { "int", "int?", "int[]", "global::Value", "global::Flavor" })
            Assert.Contains($"AddSerializer<{root}>", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SameContextNamesInDifferentNamespacesKeepDistinctGraphs()
    {
        var (compilation, result) = Generate("""
            namespace First
            {
                [Orleans.GenerateSerializerContext<int>]
                public partial class DemoContext : Orleans.Serialization.SerializerContext { }
            }
            namespace Second
            {
                [Orleans.GenerateSerializerContext<string>]
                public partial class DemoContext : Orleans.Serialization.SerializerContext { }
            }
            """);
        Assert.Empty(result.Diagnostics);
        Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var sources = result.Results.SelectMany(static result => result.GeneratedSources)
            .Where(static source => source.HintName.Contains(".context.", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, sources.Length);
        var first = Assert.Single(sources, static source => source.HintName.Contains("First.DemoContext", StringComparison.Ordinal));
        var second = Assert.Single(sources, static source => source.HintName.Contains("Second.DemoContext", StringComparison.Ordinal));
        Assert.Contains("AddSerializer<int>", first.SourceText.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("AddSerializer<string>", first.SourceText.ToString(), StringComparison.Ordinal);
        Assert.Contains("AddSerializer<string>", second.SourceText.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("AddSerializer<int>", second.SourceText.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("public class DemoContext : Orleans.Serialization.SerializerContext { }")]
    [InlineData("public partial class DemoContext<T> : Orleans.Serialization.SerializerContext { }")]
    [InlineData("public partial class DemoContext { }")]
    [InlineData("file partial class DemoContext : Orleans.Serialization.SerializerContext { }")]
    public void InvalidContextsProduceGeneratorErrors(string declaration)
    {
        var (_, result) = Generate($"[Orleans.GenerateSerializerContext<int>] {declaration}");
        var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Id == "ORLEANS0114");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public void DuplicatePartialDeclarationsGenerateOneClosedGraph()
    {
        var (compilation, result) = Generate("""
            using System.Collections.Generic;
            [Orleans.GenerateSerializerContext<List<Dictionary<string, int>>>]
            [Orleans.GenerateSerializerContext<int?>]
            public partial class DemoContext : Orleans.Serialization.SerializerContext { }
            [Orleans.GenerateSerializerContext<List<Dictionary<string, int>>>]
            [Orleans.GenerateSerializerContext<byte[]>]
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
        Assert.Equal(1, source.Split("options.AddSerializer<global::System.Collections.Generic.List<global::System.Collections.Generic.Dictionary<string, int>>>", StringSplitOptions.None).Length - 1);
        Assert.Contains("AddSerializer<int?>", source, StringComparison.Ordinal);
        Assert.Contains("AddSerializer<byte[]>", source, StringComparison.Ordinal);
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
            [GenerateSerializerContext<Payload>]
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
            [GenerateSerializerContext<ValuePayload<int>>]
            [GenerateSerializerContext<Box<byte>>]
            [GenerateSerializerContext<Box<int>>]
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
    [InlineData("class", "", true, true)]
    [InlineData("class", "[Orleans.Immutable]", true, true)]
    [InlineData("sealed class", "", false, false)]
    [InlineData("sealed class", "[Orleans.Immutable]", false, false)]
    public void BaseAliasesFollowGeneratedModelContracts(string declaration, string attributes, bool baseCodec, bool baseCopier)
    {
        var (compilation, result) = Generate($$"""
            [Orleans.GenerateSerializer]
            {{attributes}}
            public {{declaration}} Payload
            {
                [Orleans.Id(0)] public System.Collections.Generic.List<int> Values { get; set; } = new();
            }
            [Orleans.GenerateSerializerContext<Payload>]
            public partial class DemoContext : Orleans.Serialization.SerializerContext { }
            """);
        Assert.Empty(result.Diagnostics);
        Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var source = Assert.Single(result.Results.SelectMany(static result => result.GeneratedSources),
            static source => source.HintName.Contains(".context.", StringComparison.Ordinal)).SourceText.ToString();
        Assert.Equal(baseCodec, source.Contains("AddSerializerService<global::Orleans.Serialization.Serializers.IBaseCodec<global::Payload>>", StringComparison.Ordinal));
        Assert.Equal(baseCopier, source.Contains("AddSerializerService<global::Orleans.Serialization.Cloning.IBaseCopier<global::Payload>>", StringComparison.Ordinal));
        var registrations = SyntaxFactory.ParseCompilationUnit(source)
            .DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(static invocation => invocation.Expression is MemberAccessExpressionSyntax
            { Name: GenericNameSyntax { Identifier.ValueText: "AddSerializer" } }).ToArray();
        var canonical = Assert.Single(registrations, static registration => registration.Expression.ToString().Contains("<global::Payload>", StringComparison.Ordinal));
        var codecFactory = canonical.ArgumentList.Arguments[0].Expression.ToString();
        var copierFactory = canonical.ArgumentList.Arguments[1].Expression.ToString();
        if (baseCodec) Assert.Contains($"IBaseCodec<global::Payload>>({codecFactory})", source, StringComparison.Ordinal);
        if (baseCopier) Assert.Contains($"IBaseCopier<global::Payload>>({copierFactory})", source, StringComparison.Ordinal);
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
            [global::Orleans.GenerateSerializerContext<Box<byte>>]
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
    public void IncidentalPrivateInterfaceArgumentsPreserveAccessibleAliasMetadata()
    {
        var (compilation, result) = Generate("""
            [Orleans.CompoundTypeAlias("private-argument-tag")]
            public interface ITag<T> { }
            [Orleans.GenerateSerializer]
            public sealed class Payload : ITag<Payload.Hidden>
            {
                private sealed class Hidden { }
                [Orleans.Id(0)] public int Value { get; set; }
            }
            [Orleans.GenerateSerializerContext<Payload>]
            public partial class DemoContext : Orleans.Serialization.SerializerContext { }
            """);
        Assert.Empty(result.Diagnostics);
        Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var context = Assert.Single(result.Results.SelectMany(static result => result.GeneratedSources),
            static source => source.HintName.Contains(".context.", StringComparison.Ordinal)).SourceText.ToString();
        Assert.Contains("typeof(global::ITag<>)", context, StringComparison.Ordinal);
        Assert.DoesNotContain("typeof(global::Payload.Hidden)", context, StringComparison.Ordinal);
        Assert.DoesNotContain("AddSerializer<global::ITag", context, StringComparison.Ordinal);
    }

    [Fact]
    public void RequiredPrivateArgumentAliasesProduceClearGeneratorErrors()
    {
        var (_, result) = Generate("""
            public interface ITag<T> { }
            [Orleans.GenerateSerializer]
            public sealed class Payload : ITag<Payload.Hidden>
            {
                [Orleans.CompoundTypeAlias("required-private-alias")]
                private sealed class Hidden { }
                [Orleans.Id(0)] public int Value { get; set; }
            }
            [Orleans.GenerateSerializerContext<Payload>]
            public partial class DemoContext : Orleans.Serialization.SerializerContext { }
            """);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic => diagnostic.Id == "ORLEANS0115");
        Assert.Contains("inaccessible", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("Hidden", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("System.Collections.Generic.List<T>", "")]
    [InlineData("System.Collections.Generic.List<T>", "[Orleans.CompoundTypeAlias(\"expanding-interface\")]")]
    [InlineData("T[]", "")]
    [InlineData("T[]", "[Orleans.CompoundTypeAlias(\"expanding-interface\")]")]
    public void ExpandingGenericInterfaceMetadataProducesBoundedDiagnostics(string argument, string alias)
    {
        var (_, result) = Generate($$"""
            {{alias}}
            public interface ITag<T> { }
            [Orleans.GenerateSerializer]
            public sealed class Payload<T> : ITag<Payload<{{argument}}>>
            {
                [Orleans.Id(0)] public int Value { get; set; }
            }
            [Orleans.GenerateSerializerContext<Payload<int>>]
            public partial class DemoContext : Orleans.Serialization.SerializerContext { }
            """);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic => diagnostic.Id == "ORLEANS0115");
        Assert.Contains("metadata dependency graph", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("finite type metadata", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.DoesNotContain(result.Results.SelectMany(static result => result.GeneratedSources),
            static source => source.HintName.Contains(".context.", StringComparison.Ordinal));
    }

    [Fact]
    public void FiniteGenericInterfaceMetadataCyclesPreserveAliasRegistration()
    {
        var (compilation, result) = Generate("""
            [Orleans.CompoundTypeAlias("finite-interface")]
            public interface ITag<T> { }
            [Orleans.GenerateSerializer]
            public sealed class Payload<T> : ITag<Payload<T>>
            {
                [Orleans.Id(0)] public int Value { get; set; }
            }
            [Orleans.GenerateSerializerContext<Payload<int>>]
            public partial class DemoContext : Orleans.Serialization.SerializerContext { }
            """);
        Assert.Empty(result.Diagnostics);
        Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var context = Assert.Single(result.Results.SelectMany(static result => result.GeneratedSources),
            static source => source.HintName.Contains(".context.", StringComparison.Ordinal)).SourceText.ToString();
        Assert.Contains("\"finite-interface\"", context, StringComparison.Ordinal);
        Assert.Contains("typeof(global::ITag<>)", context, StringComparison.Ordinal);
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
            [GenerateSerializerContext<List<Node>>]
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
            [GenerateSerializerContext<Payload>]
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
            [GenerateSerializerContext<Payload>]
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
        => Generate(CreateCompilation(source));

    private static CSharpCompilation CreateCompilation(string source, LanguageVersion languageVersion = LanguageVersion.Preview)
    {
        var references = TestCompilationHelper.CreateCompilation(source, "ContextGeneratorTests").GetAwaiter().GetResult().References;
        var parseOptions = new CSharpParseOptions(languageVersion);
        return CSharpCompilation.Create("ContextGeneratorTests",
            [CSharpSyntaxTree.ParseText(source, parseOptions)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static (Compilation Compilation, GeneratorDriverRunResult Result) Generate(CSharpCompilation compilation)
    {
        var parseOptions = (CSharpParseOptions)compilation.SyntaxTrees.First().Options;
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new OrleansSerializationSourceGenerator().AsSourceGenerator()], parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _, TestContext.Current.CancellationToken);
        return (output, driver.GetRunResult());
    }
}
