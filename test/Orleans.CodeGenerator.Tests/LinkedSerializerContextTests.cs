using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.Extensions.Options;
using Orleans.Serialization.Configuration;

namespace Orleans.CodeGenerator.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("CodeGen")]
public sealed class LinkedSerializerContextTests
{
    // Field-target attributes disappear from genuine reference assemblies. Public accessors
    // still let the source-owned generator implement the selected contract without private access.
    private const string ProducerSource = """
        namespace Producer;
        [Orleans.GenerateSerializer, Orleans.Alias("linked-payload")]
        public sealed class Payload<T>
        {
            [field: Orleans.Id(0)] public System.Collections.Generic.List<T> Values { get; set; } = new();
            [field: Orleans.Id(1), Orleans.Immutable] public System.Collections.Generic.List<T> Stable { get; set; } = new();
        }
        [Orleans.GenerateSerializer, Orleans.Id(407)]
        public sealed class Identified
        {
            [field: Orleans.Id(0)] public int Value { get; set; }
        }
        [Orleans.GeneratedContextType<Payload<int>>]
        [Orleans.GeneratedContextType<Identified>]
        public partial class ExportedContext : Orleans.Serialization.GeneratedContext { }
        """;

    private const string EnvelopeSource = """
        [Orleans.GenerateSerializer]
        public sealed class Envelope
        {
            [Orleans.Id(0)] public Producer.Payload<int> First { get; set; }
            [Orleans.Id(1)] public Producer.Payload<int>[] Mirror { get; set; }
            [Orleans.Id(2)] public System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, Producer.Payload<int>[]>> Rows { get; set; }
            [Orleans.Id(3)] public Producer.Identified Identified { get; set; }
        }
        [Orleans.GeneratedContextType<Envelope>]
        [Orleans.GeneratedContextImport<Producer.ExportedContext>]
        public partial class ConsumerContext : Orleans.Serialization.GeneratedContext { }
        """;

    private const string RuntimeUsings = """
        using Microsoft.Extensions.DependencyInjection;
        using Microsoft.Extensions.Options;
        using Orleans.Serialization;
        using Orleans.Serialization.Configuration;
        using Orleans.Serialization.Serializers;
        """;

    [Fact]
    public async Task IncludeAttributeIsDistinctAndLeavesContextValidationToGenerator()
    {
        var include = typeof(GeneratedContextImportAttribute<int>);
        Assert.NotEqual(typeof(GeneratedContextTypeAttribute<int>), include);
        Assert.Equal(typeof(Attribute), include.BaseType);
        Assert.Empty(include.GetGenericTypeDefinition().GetGenericArguments()[0].GetGenericParameterConstraints());
        Assert.Empty(Assert.Single(include.GetConstructors()).GetParameters());
        Assert.Null(include.GetProperty("Type"));
        Assert.Null(include.Assembly.GetType("Orleans.GeneratedContextImportAttribute"));

        var input = await Compile("""
            [Orleans.GeneratedContextImport<int>]
            public partial class ConsumerContext : Orleans.Serialization.GeneratedContext { }
            """);
        // The ungenerated partial still lacks ConfigureInner, but C# accepts the
        // unconstrained attribute argument: there is no generic constraint error.
        Assert.Equal(new[] { "CS0534" }, input.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(static diagnostic => diagnostic.Id));
        var (_, result) = Generate(input);
        AssertRejected(result, "ORLEANS0115", "include a generated context");
    }

