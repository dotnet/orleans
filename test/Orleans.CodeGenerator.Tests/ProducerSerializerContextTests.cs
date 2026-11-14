using System.IO;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace Orleans.CodeGenerator.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("CodeGen")]
public sealed class ProducerSerializerContextTests
{
    [Theory]
    [InlineData(MetadataImportOptions.Public)]
    [InlineData(MetadataImportOptions.All)]
    public async Task ExportedContextComposesWithConsumerContextUsingReferenceMetadata(MetadataImportOptions import)
    {
        var producer = await TestCompilationHelper.CreateCompilation("""
            namespace Producer;
            [Orleans.GenerateSerializer]
            public sealed class Payload
            {
                [Orleans.Id(0)] public System.Collections.Generic.List<int> Values { get; set; }
            }
            [Orleans.GeneratedContextType<Payload>]
            public partial class ExportedContext : Orleans.Serialization.GeneratedContext { }
            """, $"ContextProducer{Guid.NewGuid():N}");
        var (producerOutput, producerResult) = Generate(producer);
        AssertAccepted(producerOutput, producerResult);
        var consumer = await TestCompilationHelper.CreateCompilation("""
            [Orleans.GenerateSerializer]
            public sealed class LocalPayload { [Orleans.Id(0)] public int Value { get; set; } }
            [Orleans.GeneratedContextType<LocalPayload>]
            public partial class LocalContext : Orleans.Serialization.GeneratedContext { }
            public static class RuntimeProof
            {
                public static (int, int, int, bool) Run()
                {
                    var registrations = Orleans.Serialization.ServiceCollectionExtensions.AddGeneratedContext(
                        new Microsoft.Extensions.DependencyInjection.ServiceCollection(), new Producer.ExportedContext());
                    Orleans.Serialization.ServiceCollectionExtensions.AddGeneratedContext(registrations, new LocalContext());
                    using var services = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(registrations);
                    var serializer = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Orleans.Serialization.Serializer>(services);
                    var copier = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Orleans.Serialization.DeepCopier>(services);
                    var original = new Producer.Payload { Values = new() { 13, 17 } };
                    var restored = serializer.Deserialize<Producer.Payload>(serializer.SerializeToArray(original));
                    var copy = copier.Copy(original);
                    copy.Values[0] = 23;
                    var local = serializer.Deserialize<LocalPayload>(serializer.SerializeToArray(new LocalPayload { Value = 34 }));
                    return (restored.Values[0], original.Values[0], local.Value,
                        copy.Values[0] == 23 && !object.ReferenceEquals(original.Values, copy.Values));
                }
            }
            """, $"ContextConsumer{Guid.NewGuid():N}",
            MetadataReference.CreateFromImage(Emit(producerOutput, referenceOnly: true)),
            MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.DependencyInjection.ServiceProvider).Assembly.Location));
        consumer = consumer.WithOptions(consumer.Options.WithMetadataImportOptions(import));
        var (consumerOutput, consumerResult) = Generate(consumer);
        AssertAccepted(consumerOutput, consumerResult);
        var generated = string.Join("\n", consumerResult.Results.SelectMany(static result => result.GeneratedSources)
            .Select(static source => source.SourceText.ToString()));
        Assert.DoesNotContain("Codec_Payload", generated, StringComparison.Ordinal);
        using var producerImage = new MemoryStream(Emit(producerOutput, referenceOnly: false));
        AssemblyLoadContext.Default.LoadFromStream(producerImage);
        using var consumerImage = new MemoryStream(Emit(consumerOutput, referenceOnly: false));
        var proof = AssemblyLoadContext.Default.LoadFromStream(consumerImage).GetType("RuntimeProof", throwOnError: true)!;
        Assert.Equal((13, 13, 34, true), proof.GetMethod("Run")!.CreateDelegate<Func<(int, int, int, bool)>>()());
    }

    private static (CSharpCompilation Output, GeneratorDriverRunResult Result) Generate(CSharpCompilation compilation)
    {
        var options = new CSharpParseOptions(LanguageVersion.Preview);
        compilation = compilation.RemoveAllSyntaxTrees().AddSyntaxTrees(compilation.SyntaxTrees.Select(tree =>
            tree.WithRootAndOptions(tree.GetRoot(TestContext.Current.CancellationToken), options)));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new OrleansSerializationSourceGenerator().AsSourceGenerator()], parseOptions: options,
            optionsProvider: TestCompilationHelper.CreateOptionsProvider());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _, TestContext.Current.CancellationToken);
        return ((CSharpCompilation)output, driver.GetRunResult());
    }

    private static void AssertAccepted(Compilation output, GeneratorDriverRunResult result)
    {
        Assert.Empty(result.Diagnostics);
        Assert.Empty(output.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }

    private static byte[] Emit(Compilation compilation, bool referenceOnly)
    {
        using var image = new MemoryStream();
        var result = compilation.Emit(image,
            options: new EmitOptions(metadataOnly: referenceOnly, includePrivateMembers: !referenceOnly),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return image.ToArray();
    }
}
