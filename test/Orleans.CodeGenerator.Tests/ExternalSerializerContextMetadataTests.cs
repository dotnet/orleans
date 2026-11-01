using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;

namespace Orleans.CodeGenerator.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("CodeGen")]
public sealed class ExternalSerializerContextMetadataTests
{
    private const string ProducerSource = """
        namespace Producer;
        [Orleans.GenerateSerializer]
        public sealed class Payload<T>
        {
            [Orleans.Id(0)] private System.Collections.Generic.List<T> _values = new();
            [Orleans.Id(1)] public T[] Flat { get; set; }
            [Orleans.Id(2)] public T[][] Nested { get; set; }
            public System.Collections.Generic.List<T> Values { get => _values; set => _values = value; }
        }
        """;

    [Theory]
    [InlineData("source", MetadataImportOptions.Public)]
    [InlineData("source", MetadataImportOptions.All)]
    [InlineData("full", MetadataImportOptions.Public)]
    [InlineData("full", MetadataImportOptions.All)]
    [InlineData("ref", MetadataImportOptions.Public)]
    [InlineData("ref", MetadataImportOptions.All)]
    public async Task ExternalModelsRequireCompleteImplementationMetadata(string image, MetadataImportOptions import)
    {
        var producer = await CreateProducer(ProducerSource);
        var reference = image == "source" ? producer.ToMetadataReference() : Emit(producer, image == "ref");
        var consumer = await CreateConsumer("Producer.Payload<byte>", reference);
        consumer = consumer.WithOptions(consumer.Options.WithMetadataImportOptions(import));
        var (output, result) = Generate(consumer);
        if (image == "ref")
        {
            AssertRejected(result);
            return;
        }

        AssertAccepted(output, result);
        var context = ContextSource(result);
        Assert.Contains("ListCodec<byte>", context, StringComparison.Ordinal);
        Assert.Contains("ListCopier<byte>", context, StringComparison.Ordinal);
        Assert.Contains("ArrayCodec<byte>", context, StringComparison.Ordinal);
        Assert.Contains("ArrayCopier<byte>", context, StringComparison.Ordinal);
        Assert.Contains("ByteArrayCodec", context, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("public")]
    [InlineData("private")]
    [InlineData("readonly")]
    [InlineData("getonly")]
    [InlineData("init")]
    public async Task FullMetadataPreservesPublicAndOpaqueContracts(string model)
    {
        var member = model switch
        {
            "public" => "[Orleans.Id(0)] public int Value { get; set; }",
            "private" => "[Orleans.Id(0)] private int _value;",
            "readonly" => "[Orleans.Id(0)] public readonly int Value;",
            "getonly" => "[Orleans.Id(0)] public int Value { get; }",
            "init" => "[Orleans.Id(0)] public int Value { get; init; }",
            _ => throw new ArgumentOutOfRangeException(nameof(model))
        };
        var producer = await CreateProducer($"[Orleans.GenerateSerializer] public sealed class Payload {{ {member} }}");
        var fullConsumer = await CreateConsumer("Payload", Emit(producer, false));
        var (output, result) = Generate(fullConsumer);
        AssertAccepted(output, result);
        var referenceConsumer = await CreateConsumer("Payload", Emit(producer, true));
        var (_, rejected) = Generate(referenceConsumer);
        AssertRejected(rejected);
    }

    [Theory]
    [InlineData("codec")]
    [InlineData("copier")]
    [InlineData("both")]
    public async Task FullModelsAlsoRequireFullGeneratedImplementations(string referenceOnly)
    {
        var models = await TestCompilationHelper.CreateCompilation("""
            namespace Producer;
            [Orleans.GenerateSerializer] public sealed class Payload { [Orleans.Id(0)] public int Value { get; set; } }
            """, $"Models{Guid.NewGuid():N}");
        var modelReference = Emit(models, false);
        var generated = await CreateProducer("[assembly: Orleans.GenerateCodeForDeclaringAssembly(typeof(Producer.Payload))]",
            additionalReferences: [modelReference]);
        var generatedTrees = generated.SyntaxTrees.Skip(1).ToArray();
        var codecTrees = generatedTrees.Where(tree => tree.ToString().Contains("sealed class Codec_Payload", StringComparison.Ordinal)).ToArray();
        var copierTrees = generatedTrees.Where(tree => tree.ToString().Contains("class Copier_Payload", StringComparison.Ordinal)).ToArray();
        Assert.Single(codecTrees);
        Assert.Single(copierTrees);
        var codec = CSharpCompilation.Create($"Codec{Guid.NewGuid():N}", SelectImplementation(codecTrees, "Codec_Payload"), generated.References, generated.Options);
        var copier = CSharpCompilation.Create($"Copier{Guid.NewGuid():N}", SelectImplementation(copierTrees, "Copier_Payload"), generated.References, generated.Options);
        var consumer = await CreateConsumer("Producer.Payload", modelReference);
        consumer = consumer.AddReferences(Emit(codec, referenceOnly is "codec" or "both"), Emit(copier, referenceOnly is "copier" or "both"));
        var (_, result) = Generate(consumer);
        AssertRejected(result);

        static IEnumerable<SyntaxTree> SelectImplementation(IEnumerable<SyntaxTree> trees, string name)
        {
            foreach (var tree in trees)
            {
                var root = (CompilationUnitSyntax)tree.GetRoot(TestContext.Current.CancellationToken);
                var ns = Assert.Single(root.Members.OfType<NamespaceDeclarationSyntax>());
                var retained = ns.Members.Where(member => member is ClassDeclarationSyntax type && type.Identifier.ValueText == name);
                yield return tree.WithRootAndOptions(root.WithAttributeLists(default).ReplaceNode(ns,
                    ns.WithMembers(SyntaxFactory.List(retained))), tree.Options);
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FullMetadataStillRequiresActualFriendAccess(bool friend)
    {
        var producer = await CreateProducer("""
            [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("FriendConsumer")]
            namespace Producer;
            [Orleans.GenerateSerializer] internal sealed class Payload { [Orleans.Id(0)] public int Value { get; set; } }
            """);
        var consumer = await CreateConsumer("Producer.Payload", Emit(producer, false),
            assemblyName: friend ? "FriendConsumer" : "OtherConsumer");
        var (output, result) = Generate(consumer);
        if (friend) AssertAccepted(output, result);
        else
        {
            // C# rejects an inaccessible generic attribute argument before a context can be generated.
            Assert.Empty(result.Diagnostics);
            Assert.NotEmpty(output.GetDiagnostics(TestContext.Current.CancellationToken)
                .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        }
    }

    [Fact]
    public async Task PrimitiveAndFrameworkCollectionRootsUseTheirExistingContracts()
    {
        var consumer = await TestCompilationHelper.CreateCompilation("""
            [Orleans.GenerateSerializerContext<System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, int>>>]
            public partial class DemoContext : Orleans.Serialization.SerializerContext { }
            """);
        var (output, result) = Generate(consumer);
        AssertAccepted(output, result);
        Assert.Contains("ListCodec<", ContextSource(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FSharpModelHeadersRequireFullGeneratedCSharpServices(bool referenceServices)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var configuration = typeof(ExternalSerializerContextMetadataTests).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        var tfm = AppContext.TargetFrameworkName!.Contains("Version=v8.0", StringComparison.Ordinal) ? "net8.0" : "net10.0";
        var modelDirectory = Path.Combine("bin", configuration, tfm);
        var serviceDirectory = referenceServices ? Path.Combine("obj", configuration, tfm, "ref") : Path.Combine("bin", configuration, tfm);
        var modelReference = MetadataReference.CreateFromFile(Path.Combine(root, "test", "Grains", "TestFSharp", modelDirectory, "TestFSharp.dll"));
        var consumer = await CreateConsumer("UnitTests.FSharpTypes.MulticaseStructDU", modelReference);
        consumer = consumer.AddReferences(
            MetadataReference.CreateFromFile(Path.Combine(root, "test", "Grains", "TestFSharpGrainInterfaces", serviceDirectory, "TestFSharpGrainInterfaces.dll")),
            MetadataReference.CreateFromFile(typeof(Microsoft.FSharp.Core.Unit).Assembly.Location),
            MetadataReference.CreateFromFile(Path.Combine(AppContext.BaseDirectory, "Orleans.Serialization.FSharp.dll")));
        var assembly = Assert.IsAssignableFrom<IAssemblySymbol>(consumer.GetAssemblyOrModuleSymbol(modelReference));
        Assert.DoesNotContain(assembly.GetAttributes(), static attribute =>
            attribute.AttributeClass?.Name == "ReferenceAssemblyAttribute");
        var (output, result) = Generate(consumer);
        if (referenceServices) AssertRejected(result);
        else
        {
            AssertAccepted(output, result);
            Assert.Contains("Codec_MulticaseStructDU", ContextSource(result), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task FullMetadataContextRoundTripsAndCopiesPrivateGenericState()
    {
        var producer = await CreateProducer(ProducerSource);
        var consumer = await CreateConsumer("Producer.Payload<byte>", Emit(producer, false), """
            public static class RuntimeProof
            {
                public static (int, int, bool, bool) Run()
                {
                    using var services = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(
                        Orleans.Serialization.ServiceCollectionExtensions.AddSerializerContext(
                            new Microsoft.Extensions.DependencyInjection.ServiceCollection(), new DemoContext()));
                    var serializer = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Orleans.Serialization.Serializer>(services);
                    var copier = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Orleans.Serialization.DeepCopier>(services);
                    var shared = new byte[] { 13, 17 };
                    var original = new Producer.Payload<byte> { Values = new() { 13, 17 }, Flat = shared, Nested = new[] { shared, shared } };
                    var restored = serializer.Deserialize<Producer.Payload<byte>>(serializer.SerializeToArray(original));
                    var copy = copier.Copy(original);
                    copy.Values[0] = 23;
                    return (restored.Values[0], original.Values[0],
                        object.ReferenceEquals(restored.Flat, restored.Nested[0]) && object.ReferenceEquals(restored.Nested[0], restored.Nested[1]),
                        !object.ReferenceEquals(original.Values, copy.Values) && copy.Values[0] == 23);
                }
            }
            """);
        consumer = consumer.AddReferences(MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.DependencyInjection.ServiceProvider).Assembly.Location));
        var (output, result) = Generate(consumer);
        AssertAccepted(output, result);
        using var producerImage = new MemoryStream(EmitBytes(producer, false));
        AssemblyLoadContext.Default.LoadFromStream(producerImage);
        using var consumerImage = new MemoryStream(EmitBytes(output, false));
        var proof = AssemblyLoadContext.Default.LoadFromStream(consumerImage).GetType("RuntimeProof", throwOnError: true)!;
        Assert.Equal((13, 13, true, true), proof.GetMethod("Run")!.CreateDelegate<Func<(int, int, bool, bool)>>()());
    }

    private static async Task<CSharpCompilation> CreateProducer(string source, params MetadataReference[] additionalReferences)
    {
        var compilation = await TestCompilationHelper.CreateCompilation(source, $"MetadataProducer{Guid.NewGuid():N}", additionalReferences);
        var (output, result) = Generate(compilation);
        AssertAccepted(output, result);
        Assert.DoesNotContain(output.Assembly.GetAttributes(), static attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.Reflection.AssemblyMetadataAttribute");
        return output;
    }

    private static async Task<CSharpCompilation> CreateConsumer(string type, MetadataReference reference, string proof = "", string? assemblyName = null)
        => await TestCompilationHelper.CreateCompilation($$"""
            [Orleans.GenerateSerializerContext<{{type}}>]
            public partial class DemoContext : Orleans.Serialization.SerializerContext { }
            {{proof}}
            """, assemblyName ?? $"MetadataConsumer{Guid.NewGuid():N}", reference);

    private static (CSharpCompilation Output, GeneratorDriverRunResult Result) Generate(CSharpCompilation compilation)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        compilation = compilation.RemoveAllSyntaxTrees().AddSyntaxTrees(compilation.SyntaxTrees.Select(tree =>
            tree.WithRootAndOptions(tree.GetRoot(TestContext.Current.CancellationToken), parseOptions)));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new OrleansSerializationSourceGenerator().AsSourceGenerator()], parseOptions: parseOptions,
            optionsProvider: TestCompilationHelper.CreateOptionsProvider(new Dictionary<string, string>
            {
#if NET8_0
                ["build_property.TargetFrameworkVersion"] = "v8.0"
#else
                ["build_property.TargetFrameworkVersion"] = "v10.0"
#endif
            }));
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _, TestContext.Current.CancellationToken);
        return ((CSharpCompilation)output, driver.GetRunResult());
    }

    private static void AssertAccepted(Compilation output, GeneratorDriverRunResult result)
    {
        Assert.Empty(result.Diagnostics);
        Assert.Empty(output.GetDiagnostics(TestContext.Current.CancellationToken).Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }

    private static void AssertRejected(GeneratorDriverRunResult result)
    {
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("ORLEANS0115", diagnostic.Id);
        Assert.Contains("full", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("implementation metadata", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    private static string ContextSource(GeneratorDriverRunResult result)
        => Assert.Single(result.Results.SelectMany(static result => result.GeneratedSources),
            static source => source.HintName.Contains(".context.", StringComparison.Ordinal)).SourceText.ToString();

    private static MetadataReference Emit(Compilation compilation, bool referenceOnly)
        => MetadataReference.CreateFromImage(EmitBytes(compilation, referenceOnly));

    private static byte[] EmitBytes(Compilation compilation, bool referenceOnly)
    {
        using var image = new MemoryStream();
        var result = compilation.Emit(image, options: new EmitOptions(metadataOnly: referenceOnly, includePrivateMembers: !referenceOnly),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return image.ToArray();
    }
}