    [Theory]
    [InlineData("source", MetadataImportOptions.Public)]
    [InlineData("source", MetadataImportOptions.All)]
    [InlineData("full", MetadataImportOptions.Public)]
    [InlineData("full", MetadataImportOptions.All)]
    [InlineData("ref", MetadataImportOptions.Public)]
    [InlineData("ref", MetadataImportOptions.All)]
    public async Task LinkedModelLibraryRoundTripsCopiesAndSuppliesHiddenContract(string image, MetadataImportOptions import)
    {
        var producer = await GenerateAccepted(ProducerSource);
        var reference = Reference(producer, image);
        var input = await Compile(RuntimeUsings + EnvelopeSource + """
            public static class RuntimeProof
            {
                public static object[] Run()
                {
                    var context = new ConsumerContext();
                    var options = new TypeManifestOptions();
                    ((IConfigureOptions<TypeManifestOptions>)context).Configure(options);
                    var expectedAllowed = new TypeManifestOptions();
                    expectedAllowed.AddAllowedType(typeof(Producer.Payload<>));
                    expectedAllowed.AddAllowedType(typeof(Producer.Identified));
                    using var services = new ServiceCollection().AddGeneratedContext(context).BuildServiceProvider();
                    using var otherServices = new ServiceCollection().AddGeneratedContext(new ConsumerContext()).BuildServiceProvider();
                    var serializer = services.GetRequiredService<Serializer>();
                    var copier = services.GetRequiredService<DeepCopier>();
                    var provider = services.GetRequiredService<CodecProvider>();
                    var otherProvider = otherServices.GetRequiredService<CodecProvider>();
                    var payload = new Producer.Payload<int> { Values = new() { 13, 17 }, Stable = new() { 19, 23 } };
                    var shared = new[] { payload, payload };
                    var original = new Envelope
                    {
                        First = payload, Mirror = shared,
                        Rows = new() { new() { ["left"] = shared, ["right"] = shared } },
                        Identified = new() { Value = 29 }
                    };
                    var restored = serializer.Deserialize<Envelope>(serializer.SerializeToArray(original));
                    var copy = copier.Copy(original);
                    copy.First.Values[0] = 31;
                    return new object[]
                    {
                        restored.First.Values[0], restored.First.Values[1], restored.First.Stable[0], restored.Identified.Value,
                        ReferenceEquals(restored.First, restored.Mirror[0]) && ReferenceEquals(restored.Mirror[0], restored.Mirror[1]),
                        ReferenceEquals(restored.Mirror, restored.Rows[0]["left"]) && ReferenceEquals(restored.Rows[0]["left"], restored.Rows[0]["right"]),
                        !ReferenceEquals(original.First.Stable, restored.First.Stable),
                        original.First.Values[0], copy.First.Values[0],
                        !ReferenceEquals(original, copy) && !ReferenceEquals(original.First, copy.First) && !ReferenceEquals(original.First.Values, copy.First.Values),
                        ReferenceEquals(copy.First.Stable, original.First.Stable),
                        ReferenceEquals(copy.First, copy.Mirror[0]) && ReferenceEquals(copy.Mirror[0], copy.Mirror[1]) && ReferenceEquals(copy.Mirror, copy.Rows[0]["left"]),
                        ReferenceEquals(provider.GetCodec<Producer.Payload<int>>(), provider.GetCodec<Producer.Payload<int>>()),
                        ReferenceEquals(provider.GetDeepCopier<Producer.Payload<int>>(), provider.GetDeepCopier<Producer.Payload<int>>()),
                        !ReferenceEquals(provider.GetCodec<Producer.Payload<int>>(), otherProvider.GetCodec<Producer.Payload<int>>()),
                        !ReferenceEquals(provider.GetDeepCopier<Producer.Payload<int>>(), otherProvider.GetDeepCopier<Producer.Payload<int>>()),
                        options.WellKnownTypeAliases["linked-payload"] == typeof(Producer.Payload<>),
                        options.WellKnownTypeIds[407] == typeof(Producer.Identified),
                        expectedAllowed.AllowedTypes.IsSubsetOf(options.AllowedTypes)
                    };
                }
            }
            """, reference);
        input = input.WithOptions(input.Options.WithMetadataImportOptions(import));
        if (image == "ref") AssertReferenceHidesContract(input, reference);
        var (consumer, result) = Generate(input);
        AssertAccepted(consumer, result);
        AssertOpaqueConsumer(result, "ConsumerContext", ("Payload", "Payload<int>"), ("Identified", "Identified"));
        var context = ContextSource(result, "ConsumerContext");
        Assert.True(context.IndexOf("new global::Producer.ExportedContext()", StringComparison.Ordinal)
            < context.IndexOf("options.AddSerializer", StringComparison.Ordinal), context);
        // Local generic collections can allow their closed Payload<int> argument.
        // The linked producer, not this context, supplies the open model's alias metadata.
        Assert.DoesNotContain("AddAllowedType(typeof(global::Producer.Payload<>))", context, StringComparison.Ordinal);
        Assert.DoesNotContain("linked-payload", context, StringComparison.Ordinal);
        Assert.DoesNotContain("options.WellKnownTypeIds", context, StringComparison.Ordinal);
        // The producer's field-target selection and copy policy are hidden. The consumer
        // context must not recover their List<int> graph from implementation metadata.
        Assert.DoesNotContain("ListCodec<int>", context, StringComparison.Ordinal);
        Assert.DoesNotContain("ListCopier<int>", context, StringComparison.Ordinal);
        Assert.DoesNotContain("AddSerializer<global::System.Collections.Generic.List<int>>(", context, StringComparison.Ordinal);
        AssertNoAssemblyMetadata(producer);
        AssertNoAssemblyMetadata(consumer);
        Assert.Equal(new object[] { 13, 17, 19, 29, true, true, true, 13, 31, true, true, true, true, true, true, true, true, true, true },
            Run(consumer, producer));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("full")]
    [InlineData("ref")]
    public async Task IncludeOnlyContextImportsRootsWithoutLocalFactories(string image)
    {
        var producer = await GenerateAccepted(ProducerSource);
        var (consumer, result) = Generate(await Compile(RuntimeUsings + """
            [Orleans.GeneratedContextImport<Producer.ExportedContext>]
            public partial class ConsumerContext : Orleans.Serialization.GeneratedContext { }
            public static class RuntimeProof
            {
                public static object[] Run()
                {
                    using var services = new ServiceCollection().AddGeneratedContext(new ConsumerContext()).BuildServiceProvider();
                    var serializer = services.GetRequiredService<Serializer>();
                    var original = new Producer.Identified { Value = 43 };
                    return new object[] { serializer.Deserialize<Producer.Identified>(serializer.SerializeToArray(original)).Value };
                }
            }
            """, Reference(producer, image)));
        AssertAccepted(consumer, result);
        var context = ContextSource(result, "ConsumerContext");
        Assert.Contains("new global::Producer.ExportedContext()", context, StringComparison.Ordinal);
        Assert.DoesNotContain("options.AddSerializer", context, StringComparison.Ordinal);
        AssertOpaqueConsumer(result, "ConsumerContext", ("Payload", "Payload<int>"), ("Identified", "Identified"));
        Assert.Equal(new object[] { 43 }, Run(consumer, producer));
    }

