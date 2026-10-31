using System.IO;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using Orleans.CodeGenerator.SyntaxGeneration;

namespace Orleans.CodeGenerator.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("CodeGen")]
public sealed class ReferenceVisibleSerializationContractTests
{
    private const string ProducerSource = """
        using System.Collections.Generic;
        using Orleans;
        namespace Producer;
        [GenerateSerializer]
        public sealed class Payload<T>
        {
            private List<T> _values = new();
            [Id(0)] public List<T> Values { get => _values; set => _values = value; }
            [Id(1)] public T[] Flat { get; set; }
            [Id(2)] public T[][] Nested { get; set; }
        }
        """;

    [Theory]
    [InlineData("source", MetadataImportOptions.Public)]
    [InlineData("source", MetadataImportOptions.All)]
    [InlineData("full", MetadataImportOptions.Public)]
    [InlineData("full", MetadataImportOptions.All)]
    [InlineData("ref", MetadataImportOptions.Public)]
    [InlineData("ref", MetadataImportOptions.All)]
    public async Task VisibleLogicalPropertiesPreserveGenericAndCanonicalArrayDependencies(string image, MetadataImportOptions import)
    {
        var (producer, _) = await CreateProducer(ProducerSource);
        Assert.Equal(ReferenceVisibleSerializationContract.Complete, Certificates(producer)["Producer.Payload`1"]);
        var reference = image == "source" ? producer.ToMetadataReference() : Emit(producer, image == "ref");
        var consumer = await CreateConsumer("Producer.Payload<byte>", reference);
        consumer = consumer.WithOptions(consumer.Options.WithMetadataImportOptions(import));
        var (output, result) = Generate(consumer);
        AssertAccepted(output, result);
        var context = ContextSource(result);
        Assert.Contains("ListCodec<byte>", context, StringComparison.Ordinal);
        Assert.Contains("ListCopier<byte>", context, StringComparison.Ordinal);
        Assert.Contains("ArrayCodec<byte>", context, StringComparison.Ordinal);
        Assert.Contains("ArrayCopier<byte>", context, StringComparison.Ordinal);
        Assert.Contains("ByteArrayCodec", context, StringComparison.Ordinal);
        Assert.Contains("Codec_Payload<byte>", context, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("v8.0")]
#if NET10_0_OR_GREATER
    [InlineData("v10.0")]
#endif
    public async Task ReferenceConsumerRoundTripsAndCopiesProducerImplementation(string framework)
    {
        var (producer, _) = await CreateProducer(ProducerSource, framework);
        var consumer = await CreateConsumer("Producer.Payload<byte>", Emit(producer, true), """
            public static class RuntimeProof
            {
                public static (int, int, int, int, int, bool, bool, bool) Run()
                {
                    using var services = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(
                        Orleans.Serialization.ServiceCollectionExtensions.AddSerializerContext(
                            new Microsoft.Extensions.DependencyInjection.ServiceCollection(), new DemoContext()));
                    var serializer = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Orleans.Serialization.Serializer>(services);
                    var copier = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Orleans.Serialization.DeepCopier>(services);
                    var values = new byte[] { 13, 17 };
                    var original = new Producer.Payload<byte> { Values = new() { 13, 17 }, Flat = values, Nested = new[] { values, values } };
                    var restored = serializer.Deserialize<Producer.Payload<byte>>(serializer.SerializeToArray(original));
                    var copy = copier.Copy(original);
                    copy.Flat[0] = 99;
                    copy.Values[0] = 23;
                    return (original.Flat[0], restored.Flat[0], copy.Flat[0], restored.Values[0], copy.Values[0],
                        object.ReferenceEquals(restored.Flat, restored.Nested[0]) && object.ReferenceEquals(restored.Nested[0], restored.Nested[1]),
                        object.ReferenceEquals(copy.Flat, copy.Nested[0]) && object.ReferenceEquals(copy.Nested[0], copy.Nested[1]),
                        !object.ReferenceEquals(original.Flat, copy.Flat) && !object.ReferenceEquals(original.Values, copy.Values));
                }
            }
            """);
        consumer = consumer.AddReferences(MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.DependencyInjection.ServiceProvider).Assembly.Location));
        var (output, result) = Generate(consumer);
        AssertAccepted(output, result);
        using var producerImage = new MemoryStream(EmitBytes(producer));
        AssemblyLoadContext.Default.LoadFromStream(producerImage);
        using var consumerImage = new MemoryStream(EmitBytes(output));
        var proof = AssemblyLoadContext.Default.LoadFromStream(consumerImage).GetType("RuntimeProof", throwOnError: true)!;
        var run = proof.GetMethod("Run")!.CreateDelegate<Func<(int, int, int, int, int, bool, bool, bool)>>();
        Assert.Equal((13, 13, 99, 13, 23, true, true, true), run());
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("primitive")]
    [InlineData("enum")]
    public async Task CompleteEmptyAndPrimitiveContractsSurviveReferenceEmission(string model)
    {
        var declaration = model switch
        {
            "empty" => "public sealed class Payload { }",
            "primitive" => "public sealed class Payload { [Orleans.Id(0)] public int Value { get; set; } }",
            "enum" => "public enum Payload { Zero, One }",
            _ => throw new ArgumentOutOfRangeException(nameof(model))
        };
        var (producer, _) = await CreateProducer("[Orleans.GenerateSerializer] " + declaration);
        Assert.Equal(ReferenceVisibleSerializationContract.Complete, Certificates(producer)["Payload"]);
        var consumer = await CreateConsumer("Payload", Emit(producer, true));
        var (output, result) = Generate(consumer);
        AssertAccepted(output, result);
        Assert.Contains("Codec_Payload", ContextSource(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sealed")]
    [InlineData("unsealed")]
    [InlineData("struct")]
    [InlineData("abstract")]
    [InlineData("activator")]
    [InlineData("constructor")]
    [InlineData("base")]
    [InlineData("callbacks")]
    public async Task CertificationPreservesExistingConstructionAndBaseRestrictions(string model)
    {
        var declaration = model switch
        {
            "sealed" => "public sealed class Payload { }",
            "unsealed" => "public class Payload { }",
            "struct" => "public struct Payload { [Orleans.Id(0)] public int Value; }",
            "abstract" => "public abstract class Payload { }",
            "activator" => "[Orleans.UseActivator] public sealed class Payload { }",
            "constructor" => "public sealed class Payload { public Payload(int value) { } }",
            "base" => "public sealed class Payload : Base { } public class Base { }",
            "callbacks" => """
                [Orleans.SerializationCallbacks(typeof(Callback))]
                public sealed class Payload { }
                public sealed class Callback { public void OnSerialized(Payload value) { } }
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(model))
        };
        var supported = model is "sealed" or "unsealed" or "struct";
        var (producer, _) = await CreateProducer("[Orleans.GenerateSerializer] " + declaration);
        Assert.Equal(supported ? ReferenceVisibleSerializationContract.Complete : ReferenceVisibleSerializationContract.Opaque,
            Certificates(producer)["Payload"]);
        foreach (var reference in new[] { producer.ToMetadataReference(), Emit(producer, false), Emit(producer, true) })
        {
            var consumer = await CreateConsumer("Payload", reference);
            var (output, result) = Generate(consumer);
            if (supported) AssertAccepted(output, result);
            else Assert.Single(result.Diagnostics, static diagnostic => diagnostic.Id == "ORLEANS0115");
        }
    }

    [Theory]
    [InlineData("private")]
    [InlineData("readonly")]
    [InlineData("getonly")]
    [InlineData("init")]
    [InlineData("private-setter")]
    [InlineData("field-id")]
    [InlineData("hidden-copy-policy")]
    public async Task OpaqueShapesSupportFullMetadataAndRejectReferencePlanning(string model)
    {
        var member = model switch
        {
            "private" => "[Orleans.Id(0)] private System.Collections.Generic.List<int> _value = new();",
            "readonly" => "[Orleans.Id(0)] public readonly int Value;",
            "getonly" => "[Orleans.Id(0)] public int Value { get; }",
            "init" => "[Orleans.Id(0)] public int Value { get; init; }",
            "private-setter" => "[Orleans.Id(0)] public int Value { get; private set; }",
            "field-id" => "[field: Orleans.Id(0)] public int Value { get; set; }",
            "hidden-copy-policy" => "[field: Orleans.Immutable] [Orleans.Id(0)] public System.Collections.Generic.List<int> Value { get; set; }",
            _ => throw new ArgumentOutOfRangeException(nameof(model))
        };
        var (producer, _) = await CreateProducer($"[Orleans.GenerateSerializer] public sealed class Payload {{ {member} }}");
        Assert.Equal(ReferenceVisibleSerializationContract.Opaque, Certificates(producer)["Payload"]);
        foreach (var reference in new[] { producer.ToMetadataReference(), Emit(producer, false) })
        {
            var consumer = await CreateConsumer("Payload", reference);
            var (output, result) = Generate(consumer);
            AssertAccepted(output, result);
        }

        var referenceConsumer = await CreateConsumer("Payload", Emit(producer, true));
        var (_, failure) = Generate(referenceConsumer);
        AssertRejected(failure, "producer-certified");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyTypeLocalImplicitSelectionCanBeCertified(bool typeLocal)
    {
        var attribute = typeLocal
            ? "[Orleans.GenerateSerializer(GenerateFieldIds = Orleans.GenerateFieldIds.PublicProperties)]"
            : "[Orleans.GenerateSerializer]";
        var source = $"{attribute} public sealed class Payload {{ public System.Collections.Generic.List<int> Value {{ get; set; }} }}";
        var compilation = await TestCompilationHelper.CreateCompilation(source);
        var (producer, produced) = Generate(compilation, globalSelection: true);
        AssertAccepted(producer, produced);
        Assert.Equal(typeLocal ? ReferenceVisibleSerializationContract.Complete : ReferenceVisibleSerializationContract.Opaque,
            Certificates(producer)["Payload"]);
        var consumer = await CreateConsumer("Payload", Emit(producer, true));
        var (output, result) = Generate(consumer);
        if (typeLocal)
        {
            AssertAccepted(output, result);
            Assert.Contains("ListCodec<int>", ContextSource(result), StringComparison.Ordinal);
        }
        else AssertRejected(result, "producer-certified");
    }

    [Theory]
    [InlineData(true, MetadataImportOptions.Public)]
    [InlineData(true, MetadataImportOptions.All)]
    [InlineData(false, MetadataImportOptions.Public)]
    [InlineData(false, MetadataImportOptions.All)]
    public async Task RetainedInternalContractsRequireActualFriendAccess(bool friend, MetadataImportOptions import)
    {
        var (producer, _) = await CreateProducer("""
            [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("FriendConsumer")]
            [Orleans.GenerateSerializer]
            public sealed class Payload { [Orleans.Id(0)] internal Hidden Value { get; set; } }
            [Orleans.GenerateSerializer]
            internal sealed class Hidden { [Orleans.Id(0)] public int Value { get; set; } }
            """);
        Assert.Equal(ReferenceVisibleSerializationContract.Complete, Certificates(producer)["Payload"]);
        var consumer = await CreateConsumer("Payload", Emit(producer, true),
            assemblyName: friend ? "FriendConsumer" : "OtherConsumer");
        consumer = consumer.WithOptions(consumer.Options.WithMetadataImportOptions(import));
        var (output, result) = Generate(consumer);
        if (friend)
        {
            AssertAccepted(output, result);
            Assert.Contains("Codec_Hidden", ContextSource(result), StringComparison.Ordinal);
        }
        else AssertRejected(result, "inaccessible");
    }

    [Fact]
    public async Task InternalsWithoutFriendRetentionAreOpaque()
    {
        var (producer, _) = await CreateProducer("""
            [Orleans.GenerateSerializer]
            public sealed class Payload { [Orleans.Id(0)] internal int Value { get; set; } }
            """);
        Assert.Equal(ReferenceVisibleSerializationContract.Opaque, Certificates(producer)["Payload"]);
        var consumer = await CreateConsumer("Payload", Emit(producer, true));
        var (_, result) = Generate(consumer);
        AssertRejected(result, "producer-certified");
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("empty")]
    [InlineData("null")]
    [InlineData("duplicate")]
    public async Task InvalidCertificatesFailExplicitly(string mutation)
    {
        var (producer, _) = await CreateProducer("[Orleans.GenerateSerializer] public sealed class Payload { }");
        producer = RewriteCertificate(producer, mutation);
        var consumer = await CreateConsumer("Payload", Emit(producer, true));
        var (_, result) = Generate(consumer);
        AssertRejected(result, "duplicated, malformed, or unsupported");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingCertificateRequiresFullMetadata(bool referenceOnly)
    {
        var (producer, _) = await CreateProducer(ProducerSource);
        producer = RemoveCertificates(producer);
        var consumer = await CreateConsumer("Producer.Payload<byte>", Emit(producer, referenceOnly));
        var (output, result) = Generate(consumer);
        if (referenceOnly) AssertRejected(result, "producer-certified");
        else
        {
            AssertAccepted(output, result);
            Assert.Contains("ListCodec<byte>", ContextSource(result), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ExternalSerializerProducerCannotCertifyAnotherAssemblysPrunedModel()
    {
        var raw = await TestCompilationHelper.CreateCompilation("""
            [Orleans.GenerateSerializer]
            public sealed class Payload { [Orleans.Id(0)] public int Value { get; set; } }
            """);
        var (generated, _) = await CreateProducer("[assembly: Orleans.GenerateCodeForDeclaringAssembly(typeof(Payload))]",
            additionalReferences: [Emit(raw, false)]);
        Assert.Empty(Certificates(generated));
        var consumer = await CreateConsumer("Payload", Emit(raw, true));
        consumer = consumer.AddReferences(Emit(generated, true));
        var (_, result) = Generate(consumer);
        AssertRejected(result, "producer-certified");
    }

    [Fact]
    public async Task BodiesPreserveReferenceBytesAndVisibleMetadataInvalidatesThem()
    {
        const string source = """
            [Orleans.GenerateSerializer]
            public sealed class Payload
            {
                [Orleans.Id(0)] public System.Collections.Generic.List<int> Values { get; set; }
                public int Read() => 1;
            }
            """;
        var (original, _) = await CreateProducer(source, assemblyName: "StableProducer");
        var (body, _) = await CreateProducer(source.Replace("=> 1", "=> 2", StringComparison.Ordinal), assemblyName: "StableProducer");
        var (type, _) = await CreateProducer(source.Replace("List<int>", "List<long>", StringComparison.Ordinal), assemblyName: "StableProducer");
        var (id, _) = await CreateProducer(source.Replace("Id(0)", "Id(3)", StringComparison.Ordinal), assemblyName: "StableProducer");
        Assert.Equal(ReferenceVisibleSerializationContract.Complete, Certificates(original)["Payload"]);
        Assert.Equal(EmitBytes(original, true), EmitBytes(body, true));
        Assert.NotEqual(EmitBytes(original, true), EmitBytes(type, true));
        Assert.NotEqual(EmitBytes(original, true), EmitBytes(id, true));
    }

    [Fact]
    public async Task AddingHiddenSerializedStateRevokesCertificationBeforePruning()
    {
        const string source = "[Orleans.GenerateSerializer] public sealed class Payload { [Orleans.Id(0)] public int Value { get; set; } }";
        var changed = source.Replace("public int Value", "private int _hidden; [Orleans.Id(1)] public int Value", StringComparison.Ordinal);
        var (original, _) = await CreateProducer(source, assemblyName: "StableProducer");
        var (updated, _) = await CreateProducer(changed, assemblyName: "StableProducer");
        Assert.Equal(ReferenceVisibleSerializationContract.Complete, Certificates(original)["Payload"]);
        Assert.Equal(ReferenceVisibleSerializationContract.Opaque, Certificates(updated)["Payload"]);
        Assert.NotEqual(EmitBytes(original, true), EmitBytes(updated, true));
        var consumer = await CreateConsumer("Payload", Emit(updated, true));
        var (_, result) = Generate(consumer);
        AssertRejected(result, "producer-certified");
    }

    [Fact]
    public async Task DeclarationInspectionPreservesNestedGenericArgumentsAndMemberCopyPolicy()
    {
        var (producer, _) = await CreateProducer("""
            [Orleans.GenerateSerializer]
            public sealed class Payload<T>
            {
                [Orleans.Id(0), Orleans.Immutable] public Outer<int>.Inner<T[]> Value { get; set; }
            }
            public class Outer<T> { public sealed class Inner<U> { } }
            """);
        var consumer = await CreateConsumer("Payload<byte>", Emit(producer, true));
        var target = consumer.GetTypeByMetadataName("Payload`1")!.Construct(consumer.GetSpecialType(SpecialType.System_Byte));
        var library = LibraryTypes.FromCompilation(consumer, new());
        Assert.True(new ReferenceVisibleSerializationContract.Index().TryDescribe(target, library, out var description, out var failure));
        Assert.Null(failure);
        var member = Assert.Single(description.Members);
        Assert.Equal("Outer<int>.Inner<byte[]>", member.Type.ToDisplayString());
        Assert.True(member.Symbol.HasAttribute(library.ImmutableAttribute));
    }

    [Fact]
    public async Task ShadowedDependencyIdentityProducesExplicitDiagnostic()
    {
        var (producer, _) = await CreateProducer("""
            namespace Producer;
            [Orleans.GenerateSerializer]
            public sealed class Payload { [Orleans.Id(0)] public Item Item { get; set; } }
            [Orleans.GenerateSerializer]
            public sealed class Item { [Orleans.Id(0)] public int Value { get; set; } }
            """);
        var consumer = await CreateConsumer("Producer.Payload", Emit(producer, true), "namespace Producer { public sealed class Item { } }");
        var (_, result) = Generate(consumer);
        AssertRejected(result, "cannot be named unambiguously");
    }

    [Fact]
    public async Task CompilerRetargetingPreservesDependencyIdentity()
    {
        const string source = "namespace Retargeted; [Orleans.GenerateSerializer] public sealed class Item { [Orleans.Id(0)] public int Value { get; set; } }";
        var (oldDependency, _) = await CreateProducer("[assembly: System.Reflection.AssemblyVersion(\"1.0.0.0\")]\n" + source,
            assemblyName: "RetargetedDependency");
        var (newDependency, _) = await CreateProducer("[assembly: System.Reflection.AssemblyVersion(\"2.0.0.0\")]\n" + source,
            assemblyName: "RetargetedDependency");
        var (producer, _) = await CreateProducer("""
            [Orleans.GenerateSerializer] public sealed class Payload { [Orleans.Id(0)] public Retargeted.Item Value { get; set; } }
            """, additionalReferences: [Emit(oldDependency, true)]);
        var consumer = await CreateConsumer("Payload", Emit(producer, true));
        consumer = consumer.AddReferences(Emit(newDependency, true));
        var (output, result) = Generate(consumer);
        AssertAccepted(output, result);
        Assert.Contains("Codec_Item", ContextSource(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WideGenericContractsKeepASmallClosedGraph()
    {
        var parameters = string.Join(", ", Enumerable.Range(0, 1100).Select(static index => $"T{index}"));
        var arguments = string.Join(", ", Enumerable.Repeat("int", 1100));
        var (producer, _) = await CreateProducer($"[Orleans.GenerateSerializer] public sealed class Wide<{parameters}> {{ }}");
        var consumer = await CreateConsumer($"Wide<{arguments}>", Emit(producer, true));
        var (output, result) = Generate(consumer);
        AssertAccepted(output, result);
        Assert.Contains("Codec_Wide<int, int,", ContextSource(result), StringComparison.Ordinal);
    }

    internal static async Task<(CSharpCompilation Output, GeneratorDriverRunResult Result)> CreateProducer(
        string source, string frameworkVersion = "v10.0", string? assemblyName = null, params MetadataReference[] additionalReferences)
    {
        var compilation = await TestCompilationHelper.CreateCompilation(source, assemblyName ?? $"ContractProducer{Guid.NewGuid():N}", additionalReferences);
        var (output, result) = Generate(compilation, frameworkVersion);
        AssertAccepted(output, result);
        return (output, result);
    }

    private static async Task<CSharpCompilation> CreateConsumer(string target, MetadataReference reference, string proof = "", string? assemblyName = null)
        => await TestCompilationHelper.CreateCompilation($$"""
            [Orleans.GenerateSerializerContext<{{target}}>]
            public partial class DemoContext : Orleans.Serialization.SerializerContext { }
            {{proof}}
            """, assemblyName ?? $"ContractConsumer{Guid.NewGuid():N}", reference);

    private static (CSharpCompilation Output, GeneratorDriverRunResult Result) Generate(
        CSharpCompilation compilation, string frameworkVersion = "v10.0", bool globalSelection = false)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        compilation = compilation.RemoveAllSyntaxTrees().AddSyntaxTrees(compilation.SyntaxTrees.Select(tree =>
            tree.WithRootAndOptions(tree.GetRoot(TestContext.Current.CancellationToken), parseOptions)));
        compilation = compilation.WithOptions(compilation.Options.WithDeterministic(true));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new OrleansSerializationSourceGenerator().AsSourceGenerator()], parseOptions: parseOptions,
            optionsProvider: TestCompilationHelper.CreateOptionsProvider(new Dictionary<string, string>
            {
                ["build_property.TargetFrameworkVersion"] = frameworkVersion,
                ["build_property.orleans_generatefieldids"] = globalSelection ? "PublicProperties" : "None"
            }));
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _, TestContext.Current.CancellationToken);
        return ((CSharpCompilation)output, driver.GetRunResult());
    }

    private static void AssertAccepted(Compilation output, GeneratorDriverRunResult result)
    {
        Assert.Empty(result.Diagnostics);
        Assert.Empty(output.GetDiagnostics(TestContext.Current.CancellationToken).Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }

    private static void AssertRejected(GeneratorDriverRunResult result, string reason)
    {
        var failure = Assert.Single(result.Diagnostics);
        Assert.Equal("ORLEANS0115", failure.Id);
        Assert.Contains(reason, failure.GetMessage(), StringComparison.Ordinal);
        Assert.DoesNotContain(result.Results.SelectMany(static value => value.GeneratedSources),
            static source => source.HintName.Contains(".context.", StringComparison.Ordinal));
    }

    private static string ContextSource(GeneratorDriverRunResult result)
        => Assert.Single(result.Results.SelectMany(static value => value.GeneratedSources),
            static source => source.HintName.Contains(".context.", StringComparison.Ordinal)).SourceText.ToString();

    private static Dictionary<string, string> Certificates(Compilation compilation)
        => compilation.Assembly.GetAttributes()
            .Where(static attribute => attribute.AttributeClass?.ToDisplayString() == "System.Reflection.AssemblyMetadataAttribute"
                && attribute.ConstructorArguments[0].Value is string key && key.StartsWith(ReferenceVisibleSerializationContract.KeyPrefix, StringComparison.Ordinal))
            .ToDictionary(static attribute => ((string)attribute.ConstructorArguments[0].Value!).Substring(ReferenceVisibleSerializationContract.KeyPrefix.Length),
                static attribute => (string)attribute.ConstructorArguments[1].Value!, StringComparer.Ordinal);

    private static CSharpCompilation RewriteCertificate(CSharpCompilation compilation, string mutation)
    {
        foreach (var tree in compilation.SyntaxTrees)
        {
            var root = (CompilationUnitSyntax)tree.GetRoot(TestContext.Current.CancellationToken);
            var certificate = root.AttributeLists.FirstOrDefault(static list =>
                list.Attributes.SingleOrDefault()?.ArgumentList?.Arguments[0].Expression is LiteralExpressionSyntax key
                && key.Token.ValueText.StartsWith(ReferenceVisibleSerializationContract.KeyPrefix, StringComparison.Ordinal));
            if (certificate is null) continue;
            var value = certificate.Attributes[0].ArgumentList!.Arguments[1].Expression;
            var replacement = mutation == "null" ? SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)
                : SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(mutation == "empty" ? "" : "2:complete"));
            root = mutation == "duplicate" ? root.AddAttributeLists(certificate.ReplaceNode(value, replacement)) : root.ReplaceNode(value, replacement);
            return compilation.ReplaceSyntaxTree(tree, tree.WithRootAndOptions(root, tree.Options));
        }

        throw new InvalidOperationException("The producer must have an eligibility certificate.");
    }

    private static CSharpCompilation RemoveCertificates(CSharpCompilation compilation)
    {
        foreach (var tree in compilation.SyntaxTrees.ToArray())
        {
            var root = (CompilationUnitSyntax)tree.GetRoot(TestContext.Current.CancellationToken);
            var retained = root.AttributeLists.Where(static list => !list.ToString().Contains(ReferenceVisibleSerializationContract.KeyPrefix, StringComparison.Ordinal));
            compilation = compilation.ReplaceSyntaxTree(tree, tree.WithRootAndOptions(root.WithAttributeLists(SyntaxFactory.List(retained)), tree.Options));
        }

        return compilation;
    }

    private static MetadataReference Emit(Compilation compilation, bool referenceAssembly)
        => MetadataReference.CreateFromImage(EmitBytes(compilation, referenceAssembly));

    private static byte[] EmitBytes(Compilation compilation, bool referenceAssembly = false)
    {
        using var image = new MemoryStream();
        var result = compilation.Emit(image, options: new EmitOptions(metadataOnly: referenceAssembly, includePrivateMembers: !referenceAssembly),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return image.ToArray();
    }
}
