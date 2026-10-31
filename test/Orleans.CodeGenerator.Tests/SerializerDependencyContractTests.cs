using System.IO;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;

namespace Orleans.CodeGenerator.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("CodeGen")]
public sealed class SerializerDependencyContractTests
{
    private const string ProducerSource = """
        using System.Collections.Generic;
        using Orleans;
        namespace Producer;
        [GenerateSerializer]
        public sealed class Payload<T>
        {
            [Id(0)] private List<T> _values = new();
            [Id(1)] public T[] Flat { get; set; }
            [Id(2)] public T[][] Nested { get; set; }
            public List<T> Values { get => _values; set => _values = value; }
        }
        """;

    [Theory]
    [InlineData(false, MetadataImportOptions.Public)]
    [InlineData(false, MetadataImportOptions.All)]
    [InlineData(true, MetadataImportOptions.Public)]
    [InlineData(true, MetadataImportOptions.All)]
    public async Task WholeProducerContractsPreserveGenericPrivateAndCanonicalArrayDependencies(
        bool referenceImage, MetadataImportOptions metadataImport)
    {
        var (producer, _) = await CreateProducer(ProducerSource);
        var reference = referenceImage ? Emit(producer, referenceAssembly: true) : producer.ToMetadataReference();
        var consumer = await CreateConsumer("Producer.Payload<byte>", reference);
        consumer = consumer.WithOptions(consumer.Options.WithMetadataImportOptions(metadataImport));
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
    public async Task ConsumerCompiledAgainstReferenceImageRoundTripsAndCopiesProducerImplementation(string producerFramework)
    {
        var (producer, _) = await CreateProducer(ProducerSource, producerFramework);
        var consumer = await CreateConsumer("Producer.Payload<byte>", Emit(producer, referenceAssembly: true), """
            public static class RuntimeProof
            {
                public static (int Original, int Restored, int Copy, int RestoredValue, int CopyValue,
                    bool RestoredShared, bool CopyShared, bool Independent) Run()
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
        consumer = consumer.AddReferences(MetadataReference.CreateFromFile(
            typeof(Microsoft.Extensions.DependencyInjection.ServiceProvider).Assembly.Location));
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
    [InlineData("public sealed class Empty { }", "Empty")]
    [InlineData("public sealed class Primitive { [Orleans.Id(0)] public int Value { get; set; } }", "Primitive")]
    [InlineData("public enum Choice { Zero, One }", "Choice")]
    public async Task CompleteEmptyContractsAreRetainedInReferenceImages(string declaration, string name)
    {
        var (producer, _) = await CreateProducer("[Orleans.GenerateSerializer] " + declaration);
        var consumer = await CreateConsumer(name, Emit(producer, referenceAssembly: true));
        var (output, result) = Generate(consumer);
        AssertAccepted(output, result);
        Assert.Contains($"Codec_{name}", ContextSource(result), StringComparison.Ordinal);
        Assert.NotEmpty(Contracts(producer));
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
    public async Task ContractEligibilityMatchesExistingContextSupportAcrossSourceAndPeMetadata(string modelCase)
    {
        var declaration = modelCase switch
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
            _ => throw new ArgumentOutOfRangeException(nameof(modelCase))
        };
        var supported = modelCase is "sealed" or "unsealed" or "struct";
        var (producer, _) = await CreateProducer("[Orleans.GenerateSerializer] " + declaration);
        Assert.Equal(supported, Contracts(producer).Any(static contract => contract.Contains(":OrleansCodeGen.Codec_Payload=", StringComparison.Ordinal)));
        foreach (var reference in new[] { producer.ToMetadataReference(), Emit(producer, false), Emit(producer, true) })
        {
            var consumer = await CreateConsumer("Payload", reference);
            var (output, result) = Generate(consumer);
            if (supported)
            {
                AssertAccepted(output, result);
            }
            else
            {
                Assert.Single(result.Diagnostics, static diagnostic => diagnostic.Id == "ORLEANS0115");
            }
        }
    }

    [Fact]
    public async Task CodecAndCopierContractsFollowTheirOwnServiceSelection()
    {
        var (producer, _) = await CreateProducer("""
            [Orleans.GenerateSerializer]
            public sealed class Payload
            {
                [Orleans.Id(0), Orleans.Immutable]
                public System.Collections.Generic.List<int> Value;
            }
            """);
        var consumer = await CreateConsumer("Payload", Emit(producer, referenceAssembly: true));
        var producerAssembly = Assert.IsAssignableFrom<IAssemblySymbol>(consumer.GetAssemblyOrModuleSymbol(consumer.References.Last()));
        var index = new SerializerDependencyContract.Index(consumer);
        var codecDependencies = new List<ITypeSymbol>();
        var copierDependencies = new List<ITypeSymbol>();
        var target = consumer.GetTypeByMetadataName("Payload")!;
        Assert.True(index.TryRead(producerAssembly.GetTypeByMetadataName("OrleansCodeGen.Codec_Payload")!,
            target, codecDependencies, [], out var codecFailure));
        Assert.True(index.TryRead(producerAssembly.GetTypeByMetadataName("OrleansCodeGen.Copier_Payload")!,
            target, copierDependencies, [], out var copierFailure));
        Assert.Null(codecFailure);
        Assert.Null(copierFailure);
        Assert.Equal("System.Collections.Generic.List<int>", Assert.Single(codecDependencies).ToDisplayString());
        Assert.Empty(copierDependencies);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InternalDependenciesRequireFriendAccessibility(bool friend)
    {
        var source = (friend ? "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"FriendConsumer\")]\n" : "") + """
            [Orleans.GenerateSerializer]
            public sealed class Payload
            {
                [Orleans.Id(0)] private Hidden _value = new();
            }
            [Orleans.GenerateSerializer]
            internal sealed class Hidden { [Orleans.Id(0)] public int Value { get; set; } }
            """;
        var (producer, _) = await CreateProducer(source);
        var consumer = await CreateConsumer("Payload", Emit(producer, referenceAssembly: true), assemblyName: "FriendConsumer");
        var (output, result) = Generate(consumer);
        if (friend)
        {
            AssertAccepted(output, result);
            Assert.Contains("Codec_Hidden", ContextSource(result), StringComparison.Ordinal);
        }
        else
        {
            var failure = Assert.Single(result.Diagnostics, static diagnostic => diagnostic.Id == "ORLEANS0115");
            Assert.Contains("Hidden", failure.GetMessage(), StringComparison.Ordinal);
            Assert.DoesNotContain(result.Results.SelectMany(static value => value.GeneratedSources),
                static source => source.HintName.Contains(".context.", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("unknown", false)]
    [InlineData("unknown", true)]
    [InlineData("truncated", false)]
    [InlineData("truncated", true)]
    [InlineData("trailing", false)]
    [InlineData("trailing", true)]
    [InlineData("duplicate", false)]
    [InlineData("duplicate", true)]
    [InlineData("empty", false)]
    [InlineData("empty", true)]
    [InlineData("arity", true)]
    [InlineData("negative", true)]
    [InlineData("count", true)]
    [InlineData("ordinal", true)]
    [InlineData("rank", true)]
    [InlineData("rank-large", true)]
    [InlineData("depth", true)]
    [InlineData("size", true)]
    [InlineData("type-arity", true)]
    [InlineData("identity-empty", true)]
    public async Task PresentInvalidContractsFailClosedWithoutLegacyFallback(string mutation, bool referenceImage)
    {
        var (producer, _) = await CreateProducer("""
            [Orleans.GenerateSerializer]
            public sealed class Payload { [Orleans.Id(0)] public int Value { get; set; } }
            """);
        producer = MutateContract(producer, mutation);
        var consumer = await CreateConsumer("Payload", Emit(producer, referenceImage));
        var (_, result) = Generate(consumer);
        var failure = Assert.Single(result.Diagnostics, static diagnostic => diagnostic.Id == "ORLEANS0115");
        Assert.Contains("dependency contract", failure.GetMessage(), StringComparison.Ordinal);
        Assert.DoesNotContain(result.Results.SelectMany(static value => value.GeneratedSources),
            static source => source.HintName.Contains(".context.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbsentContractsPreserveLegacyFullMetadataAndRejectWholeProducerReferences(bool referenceImage)
    {
        var (producer, _) = await CreateProducer("""
            [Orleans.GenerateSerializer]
            public sealed class Payload { [Orleans.Id(0)] public System.Collections.Generic.List<int> Values { get; set; } }
            """);
        producer = RemoveContracts(producer);
        var consumer = await CreateConsumer("Payload", Emit(producer, referenceImage));
        var (output, result) = Generate(consumer);
        if (referenceImage)
        {
            var failure = Assert.Single(result.Diagnostics, static diagnostic => diagnostic.Id == "ORLEANS0115");
            Assert.Contains("complete generated implementation metadata", failure.GetMessage(), StringComparison.Ordinal);
        }
        else
        {
            AssertAccepted(output, result);
            Assert.Contains("ListCodec<int>", ContextSource(result), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task MethodBodiesLeaveContractsStableAndPrivateDependencyChangesInvalidateThem()
    {
        const string source = """
            [Orleans.GenerateSerializer]
            public sealed class Payload
            {
                [Orleans.Id(0)] private System.Collections.Generic.List<int> _values = new();
                public int Read() => 1;
            }
            """;
        var (original, _) = await CreateProducer(source, assemblyName: "StableProducer");
        var (bodyEdit, _) = await CreateProducer(source.Replace("=> 1", "=> 2", StringComparison.Ordinal), assemblyName: "StableProducer");
        var (dependencyEdit, _) = await CreateProducer(source.Replace("List<int>", "List<long>", StringComparison.Ordinal), assemblyName: "StableProducer");
        Assert.Equal(Contracts(original), Contracts(bodyEdit));
        Assert.NotEqual(Contracts(original), Contracts(dependencyEdit));
        Assert.Equal(EmitBytes(original, referenceAssembly: true), EmitBytes(bodyEdit, referenceAssembly: true));
        Assert.NotEqual(EmitBytes(original, referenceAssembly: true), EmitBytes(dependencyEdit, referenceAssembly: true));
    }

    [Theory]
    [InlineData("api")]
    [InlineData("public-id")]
    [InlineData("private-id")]
    public async Task ReferenceImagesTrackConsumerRelevantApiAndSerializationMetadata(string edit)
    {
        const string source = """
            [Orleans.GenerateSerializer]
            public sealed class Payload
            {
                [Orleans.Id(0)] public int Value { get; set; }
                [Orleans.Id(1)] private int _private;
            }
            """;
        var changed = edit switch
        {
            "api" => source.Replace("public int Value", "public long Value", StringComparison.Ordinal),
            "public-id" => source.Replace("Id(0)", "Id(3)", StringComparison.Ordinal),
            "private-id" => source.Replace("Id(1)", "Id(4)", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(edit))
        };
        var (original, _) = await CreateProducer(source, assemblyName: "MetadataProducer");
        var (updated, _) = await CreateProducer(changed, assemblyName: "MetadataProducer");
        Assert.Equal(Contracts(original), Contracts(updated));
        if (edit == "private-id")
        {
            Assert.Equal(EmitBytes(original, referenceAssembly: true), EmitBytes(updated, referenceAssembly: true));
        }
        else
        {
            Assert.NotEqual(EmitBytes(original, referenceAssembly: true), EmitBytes(updated, referenceAssembly: true));
        }
    }

    [Fact]
    public async Task NestedGenericDependenciesRetainContainingAndOwnArguments()
    {
        var (producer, _) = await CreateProducer("""
            [Orleans.GenerateSerializer]
            public sealed class Payload<T>
            {
                [Orleans.Id(0)] private Outer<int>.Inner<T[]> _value = new();
            }
            public class Outer<T>
            {
                public sealed class Inner<U> { [Orleans.Id(0)] public U Value { get; set; } }
            }
            """);
        var consumer = await CreateConsumer("Payload<byte>", Emit(producer, referenceAssembly: true));
        var producerAssembly = Assert.IsAssignableFrom<IAssemblySymbol>(consumer.GetAssemblyOrModuleSymbol(consumer.References.Last()));
        var index = new SerializerDependencyContract.Index(consumer);
        var dependencies = new List<ITypeSymbol>();
        Assert.True(index.TryRead(producerAssembly.GetTypeByMetadataName("OrleansCodeGen.Codec_Payload`1")!
            .Construct(consumer.GetSpecialType(SpecialType.System_Byte)), consumer.GetTypeByMetadataName("Payload`1")!
            .Construct(consumer.GetSpecialType(SpecialType.System_Byte)), dependencies, [], out var failure));
        Assert.Null(failure);
        Assert.Equal("Outer<int>.Inner<byte[]>", Assert.Single(dependencies).ToDisplayString());
    }

    [Fact]
    public async Task ShadowedDependencyIdentityProducesExplicitDiagnostic()
    {
        var (producer, _) = await CreateProducer("""
            namespace Producer;
            [Orleans.GenerateSerializer]
            public sealed class Payload { [Orleans.Id(0)] private Item _item = new(); }
            [Orleans.GenerateSerializer]
            public sealed class Item { [Orleans.Id(0)] public int Value { get; set; } }
            """);
        var consumer = await CreateConsumer("Producer.Payload", Emit(producer, referenceAssembly: true),
            "namespace Producer { public sealed class Item { } }");
        var (_, result) = Generate(consumer);
        var failure = Assert.Single(result.Diagnostics, static diagnostic => diagnostic.Id == "ORLEANS0115");
        Assert.Contains("cannot be named unambiguously", failure.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProducerReferenceIdentityFollowsCompilerRetargeting()
    {
        const string dependencySource = """
            namespace Retargeted;
            [Orleans.GenerateSerializer]
            public sealed class Item { [Orleans.Id(0)] public int Value { get; set; } }
            """;
        var (oldDependency, _) = await CreateProducer(
            "[assembly: System.Reflection.AssemblyVersion(\"1.0.0.0\")]\n" + dependencySource,
            assemblyName: "RetargetedDependency");
        var (newDependency, _) = await CreateProducer(
            "[assembly: System.Reflection.AssemblyVersion(\"2.0.0.0\")]\n" + dependencySource,
            assemblyName: "RetargetedDependency");
        var (producer, _) = await CreateProducer("""
            [Orleans.GenerateSerializer]
            public sealed class Payload { [Orleans.Id(0)] public Retargeted.Item Value { get; set; } }
            """, additionalReferences: [Emit(oldDependency, referenceAssembly: true)]);
        var consumer = await CreateConsumer("Payload", Emit(producer, referenceAssembly: true));
        consumer = consumer.AddReferences(Emit(newDependency, referenceAssembly: true));
        var (output, result) = Generate(consumer);
        AssertAccepted(output, result);
        Assert.Contains("Codec_Item", ContextSource(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepeatedGenericArgumentsDoNotConsumeTheClosedTypeBudget()
    {
        var fields = string.Join("\n", Enumerable.Range(0, 400).Select(static index =>
            $"[Orleans.Id({index})] public Box<Leaf{index}, T> Value{index};"));
        var leaves = string.Join("\n", Enumerable.Range(0, 400).Select(static index =>
            $"public sealed class Leaf{index} {{ }}"));
        var (producer, _) = await CreateProducer($$"""
            [Orleans.GenerateSerializer]
            public sealed class Payload<T> { {{fields}} }
            public sealed class Box<T, U> { }
            {{leaves}}
            """);
        var consumer = await CreateConsumer("Payload<byte>", Emit(producer, referenceAssembly: true));
        var producerAssembly = Assert.IsAssignableFrom<IAssemblySymbol>(consumer.GetAssemblyOrModuleSymbol(consumer.References.Last()));
        var argument = consumer.GetSpecialType(SpecialType.System_Byte);
        var dependencies = new List<ITypeSymbol>();
        var index = new SerializerDependencyContract.Index(consumer);
        Assert.True(index.TryRead(producerAssembly.GetTypeByMetadataName("OrleansCodeGen.Codec_Payload`1")!.Construct(argument),
            consumer.GetTypeByMetadataName("Payload`1")!.Construct(argument), dependencies, [], out var failure));
        Assert.Null(failure);
        Assert.Equal(400, dependencies.Count);
        Assert.Contains(dependencies, static dependency => dependency.ToDisplayString() == "Box<Leaf0, byte>");
        Assert.Contains(dependencies, static dependency => dependency.ToDisplayString() == "Box<Leaf399, byte>");
    }

    [Fact]
    public async Task WideGenericContractsPreserveAnOtherwiseSmallClosedTypeGraph()
    {
        var parameters = string.Join(", ", Enumerable.Range(0, 1100).Select(static index => $"T{index}"));
        var arguments = string.Join(", ", Enumerable.Repeat("int", 1100));
        var (producer, _) = await CreateProducer($$"""
            [Orleans.GenerateSerializer]
            public sealed class Wide<{{parameters}}> { }
            """);
        foreach (var reference in new[] { Emit(producer, false), Emit(producer, true) })
        {
            var consumer = await CreateConsumer($"Wide<{arguments}>", reference);
            var (output, result) = Generate(consumer);
            AssertAccepted(output, result);
            Assert.Contains("Codec_Wide<int, int,", ContextSource(result), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task DeepContainingTypesAreDiagnosedBeforeRecursiveConstruction()
    {
        var declarations = string.Concat(Enumerable.Range(0, 130).Select(static index => $"public class N{index} {{ "));
        var typeName = string.Join(".", Enumerable.Range(0, 130).Select(static index => $"N{index}"));
        var (producer, _) = await CreateProducer($$"""
            [Orleans.GenerateSerializer]
            public sealed class Payload { [Orleans.Id(0)] public {{typeName}} Value { get; set; } }
            {{declarations}}{{new string('}', 130)}}
            """);
        var consumer = await CreateConsumer("Payload", Emit(producer, referenceAssembly: true));
        var (_, result) = Generate(consumer);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic => diagnostic.Id == "ORLEANS0115");
        Assert.Contains("containing type nesting", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    internal static async Task<(CSharpCompilation Output, GeneratorDriverRunResult Result)> CreateProducer(
        string source, string frameworkVersion = "v10.0", string? assemblyName = null,
        params MetadataReference[] additionalReferences)
    {
        var compilation = await TestCompilationHelper.CreateCompilation(source, assemblyName ?? $"DependencyProducer{Guid.NewGuid():N}", additionalReferences);
        var (output, result) = Generate(compilation, frameworkVersion);
        AssertAccepted(output, result);
        return (output, result);
    }

    private static async Task<CSharpCompilation> CreateConsumer(
        string target, MetadataReference reference, string proof = "", string? assemblyName = null)
        => await TestCompilationHelper.CreateCompilation($$"""
            [Orleans.GenerateSerializerContext<{{target}}>]
            public partial class DemoContext : Orleans.Serialization.SerializerContext { }
            {{proof}}
            """, assemblyName ?? $"DependencyConsumer{Guid.NewGuid():N}", reference);

    private static (CSharpCompilation Output, GeneratorDriverRunResult Result) Generate(
        CSharpCompilation compilation, string frameworkVersion = "v10.0")
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        compilation = compilation.RemoveAllSyntaxTrees().AddSyntaxTrees(compilation.SyntaxTrees.Select(tree =>
            tree.WithRootAndOptions(tree.GetRoot(TestContext.Current.CancellationToken), parseOptions)));
        compilation = compilation.WithOptions(compilation.Options.WithDeterministic(true));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new OrleansSerializationSourceGenerator().AsSourceGenerator()], parseOptions: parseOptions,
            optionsProvider: TestCompilationHelper.CreateOptionsProvider(new Dictionary<string, string>
            {
                ["build_property.TargetFrameworkVersion"] = frameworkVersion
            }));
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _, TestContext.Current.CancellationToken);
        return ((CSharpCompilation)output, driver.GetRunResult());
    }

    private static void AssertAccepted(Compilation output, GeneratorDriverRunResult result)
    {
        Assert.Empty(result.Diagnostics);
        Assert.Empty(output.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }

    private static string ContextSource(GeneratorDriverRunResult result)
        => Assert.Single(result.Results.SelectMany(static value => value.GeneratedSources),
            static source => source.HintName.Contains(".context.", StringComparison.Ordinal)).SourceText.ToString();

    private static string[] Contracts(Compilation compilation)
        => compilation.Assembly.GetAttributes()
            .Where(static attribute => attribute.AttributeClass?.ToDisplayString() == "System.Reflection.AssemblyMetadataAttribute"
                && attribute.ConstructorArguments[0].Value is string key && key.StartsWith(SerializerDependencyContract.KeyPrefix, StringComparison.Ordinal))
            .Select(static attribute => attribute.ConstructorArguments[0].Value + "=" + attribute.ConstructorArguments[1].Value)
            .Order(StringComparer.Ordinal).ToArray();

    private static CSharpCompilation MutateContract(CSharpCompilation compilation, string mutation)
    {
        foreach (var tree in compilation.SyntaxTrees)
        {
            var root = (CompilationUnitSyntax)tree.GetRoot(TestContext.Current.CancellationToken);
            var contract = root.AttributeLists.FirstOrDefault(static list =>
                list.Attributes.SingleOrDefault()?.ArgumentList?.Arguments[0].Expression is LiteralExpressionSyntax key
                && key.Token.ValueText.Contains("Codec_Payload", StringComparison.Ordinal));
            if (contract is null)
            {
                continue;
            }

            if (mutation == "duplicate")
            {
                var existing = (LiteralExpressionSyntax)contract.Attributes[0].ArgumentList!.Arguments[1].Expression;
                var duplicate = contract.ReplaceNode(existing, SyntaxFactory.LiteralExpression(
                    SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(existing.Token.ValueText + "x")));
                return compilation.ReplaceSyntaxTree(tree, tree.WithRootAndOptions(root.AddAttributeLists(duplicate), tree.Options));
            }

            var value = (LiteralExpressionSyntax)contract.Attributes[0].ArgumentList!.Arguments[1].Expression;
            var original = value.Token.ValueText;
            var replacement = mutation switch
            {
                "unknown" => "999;" + original.Substring(original.IndexOf(';') + 1),
                "truncated" => original.Substring(0, original.Length - 1),
                "trailing" => original + "x",
                "empty" => "",
                "arity" => "1;1;" + original.Substring(4),
                "negative" => "1;-1;" + original.Substring(4),
                "count" => original.Substring(0, original.Length - 4) + "1025;0;",
                "ordinal" => original.Substring(0, original.Length - 4) + "1;p0;0;",
                "rank" => original.Substring(0, original.Length - 4) + "0;1;a1;",
                "rank-large" => original.Substring(0, original.Length - 4) + "0;1;a2147483647;",
                "depth" => original.Substring(0, original.Length - 4) + "1;" + new string('v', 128) + "p0;0;",
                "size" => original + new string('x', 1024 * 1024),
                "type-arity" => original.Substring(0, original.Length - 6) + "1;p0;0;0;",
                "identity-empty" => original.Substring(0, original.Length - 4) + "1;n0:0:0;0;",
                _ => throw new ArgumentOutOfRangeException(nameof(mutation))
            };
            root = root.ReplaceNode(value, SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(replacement)));
            return compilation.ReplaceSyntaxTree(tree, tree.WithRootAndOptions(root, tree.Options));
        }

        throw new InvalidOperationException("The generated codec must have a dependency contract.");
    }

    private static CSharpCompilation RemoveContracts(CSharpCompilation compilation)
    {
        foreach (var tree in compilation.SyntaxTrees.ToArray())
        {
            var root = (CompilationUnitSyntax)tree.GetRoot(TestContext.Current.CancellationToken);
            var retained = root.AttributeLists.Where(static list => !list.ToString().Contains(SerializerDependencyContract.KeyPrefix, StringComparison.Ordinal));
            compilation = compilation.ReplaceSyntaxTree(tree, tree.WithRootAndOptions(
                root.WithAttributeLists(SyntaxFactory.List(retained)), tree.Options));
        }

        return compilation;
    }

    private static MetadataReference Emit(Compilation compilation, bool referenceAssembly)
        => MetadataReference.CreateFromImage(EmitBytes(compilation, referenceAssembly));

    private static byte[] EmitBytes(Compilation compilation, bool referenceAssembly = false)
    {
        using var image = new MemoryStream();
        var result = compilation.Emit(image, options: new EmitOptions(
            metadataOnly: referenceAssembly, includePrivateMembers: !referenceAssembly),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return image.ToArray();
    }
}
