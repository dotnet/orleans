using System.IO;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.Extensions.DependencyInjection;
using Orleans.CodeGenerator.Diagnostics;
using Orleans.Serialization;

namespace Orleans.CodeGenerator.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("CodeGen")]
public sealed class ExternalMemberAccessibilityTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExternalMemberTypesRequireActualConsumerAccessibility(bool peReference, bool friend)
    {
        var producer = await TestCompilationHelper.CreateCompilation(
            (friend ? """[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Consumer")]""" : "") + """

            namespace External;
            [Orleans.GenerateSerializer]
            public sealed class Payload
            {
                [Orleans.Id(0)] private System.Collections.Generic.List<Hidden> _values = new();
            }
            internal sealed class Hidden { }
            """, "Producer");
        var reference = peReference ? EmitReference(producer) : producer.ToMetadataReference();
        var consumer = await TestCompilationHelper.CreateCompilation("""
            [assembly: Orleans.GenerateCodeForDeclaringAssembly(typeof(External.Payload))]
            """, "Consumer", reference);
        consumer = consumer.WithOptions(consumer.Options.WithMetadataImportOptions(MetadataImportOptions.All));
        var (output, result) = Generate(consumer);

        if (friend)
        {
            AssertAccepted(output, result);
            Assert.Contains(result.GeneratedSources, static source =>
                source.SourceText.ToString().Contains("ListCodec<global::External.Hidden>", StringComparison.Ordinal));
        }
        else
        {
            var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
                diagnostic.Id == InaccessibleSerializableTypeDiagnostic.RuleId);
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
            Assert.Equal(Location.None, diagnostic.Location);
            Assert.Contains("External.Hidden", diagnostic.GetMessage(), StringComparison.Ordinal);
            Assert.DoesNotContain(result.GeneratedSources, static source => source.HintName.Contains(".ser.", StringComparison.Ordinal));
            Assert.Null(result.Exception);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicMemberTypesAndSupportedPrivateStateRetainExternalSerializerBehavior(bool peReference)
    {
        var producer = await TestCompilationHelper.CreateCompilation("""
            namespace External;
            [Orleans.GenerateSerializer]
            public sealed class Payload
            {
                [Orleans.Id(0)] private int _value;
                public void Set(int value) => _value = value;
                public int Get() => _value;
            }
            """, $"PrivateStateProducer{Guid.NewGuid():N}");
        var reference = peReference ? EmitReference(producer) : producer.ToMetadataReference();
        var consumer = await TestCompilationHelper.CreateCompilation("""
            [assembly: Orleans.GenerateCodeForDeclaringAssembly(typeof(External.Payload))]
            """, $"PrivateStateConsumer{Guid.NewGuid():N}", reference);
        consumer = consumer.WithOptions(consumer.Options.WithMetadataImportOptions(MetadataImportOptions.All));
        var (output, result) = Generate(consumer);
        AssertAccepted(output, result);

        using var producerImage = new MemoryStream(EmitBytes(producer));
        var payloadType = AssemblyLoadContext.Default.LoadFromStream(producerImage).GetType("External.Payload", throwOnError: true)!;
        using var serializerImage = new MemoryStream(EmitBytes(output));
        var serializerAssembly = AssemblyLoadContext.Default.LoadFromStream(serializerImage);
        using var services = new ServiceCollection().AddSerializer(builder => builder.AddAssembly(serializerAssembly)).BuildServiceProvider();
        var original = Activator.CreateInstance(payloadType)!;
        payloadType.GetMethod("Set")!.Invoke(original, [137]);
        var serializer = services.GetRequiredService<Serializer>();
        var restored = serializer.Deserialize<object>(serializer.SerializeToArray(original));
        var copy = services.GetRequiredService<DeepCopier>().Copy(original);
        Assert.Equal(137, payloadType.GetMethod("Get")!.Invoke(restored, null));
        Assert.Equal(137, payloadType.GetMethod("Get")!.Invoke(copy, null));
        Assert.NotSame(original, restored);
        Assert.NotSame(original, copy);
    }

    [Theory]
    [InlineData("private int _value;")]
    [InlineData("internal Hidden _value;")]
    public async Task ProducerOwnedMembersKeepExistingAccessRules(string member)
    {
        var compilation = await TestCompilationHelper.CreateCompilation($$"""
            [Orleans.GenerateSerializer]
            public sealed class Payload
            {
                [Orleans.Id(0)] {{member}}
            }
            internal sealed class Hidden { }
            """);
        var (output, result) = Generate(compilation);
        AssertAccepted(output, result);
        Assert.Contains(result.GeneratedSources, static source => source.SourceText.ToString()
            .Contains("class Codec_Payload", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReferenceOnlyRawModelsKeepTheExplicitDiagnostic()
    {
        var producer = await TestCompilationHelper.CreateCompilation("""
            [Orleans.GenerateSerializer]
            public sealed class Payload { [Orleans.Id(0)] public int Value { get; set; } }
            """, "Producer");
        var consumer = await TestCompilationHelper.CreateCompilation("""
            [assembly: Orleans.GenerateCodeForDeclaringAssembly(typeof(Payload))]
            """, "Consumer", EmitReference(producer, referenceOnly: true));
        var (_, result) = Generate(consumer);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Id == ReferenceAssemblyWithGenerateSerializerDiagnostic.DiagnosticId);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(Location.None, diagnostic.Location);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsupportedExternalSetterProducesTheExistingDiagnostic(bool peReference)
    {
        var producer = await TestCompilationHelper.CreateCompilation("""
            [Orleans.GenerateSerializer]
            public sealed class Payload
            {
                [Orleans.Id(0)] public int Value => 137;
            }
            """, "Producer");
        var consumer = await TestCompilationHelper.CreateCompilation("""
            [assembly: Orleans.GenerateCodeForDeclaringAssembly(typeof(Payload))]
            """, "Consumer", peReference ? EmitReference(producer) : producer.ToMetadataReference());
        var (_, result) = Generate(consumer);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Id == InaccessibleSetterDiagnostic.RuleId);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(Location.None, diagnostic.Location);
        Assert.Contains("Payload.Value", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Null(result.Exception);
    }

    [Fact]
    public async Task ProducerOwnedSetterDiagnosticsRetainTheirSourceLocation()
    {
        var compilation = await TestCompilationHelper.CreateCompilation("""
            [Orleans.GenerateSerializer]
            public sealed class Payload { [Orleans.Id(0)] public int Value => 137; }
            """);
        var (_, result) = Generate(compilation);
        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic =>
            diagnostic.Id == InaccessibleSetterDiagnostic.RuleId);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.True(diagnostic.Location.IsInSource);
        Assert.Equal("Value", diagnostic.Location.SourceTree!.GetText(TestContext.Current.CancellationToken)
            .ToString(diagnostic.Location.SourceSpan));
        Assert.Null(result.Exception);
    }

    private static (CSharpCompilation Output, GeneratorRunResult Result) Generate(CSharpCompilation compilation)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        compilation = compilation.RemoveAllSyntaxTrees().AddSyntaxTrees(compilation.SyntaxTrees.Select(tree =>
            tree.WithRootAndOptions(tree.GetRoot(TestContext.Current.CancellationToken), parseOptions)));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new OrleansSerializationSourceGenerator().AsSourceGenerator()], parseOptions: parseOptions,
            optionsProvider: TestCompilationHelper.CreateOptionsProvider());
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation, out var output, out _, TestContext.Current.CancellationToken);
        return ((CSharpCompilation)output, driver.GetRunResult().Results.Single());
    }

    private static void AssertAccepted(CSharpCompilation output, GeneratorRunResult result)
    {
        Assert.Empty(result.Diagnostics);
        Assert.Null(result.Exception);
        Assert.Empty(output.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }

    private static MetadataReference EmitReference(CSharpCompilation compilation, bool referenceOnly = false)
        => MetadataReference.CreateFromImage(EmitBytes(compilation, referenceOnly));

    private static byte[] EmitBytes(CSharpCompilation compilation, bool referenceOnly = false)
    {
        using var image = new MemoryStream();
        var result = compilation.Emit(image, options: new EmitOptions(
            metadataOnly: referenceOnly, includePrivateMembers: !referenceOnly),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return image.ToArray();
    }
}