    [Fact]
    public async Task DeclaredSuppliedRootUsesTransitiveOwnerWithoutReconstruction()
    {
        // Bridge also declares the root, but its included context owns the implementation.
        // Declaring that same external root in the consumer must not trigger ref inspection.
        var (producer, producerResult) = Generate(await Compile(ProducerSource + """
            [Orleans.GeneratedContextType<Producer.Payload<int>>]
            [Orleans.GeneratedContextImport<Producer.ExportedContext>]
            public partial class BridgeContext : Orleans.Serialization.GeneratedContext { }
            """));
        AssertAccepted(producer, producerResult);
        AssertOpaqueConsumer(producerResult, "BridgeContext", ("Payload", "Payload<int>"));
        var (consumer, result) = Generate(await Compile(RuntimeUsings + """
            [Orleans.GeneratedContextType<Producer.Payload<int>>]
            [Orleans.GeneratedContextImport<Producer.BridgeContext>]
            public partial class ConsumerContext : Orleans.Serialization.GeneratedContext { }
            public static class RuntimeProof
            {
                public static object[] Run()
                {
                    using var services = new ServiceCollection().AddGeneratedContext(new ConsumerContext()).BuildServiceProvider();
                    var serializer = services.GetRequiredService<Serializer>();
                    var copier = services.GetRequiredService<DeepCopier>();
                    var original = new Producer.Payload<int> { Values = new() { 61 }, Stable = new() { 71 } };
                    var restored = serializer.Deserialize<Producer.Payload<int>>(serializer.SerializeToArray(original));
                    var copy = copier.Copy(original);
                    copy.Values[0] = 67;
                    return new object[]
                    {
                        restored.Values[0], restored.Stable[0], original.Values[0], copy.Values[0],
                        !ReferenceEquals(original, copy) && !ReferenceEquals(original.Values, copy.Values),
                        ReferenceEquals(original.Stable, copy.Stable)
                    };
                }
            }
            """, Reference(producer, "ref")));
        AssertAccepted(consumer, result);
        AssertOpaqueConsumer(result, "ConsumerContext", ("Payload", "Payload<int>"));
        Assert.DoesNotContain("options.AddSerializer", ContextSource(result, "ConsumerContext"), StringComparison.Ordinal);
        Assert.Equal(new object[] { 61, 71, 61, 67, true, true }, Run(consumer, producer));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedAndIncludeOnlyPartialsGenerateEachContextExactlyOnce(bool includeFirst)
    {
        var root = """
            [Orleans.GeneratedContextType<string>]
            [Orleans.GeneratedContextType<string>]
            public partial class MixedContext : Orleans.Serialization.GeneratedContext { }
            """;
        var links = """
            [Orleans.GeneratedContextImport<LeafContext>]
            [Orleans.GeneratedContextImport<LeafContext>]
            public partial class MixedContext { }
            [Orleans.GeneratedContextImport<LeafContext>]
            public partial class OnlyContext : Orleans.Serialization.GeneratedContext { }
            """;
        var input = await Compile("""
            [Orleans.GeneratedContextType<int>]
            public partial class LeafContext : Orleans.Serialization.GeneratedContext { }
            """);
        input = input.AddSyntaxTrees(CSharpSyntaxTree.ParseText(includeFirst ? links : root, cancellationToken: TestContext.Current.CancellationToken),
            CSharpSyntaxTree.ParseText(includeFirst ? root : links, cancellationToken: TestContext.Current.CancellationToken), CSharpSyntaxTree.ParseText("""
            [Orleans.GeneratedContextImport<LeafContext>]
            public partial class OnlyContext { }
            """, cancellationToken: TestContext.Current.CancellationToken));
        var (output, result) = Generate(input);
        AssertAccepted(output, result);
        Assert.Equal(3, ContextSources(result).Count());
        Assert.Equal(3, Sources(result).Count(source => source.HintName.Contains(".context-declaration.", StringComparison.Ordinal)));
        var mixed = ContextSource(result, "MixedContext");
        Assert.Equal(1, Occurrences(mixed, "new global::LeafContext()"));
        Assert.Equal(1, Occurrences(mixed, "options.AddSerializer<string>"));
        Assert.DoesNotContain("options.AddSerializer<int>", mixed, StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(ContextSource(result, "OnlyContext"), "new global::LeafContext()"));
        Emit(output);
    }

    [Fact]
    public async Task ModelRootAttributesRemainModelOnly()
    {
        var (output, result) = Generate(await Compile("""
            [Orleans.GenerateSerializer] public sealed class Model { [Orleans.Id(0)] public int Value { get; set; } }
            [Orleans.GeneratedContextType<Model>]
            public partial class ModelContext : Orleans.Serialization.GeneratedContext { }
            """));
        AssertAccepted(output, result);
        var context = ContextSource(result, "ModelContext");
        Assert.Contains("options.AddSerializer<global::Model>", context, StringComparison.Ordinal);
        Assert.Contains("new global::OrleansCodeGen.Codec_Model", context, StringComparison.Ordinal);
        Assert.DoesNotContain("IConfigureOptions<", context, StringComparison.Ordinal);
        Emit(output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContextTypesCannotBeDeclaredAsModelRoots(bool transitive)
    {
        var source = """
            [Orleans.GeneratedContextType<int>]
            public partial class LeafContext : Orleans.Serialization.GeneratedContext { }
            """;
        if (transitive)
        {
            // Invalid linked metadata is deliberately emitted without running its generator.
            var producer = await Compile("""
                [Orleans.GeneratedContextType<LeafContext>]
                public class BadContext : Orleans.Serialization.GeneratedContext
                { protected override void ConfigureInner(Orleans.Serialization.Configuration.TypeManifestOptions options) { } }
                [Orleans.GeneratedContextType<int>]
                public class LeafContext : Orleans.Serialization.GeneratedContext
                { protected override void ConfigureInner(Orleans.Serialization.Configuration.TypeManifestOptions options) { } }
                """);
            var (_, result) = Generate(await Compile("""
                [Orleans.GeneratedContextImport<BadContext>]
                public partial class ConsumerContext : Orleans.Serialization.GeneratedContext { }
                """, Reference(producer, "ref")));
            AssertRejected(result, "ORLEANS0115", "GeneratedContextTypeAttribute declares model roots");
        }
        else
        {
            var (_, result) = Generate(await Compile(source + """
                [Orleans.GeneratedContextType<LeafContext>]
                public partial class ConsumerContext : Orleans.Serialization.GeneratedContext { }
                """));
            AssertRejected(result, "ORLEANS0115", "use GeneratedContextImportAttribute");
        }
    }

    [Theory]
    [InlineData("nonpartial", "public class ConsumerContext : Orleans.Serialization.GeneratedContext { }")]
    [InlineData("generic", "public partial class ConsumerContext<T> : Orleans.Serialization.GeneratedContext { }")]
    [InlineData("abstract", "public abstract partial class ConsumerContext : Orleans.Serialization.GeneratedContext { }")]
    [InlineData("unrelated", "public partial class ConsumerContext { }")]
    [InlineData("nested", "public class Outer { [Orleans.GeneratedContextImport<LeafContext>] public partial class ConsumerContext : Orleans.Serialization.GeneratedContext { } }")]
    public async Task InvalidIncludeOnlyDeclarationsReportConsumerDiagnostic(string caseId, string declaration)
    {
        var input = await Compile("""
            [Orleans.GeneratedContextType<int>]
            public partial class LeafContext : Orleans.Serialization.GeneratedContext { }
            """ + (caseId == "nested" ? declaration : "[Orleans.GeneratedContextImport<LeafContext>] " + declaration));
        var (_, result) = Generate(input);
        AssertRejected(result, "ORLEANS0114", "top-level partial class");
    }

    [Theory]
    [InlineData("abstract", "public abstract class Linked : Orleans.Serialization.GeneratedContext", "", "Linked")]
    [InlineData("generic", "public class Linked<T> : Orleans.Serialization.GeneratedContext", "", "Linked<int>")]
    [InlineData("parameterized", "public class Linked : Orleans.Serialization.GeneratedContext", "public Linked(int value) { }", "Linked")]
    [InlineData("optional", "public class Linked : Orleans.Serialization.GeneratedContext", "public Linked(int value = 0) { }", "Linked")]
    [InlineData("private", "public class Linked : Orleans.Serialization.GeneratedContext", "private Linked() { }", "Linked")]
    [InlineData("protected", "public class Linked : Orleans.Serialization.GeneratedContext", "protected Linked() { }", "Linked")]
    [InlineData("nested", "public class Linked : Orleans.Serialization.GeneratedContext", "", "Outer.Linked")]
    [InlineData("indirect", "public class Linked : BaseContext", "", "Linked")]
    public async Task LinkedTypesRequireConcreteTopLevelDefaultConstruction(string caseId, string declaration, string constructor, string argument)
    {
        var body = $$"""
            [Orleans.GeneratedContextType<int>]
            {{declaration}}
            {
                {{constructor}}
                protected override void ConfigureInner(Orleans.Serialization.Configuration.TypeManifestOptions options) { }
            }
            """;
        if (caseId == "nested") body = "public class Outer { " + body + " }";
        if (caseId == "indirect") body += """
            public abstract class BaseContext : Orleans.Serialization.GeneratedContext { }
            """;
        var producer = await Compile(body);
        var (_, result) = Generate(await Compile($$"""
            [Orleans.GeneratedContextImport<{{argument}}>]
            public partial class ConsumerContext : Orleans.Serialization.GeneratedContext { }
            """, Reference(producer, "ref")));
        AssertRejected(result, "ORLEANS0115", "accessible parameterless construction");
    }

    [Theory]
    [InlineData("none")]
    [InlineData("model")]
    [InlineData("nested-model")]
    [InlineData("missing")]
    [InlineData("open")]
    public async Task MissingOrIncorrectIncludesFailAtTheirOwningBoundary(string caseId)
    {
        var producer = await Compile("""
            public sealed class Model { }
            public class EmptyContext : Orleans.Serialization.GeneratedContext
            { protected override void ConfigureInner(Orleans.Serialization.Configuration.TypeManifestOptions options) { } }
            public class GenericContext<T> : Orleans.Serialization.GeneratedContext
            { protected override void ConfigureInner(Orleans.Serialization.Configuration.TypeManifestOptions options) { } }
            [Orleans.GeneratedContextType<int>]
            [Orleans.GeneratedContextImport<Model>]
            public class InvalidLinkContext : Orleans.Serialization.GeneratedContext
            { protected override void ConfigureInner(Orleans.Serialization.Configuration.TypeManifestOptions options) { } }
            """);
        var argument = caseId switch
        {
            "none" => "EmptyContext",
            "model" => "Model",
            "nested-model" => "InvalidLinkContext",
            "missing" => "MissingContext",
            "open" => "GenericContext<>",
            _ => throw new ArgumentOutOfRangeException(nameof(caseId))
        };
        var (output, result) = Generate(await Compile($$"""
            [Orleans.GeneratedContextImport<{{argument}}>]
            public partial class ConsumerContext : Orleans.Serialization.GeneratedContext { }
            """, Reference(producer, "ref")));
        if (caseId is "none" or "model" or "nested-model")
            AssertRejected(result, "ORLEANS0115", caseId == "none" ? "must declare roots" : "include a generated context");
        else
        {
            Assert.Empty(result.Diagnostics);
            Assert.Null(Assert.Single(result.Results).Exception);
            Assert.Contains(output.GetDiagnostics(TestContext.Current.CancellationToken),
                diagnostic => diagnostic.Id == (caseId == "missing" ? "CS0246" : "CS7003")
                    && diagnostic.Severity == DiagnosticSeverity.Error);
            Assert.Empty(ContextSources(result));
        }
    }

    [Theory]
    [InlineData("friend", true, true)]
    [InlineData("nonfriend", false, true)]
    [InlineData("private-ctor", true, false)]
    public async Task InternalLinkedContextsHonorFriendAndConstructorAccessibility(string caseId, bool friend, bool accessibleConstructor)
    {
        var consumerName = $"Friend{caseId.Replace("-", "", StringComparison.Ordinal)}{Guid.NewGuid():N}";
        var producer = await GenerateAccepted($$"""
            [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("{{consumerName}}")]
            namespace Producer;
            [Orleans.GeneratedContextType<int>]
            internal partial class InternalContext : Orleans.Serialization.GeneratedContext
            { {{(accessibleConstructor ? "internal" : "private")}} InternalContext() { } }
            """);
        var input = await TestCompilationHelper.CreateCompilation(RuntimeUsings + """
            [Orleans.GeneratedContextImport<Producer.InternalContext>]
            public partial class ConsumerContext : Orleans.Serialization.GeneratedContext { }
            public static class RuntimeProof
            {
                public static object[] Run()
                {
                    using var services = new ServiceCollection().AddGeneratedContext(new ConsumerContext()).BuildServiceProvider();
                    var serializer = services.GetRequiredService<Serializer>();
                    return new object[] { serializer.Deserialize<int>(serializer.SerializeToArray(47)) };
                }
            }
            """, friend ? consumerName : $"Other{Guid.NewGuid():N}", Reference(producer, "ref"),
            MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.DependencyInjection.ServiceProvider).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(IConfigureOptions<>).Assembly.Location));
        var (output, result) = Generate(input);
        if (!friend)
        {
            Assert.Contains(output.GetDiagnostics(TestContext.Current.CancellationToken),
                static diagnostic => diagnostic.Id == "CS0122" && diagnostic.Severity == DiagnosticSeverity.Error);
            Assert.DoesNotContain(result.Results, static result => result.Exception is not null);
        }
        else if (!accessibleConstructor)
            AssertRejected(result, "ORLEANS0115", "accessible parameterless construction");
        else
        {
            AssertAccepted(output, result);
            Assert.Equal(new object[] { 47 }, Run(output, producer));
        }
    }

    [Theory]
    [InlineData("missing-link")]
    [InlineData("unprovided-root")]
    [InlineData("different-closed-generic")]
    public async Task ReferenceModelsRequireAnIncludeProvidingTheExactClosedRoot(string caseId)
    {
        var producer = await GenerateAccepted(ProducerSource + """
            [Orleans.GeneratedContextType<int>]
            public partial class OtherContext : Orleans.Serialization.GeneratedContext { }
            """);
        var included = caseId switch
        {
            "missing-link" => "",
            "unprovided-root" => "[Orleans.GeneratedContextImport<Producer.OtherContext>]",
            "different-closed-generic" => "[Orleans.GeneratedContextImport<Producer.ExportedContext>]",
            _ => throw new ArgumentOutOfRangeException(nameof(caseId))
        };
        var argument = caseId == "different-closed-generic" ? "string" : "int";
        var (_, result) = Generate(await Compile($$"""
            [Orleans.GenerateSerializer]
            public sealed class Local
            { [Orleans.Id(0)] public Producer.Payload<{{argument}}> Value { get; set; } }
            [Orleans.GeneratedContextType<Local>]
            {{included}}
            public partial class ConsumerContext : Orleans.Serialization.GeneratedContext { }
            """, Reference(producer, "ref")));
        AssertRejected(result, "ORLEANS0115", "implementation metadata");
    }

    [Theory]
    [InlineData("source", false)]
    [InlineData("source", true)]
    [InlineData("full", false)]
    [InlineData("full", true)]
    [InlineData("ref", false)]
    [InlineData("ref", true)]
    public async Task OpaqueCollectionAndCanonicalArrayBackEdgesPreserveCycles(string image, bool copierFirst)
    {
        var producer = await GenerateAccepted("""
            namespace Producer;
            [Orleans.GenerateSerializer]
            public sealed class Node
            {
                [field: Orleans.Id(0)] public int Value { get; set; }
                [field: Orleans.Id(1)] public System.Collections.Generic.List<Node> Children { get; set; }
                [field: Orleans.Id(2)] public Node[] Flat { get; set; }
                [field: Orleans.Id(3)] public Node[][] Nested { get; set; }
            }
            [Orleans.GeneratedContextType<Node>]
            public partial class NodeContext : Orleans.Serialization.GeneratedContext { }
            """);
        var (output, result) = Generate(await Compile(RuntimeUsings + $$"""
            [Orleans.GenerateSerializer]
            public sealed class Box<T>
            {
                [Orleans.Id(0)] public System.Collections.Generic.List<T> Children { get; set; }
                [Orleans.Id(1)] public T[] Flat { get; set; }
                [Orleans.Id(2)] public T[][] Nested { get; set; }
            }
            [Orleans.GeneratedContextType<System.Collections.Generic.List<Producer.Node>>]
            [Orleans.GeneratedContextType<Box<Producer.Node>>]
            [Orleans.GeneratedContextImport<Producer.NodeContext>]
            public partial class ConsumerContext : Orleans.Serialization.GeneratedContext { }
            public static class RuntimeProof
            {
                public static object[] Run()
                {
                    using var services = new ServiceCollection().AddGeneratedContext(new ConsumerContext()).BuildServiceProvider();
                    var provider = services.GetRequiredService<CodecProvider>();
                    if ({{(copierFirst ? "true" : "false")}}) provider.GetDeepCopier<System.Collections.Generic.List<Producer.Node>>();
                    else provider.GetCodec<System.Collections.Generic.List<Producer.Node>>();
                    var serializer = services.GetRequiredService<Serializer>();
                    var copier = services.GetRequiredService<DeepCopier>();
                    var node = new Producer.Node { Value = 53 };
                    var children = new System.Collections.Generic.List<Producer.Node> { node, node };
                    var flat = new[] { node, node };
                    var nested = new[] { flat, flat };
                    node.Children = children; node.Flat = flat; node.Nested = nested;
                    var original = new Box<Producer.Node> { Children = children, Flat = flat, Nested = nested };
                    var restored = serializer.Deserialize<Box<Producer.Node>>(serializer.SerializeToArray(original));
                    var copy = copier.Copy(original);
                    copy.Children[0].Value = 59;
                    return new object[]
                    {
                        restored.Children.Count, restored.Children[0].Value,
                        ReferenceEquals(restored.Children[0], restored.Children[1]) && ReferenceEquals(restored.Children, restored.Children[0].Children),
                        ReferenceEquals(restored.Flat, restored.Children[0].Flat) && ReferenceEquals(restored.Flat[0], restored.Children[0]),
                        ReferenceEquals(restored.Nested, restored.Children[0].Nested) && ReferenceEquals(restored.Flat, restored.Nested[0]) && ReferenceEquals(restored.Nested[0], restored.Nested[1]),
                        node.Value, copy.Children[0].Value,
                        !ReferenceEquals(original, copy) && !ReferenceEquals(children, copy.Children) && !ReferenceEquals(node, copy.Children[0]) && !ReferenceEquals(flat, copy.Flat),
                        ReferenceEquals(copy.Children[0], copy.Children[1]) && ReferenceEquals(copy.Children, copy.Children[0].Children),
                        ReferenceEquals(copy.Flat, copy.Children[0].Flat) && ReferenceEquals(copy.Flat[0], copy.Children[0]),
                        ReferenceEquals(copy.Nested, copy.Children[0].Nested) && ReferenceEquals(copy.Flat, copy.Nested[0]) && ReferenceEquals(copy.Nested[0], copy.Nested[1])
                    };
                }
            }
            """, Reference(producer, image)));
        AssertAccepted(output, result);
        AssertOpaqueConsumer(result, "ConsumerContext", ("Node", "Node"));
        var context = ContextSource(result, "ConsumerContext");
        Assert.Contains("CreateCodecHolder<global::Producer.Node>", context, StringComparison.Ordinal);
        Assert.Contains("CreateCopierHolder<global::Producer.Node>", context, StringComparison.Ordinal);
        Assert.Equal(new object[] { 2, 53, true, true, true, 53, 59, true, true, true, true }, Run(output, producer));
    }

    [Fact]
    public async Task TransitiveDiamondAndRepeatedGeneratedLinksCompose()
    {
        var (output, result) = Generate(await Compile(RuntimeUsings + """
            public static class Visits
            {
                public static System.Collections.Generic.Dictionary<System.Type, int> Counts = new();
                public static object Key(System.Type type)
                { Counts.TryGetValue(type, out var count); Counts[type] = count + 1; return type; }
            }
            [Orleans.GenerateSerializer, Orleans.Alias("linked-leaf")]
            public sealed class Leaf { [Orleans.Id(0)] public int Value { get; set; } }
            [Orleans.GenerateSerializer, Orleans.Alias("linked-left")]
            public sealed class Left { [Orleans.Id(0)] public Leaf Value { get; set; } }
            [Orleans.GenerateSerializer, Orleans.Alias("linked-right")]
            public sealed class Right { [Orleans.Id(0)] public Leaf Value { get; set; } }
            [Orleans.GeneratedContextType<Leaf>]
            public partial class LeafContext : Orleans.Serialization.GeneratedContext
            { public override object Key => Visits.Key(typeof(LeafContext)); }
            [Orleans.GeneratedContextType<Left>]
            [Orleans.GeneratedContextImport<LeafContext>]
            public partial class LeftContext : Orleans.Serialization.GeneratedContext
            { public override object Key => Visits.Key(typeof(LeftContext)); }
            [Orleans.GeneratedContextType<Right>]
            [Orleans.GeneratedContextImport<LeafContext>]
            public partial class RightContext : Orleans.Serialization.GeneratedContext
            { public override object Key => Visits.Key(typeof(RightContext)); }
            [Orleans.GeneratedContextImport<LeftContext>]
            [Orleans.GeneratedContextImport<RightContext>]
            [Orleans.GeneratedContextImport<LeftContext>]
            public partial class EntryContext : Orleans.Serialization.GeneratedContext
            { public override object Key => Visits.Key(typeof(EntryContext)); }
            public static class RuntimeProof
            {
                public static object[] Run()
                {
                    var options = new TypeManifestOptions();
                    var entry = (IConfigureOptions<TypeManifestOptions>)new EntryContext();
                    entry.Configure(options);
                    entry.Configure(options);
                    var counts = new[] { Visits.Counts[typeof(EntryContext)], Visits.Counts[typeof(LeftContext)], Visits.Counts[typeof(RightContext)], Visits.Counts[typeof(LeafContext)] };
                    var first = options.WellKnownTypeAliases.Count;
                    entry.Configure(new TypeManifestOptions());
                    return new object[]
                    {
                        counts[0], counts[1], counts[2], counts[3], first,
                        options.WellKnownTypeAliases["linked-leaf"] == typeof(Leaf),
                        options.WellKnownTypeAliases["linked-left"] == typeof(Left),
                        options.WellKnownTypeAliases["linked-right"] == typeof(Right),
                        Visits.Counts[typeof(LeftContext)], Visits.Counts[typeof(RightContext)]
                    };
                }
            }
            """));
        AssertAccepted(output, result);
        Assert.Equal(4, ContextSources(result).Count());
        var entry = ContextSource(result, "EntryContext");
        Assert.Equal(1, Occurrences(entry, "new global::LeftContext()"));
        Assert.Equal(1, Occurrences(entry, "new global::RightContext()"));
        Assert.DoesNotContain("new global::LeafContext()", entry, StringComparison.Ordinal);
        // Key reads count attempts, not ConfigureInner executions. The independent probe below
        // pins executions, including equality of distinct key objects and per-options isolation.
        Assert.Equal(new object[] { 2, 1, 1, 2, 3, true, true, true, 2, 2 }, Run(output));
    }

    [Theory]
    [InlineData("self")]
    [InlineData("indirect")]
    [InlineData("same-root")]
    public async Task CyclicIncludesRejectGraphsWithoutAnOwningContext(string caseId)
    {
        if (caseId == "self")
        {
            var (_, result) = Generate(await Compile("""
                [Orleans.GeneratedContextType<int>]
                [Orleans.GeneratedContextImport<ConsumerContext>]
                public partial class ConsumerContext : Orleans.Serialization.GeneratedContext { }
                """));
            AssertRejected(result, "ORLEANS0115", "includes must be acyclic");
            return;
        }

        // Corrupt supplied declarations are compiled as metadata, not generated. The consumer
        // must diagnose the cycle itself, including the ownerless A/B case with identical roots.
        var producer = await Compile($$"""
            namespace Producer;
            [Orleans.GeneratedContextType<int>]
            [Orleans.GeneratedContextImport<B>]
            public class A : Orleans.Serialization.GeneratedContext
            { protected override void ConfigureInner(Orleans.Serialization.Configuration.TypeManifestOptions options) { } }
            [Orleans.GeneratedContextType<{{(caseId == "same-root" ? "int" : "string")}}>]
            [Orleans.GeneratedContextImport<A>]
            public class B : Orleans.Serialization.GeneratedContext
            { protected override void ConfigureInner(Orleans.Serialization.Configuration.TypeManifestOptions options) { } }
            """);
        var (_, rejected) = Generate(await Compile("""
            [Orleans.GeneratedContextImport<Producer.A>]
            public partial class ConsumerContext : Orleans.Serialization.GeneratedContext { }
            """, Reference(producer, "ref")));
        AssertRejected(rejected, "ORLEANS0115", "includes must be acyclic");
        Assert.Contains("owning context", Assert.Single(rejected.Diagnostics).GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingProviderKeysConfigureSharedAndCyclicGraphsExactlyOncePerOptions()
    {
        var root = new CountingProvider("root");
        var left = new CountingProvider("left");
        var right = new CountingProvider("right");
        var shared = new CountingProvider("shared");
        var equivalent = new CountingProvider(new string("shared".ToCharArray()));
        root.Links.AddRange([left, right, left]);
        left.Links.Add(shared);
        right.Links.AddRange([shared, equivalent]);
        shared.Links.Add(root);
        var options = new TypeManifestOptions();
        ((IConfigureOptions<TypeManifestOptions>)root).Configure(options);
        ((IConfigureOptions<TypeManifestOptions>)root).Configure(options);
        Assert.Equal(new[] { 1, 1, 1, 1, 0 }, new[] { root.Configurations, left.Configurations, right.Configurations, shared.Configurations, equivalent.Configurations });
        ((IConfigureOptions<TypeManifestOptions>)root).Configure(new TypeManifestOptions());
        Assert.Equal(new[] { 2, 2, 2, 2, 0 }, new[] { root.Configurations, left.Configurations, right.Configurations, shared.Configurations, equivalent.Configurations });
    }

    [Theory]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public async Task LinkedGraphDepthAccepts128LinksAndRejects129(int links, bool accepted)
    {
        var source = new StringBuilder("namespace Producer;\n");
        for (var index = 0; index < links; index++)
        {
            source.AppendLine(index == links - 1
                ? "[Orleans.GeneratedContextType<int>]"
                : $"[Orleans.GeneratedContextImport<C{index + 1}>]");
            source.AppendLine($"public partial class C{index} : Orleans.Serialization.GeneratedContext {{ }}");
        }
        var producer = await GenerateAccepted(source.ToString());
        var (output, result) = Generate(await Compile("""
            [Orleans.GeneratedContextImport<Producer.C0>]
            public partial class ConsumerContext : Orleans.Serialization.GeneratedContext { }
            """, Reference(producer, "ref")));
        if (accepted)
        {
            AssertAccepted(output, result);
            Assert.Equal(1, Occurrences(ContextSource(result, "ConsumerContext"), "new global::Producer.C0()"));
            Emit(output);
        }
        else AssertRejected(result, "ORLEANS0115", "128 nested dependencies or 1024 contexts");
    }

    [Fact]
    public async Task LinkedGraphBudgetCountsUniqueContextsIncludingConsumer()
    {
        // A flat graph avoids conflating the context budget with the independent depth bound.
        // All leaves supply the same int root, so the closed-type graph cannot mask this boundary.
        var source = new StringBuilder("namespace Producer;\n");
        for (var index = 0; index < 1024; index++)
            source.AppendLine($"[Orleans.GeneratedContextType<int>] public partial class C{index} : Orleans.Serialization.GeneratedContext {{ }}");
        var producer = await GenerateAccepted(source.ToString());
        var reference = Reference(producer, "ref");
        var includes = new StringBuilder();
        for (var index = 0; index < 1023; index++)
            includes.AppendLine($"[Orleans.GeneratedContextImport<Producer.C{index}>]");
        var declaration = "public partial class ConsumerContext : Orleans.Serialization.GeneratedContext { }";
        var (output, result) = Generate(await Compile(includes + declaration, reference));
        AssertAccepted(output, result);
        Assert.Equal(1023, Occurrences(ContextSource(result, "ConsumerContext"), ").Configure(options);"));
        Emit(output);
        var (duplicateOutput, duplicateResult) = Generate(await Compile(includes + "[Orleans.GeneratedContextImport<Producer.C0>]" + declaration, reference));
        AssertAccepted(duplicateOutput, duplicateResult);
        Assert.Equal(1023, Occurrences(ContextSource(duplicateResult, "ConsumerContext"), ").Configure(options);"));
        var (_, rejected) = Generate(await Compile(includes + "[Orleans.GeneratedContextImport<Producer.C1023>]" + declaration, reference));
        AssertRejected(rejected, "ORLEANS0115", "128 nested dependencies or 1024 contexts");
    }

    private sealed class CountingProvider(object key) : TypeManifestProviderBase
    {
        public override object Key => key;
        public List<CountingProvider> Links { get; } = [];
        public int Configurations { get; private set; }
        protected override void ConfigureInner(TypeManifestOptions options)
        {
            Configurations++;
            foreach (var link in Links) ((IConfigureOptions<TypeManifestOptions>)link).Configure(options);
        }
    }

    private static Task<CSharpCompilation> Compile(string source, params MetadataReference[] references)
        => TestCompilationHelper.CreateCompilation(source, $"LinkedContext{Guid.NewGuid():N}",
            [.. references,
                MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.DependencyInjection.ServiceProvider).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(IConfigureOptions<>).Assembly.Location)]);

    private static async Task<CSharpCompilation> GenerateAccepted(string source)
    {
        var (output, result) = Generate(await Compile(source));
        AssertAccepted(output, result);
        AssertNoAssemblyMetadata(output);
        return output;
    }

    private static (CSharpCompilation Output, GeneratorDriverRunResult Result) Generate(CSharpCompilation input)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        input = input.RemoveAllSyntaxTrees().AddSyntaxTrees(input.SyntaxTrees.Select(tree =>
            tree.WithRootAndOptions(tree.GetRoot(TestContext.Current.CancellationToken), parseOptions)));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new OrleansSerializationSourceGenerator().AsSourceGenerator()], parseOptions: parseOptions,
            optionsProvider: TestCompilationHelper.CreateOptionsProvider());
        driver = driver.RunGeneratorsAndUpdateCompilation(input, out var output, out _, TestContext.Current.CancellationToken);
        return ((CSharpCompilation)output, driver.GetRunResult());
    }

    private static void AssertAccepted(Compilation output, GeneratorDriverRunResult result)
    {
        Assert.DoesNotContain(result.Results, static result => result.Exception is not null);
        Assert.Empty(result.Diagnostics);
        AssertNoCompilerErrors(output);
    }

    private static void AssertNoCompilerErrors(Compilation output)
        => Assert.Empty(output.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

    private static void AssertRejected(GeneratorDriverRunResult result, string id, string message)
    {
        Assert.DoesNotContain(result.Results, static result => result.Exception is not null);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(id, diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains(message, diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    private static IEnumerable<GeneratedSourceResult> Sources(GeneratorDriverRunResult result)
        => result.Results.SelectMany(static result => result.GeneratedSources);

    private static IEnumerable<GeneratedSourceResult> ContextSources(GeneratorDriverRunResult result)
        => Sources(result).Where(static source => source.HintName.Contains(".context.", StringComparison.Ordinal));

    private static string ContextSource(GeneratorDriverRunResult result, string name)
        => Assert.Single(ContextSources(result), source => source.HintName.EndsWith($".{name}.g.cs", StringComparison.Ordinal)).SourceText.ToString();

    private static void AssertOpaqueConsumer(GeneratorDriverRunResult result, string contextName, params (string Name, string Root)[] models)
    {
        var context = ContextSource(result, contextName);
        var generated = string.Join("\n", Sources(result).Select(static source => source.SourceText.ToString()));
        foreach (var model in models)
        {
            // Ordinary local serializers can name public concrete producer services. They
            // resolve those services through the linked factories, without reconstructing
            // their graph. Only the consumer context owns registration/construction here.
            Assert.DoesNotContain($"Codec_{model.Name}", context, StringComparison.Ordinal);
            Assert.DoesNotContain($"Copier_{model.Name}", context, StringComparison.Ordinal);
            // Arrays/collections of a supplied model are local roots, not the supplied
            // exact root. Match the closing generic argument and invocation boundary.
            Assert.DoesNotContain($"AddSerializer<global::Producer.{model.Root}>(", context, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("AssemblyMetadata", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("MakeGenericType", generated, StringComparison.Ordinal);
    }

    private static void AssertNoAssemblyMetadata(Compilation output)
        => Assert.DoesNotContain(output.Assembly.GetAttributes(), static attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.Reflection.AssemblyMetadataAttribute");

    private static void AssertReferenceHidesContract(CSharpCompilation input, MetadataReference reference)
    {
        var assembly = Assert.IsAssignableFrom<IAssemblySymbol>(input.GetAssemblyOrModuleSymbol(reference));
        Assert.Contains(assembly.GetAttributes(), static attribute => attribute.AttributeClass?.Name == "ReferenceAssemblyAttribute");
        var all = input.WithOptions(input.Options.WithMetadataImportOptions(MetadataImportOptions.All));
        var payload = all.GetTypeByMetadataName("Producer.Payload`1")!;
        Assert.DoesNotContain(payload.GetMembers().OfType<IFieldSymbol>(), static field => !field.IsStatic);
        Assert.All(payload.GetMembers().OfType<IPropertySymbol>(), static property =>
            Assert.DoesNotContain(property.GetAttributes(), static attribute => attribute.AttributeClass?.Name is "IdAttribute" or "ImmutableAttribute"));
        var codec = all.GetTypeByMetadataName("OrleansCodeGen.Producer.Codec_Payload`1")!;
        Assert.NotNull(codec);
        Assert.DoesNotContain(codec.GetMembers().OfType<IFieldSymbol>(), static field => !field.IsStatic);
    }

    private static MetadataReference Reference(CSharpCompilation producer, string image)
        => image switch
        {
            "source" => producer.ToMetadataReference(),
            "full" => MetadataReference.CreateFromImage(Emit(producer)),
            "ref" => MetadataReference.CreateFromImage(Emit(producer, referenceOnly: true)),
            _ => throw new ArgumentOutOfRangeException(nameof(image))
        };

    private static byte[] Emit(Compilation compilation, bool referenceOnly = false)
    {
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream,
            options: new EmitOptions(metadataOnly: referenceOnly, includePrivateMembers: !referenceOnly),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return stream.ToArray();
    }

    private static object[] Run(CSharpCompilation consumer, params CSharpCompilation[] producers)
    {
        foreach (var producer in producers)
        {
            LoadIsolatedRuntimeAssembly(producer);
        }
        var assembly = LoadIsolatedRuntimeAssembly(consumer);
        try
        {
            return assembly.GetType("RuntimeProof", throwOnError: true)!.GetMethod("Run")!
                .CreateDelegate<Func<object[]>>()();
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static Assembly LoadIsolatedRuntimeAssembly(CSharpCompilation compilation)
    {
        // Default ALC images remain loaded for the module's lifetime. Synthetic producers
        // must not join later AddSerializer assembly discovery and collide on their IDs/
        // aliases. Strip only assembly discovery opt-ins, retaining model metadata, IVT,
        // context declarations and all generated services/factories.
        var discoveryAttributes = compilation.Assembly.GetAttributes()
            .Where(static attribute => IsAutomaticDiscoveryAttribute(attribute.AttributeClass?.ToDisplayString()))
            .Select(static attribute => attribute.ApplicationSyntaxReference!)
            .ToLookup(static reference => reference.SyntaxTree, static reference => reference.Span);
        var isolated = compilation;
        foreach (var tree in compilation.SyntaxTrees)
        {
            var removedSpans = discoveryAttributes[tree].ToHashSet();
            if (removedSpans.Count == 0) continue;
            var root = (CompilationUnitSyntax)tree.GetRoot(TestContext.Current.CancellationToken);
            var retained = root.AttributeLists
                .Select(list => list.WithAttributes(SyntaxFactory.SeparatedList(
                    list.Attributes.Where(attribute => !removedSpans.Contains(attribute.Span)))))
                .Where(static list => list.Attributes.Count > 0);
            isolated = isolated.ReplaceSyntaxTree(tree,
                tree.WithRootAndOptions(root.WithAttributeLists(SyntaxFactory.List(retained)), tree.Options));
        }

        Assert.DoesNotContain(isolated.Assembly.GetAttributes(), static attribute =>
            IsAutomaticDiscoveryAttribute(attribute.AttributeClass?.ToDisplayString()));
        using var stream = new MemoryStream(Emit(isolated));
        var assembly = AssemblyLoadContext.Default.LoadFromStream(stream);
        Assert.DoesNotContain(assembly.GetCustomAttributesData(), static attribute =>
            IsAutomaticDiscoveryAttribute(attribute.AttributeType.FullName));
        return assembly;
    }

    private static bool IsAutomaticDiscoveryAttribute(string? name)
        => name is "Orleans.Serialization.Configuration.TypeManifestProviderAttribute"
            or "Orleans.ApplicationPartAttribute";

    private static int Occurrences(string source, string value)
        => source.Split(value, StringSplitOptions.None).Length - 1;
}
