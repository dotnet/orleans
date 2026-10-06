using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using Orleans.CodeGenerator;
using Xunit;

namespace Orleans.CodeGenerator.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("CodeGen")]
public sealed class ReferencedSerializerContextTests
{
    [Fact]
    public async Task ReferencedNet8DynamicImplementationsRoundTripAndCopy()
    {
        var producer = await TestCompilationHelper.CreateCompilation("""
            [Orleans.GenerateSerializer]
            public class DynamicPayload<T>
            {
                [Orleans.Id(0)] public readonly T Value;
                public DynamicPayload() { }
                public DynamicPayload(T value) { Value = value; }
            }
            """, $"DynamicProducer{Guid.NewGuid():N}");
        var (producerOutput, producerResult) = Generate(producer, "v8.0", hotReload: false);
        Assert.Empty(producerResult.Diagnostics);
        AssertNoCompilationErrors(producerOutput);
        Assert.Contains("Utilities.FieldAccessor", string.Join("\n",
            producerResult.Results.SelectMany(static result => result.GeneratedSources)
                .Select(static source => source.SourceText.ToString())), StringComparison.Ordinal);
        var consumer = await TestCompilationHelper.CreateCompilation("""
            [Orleans.GenerateSerializerContext<DynamicPayload<System.Collections.Generic.List<int>>>]
            public partial class DemoContext : Orleans.Serialization.SerializerContext { }
            public static class DynamicProducerProof
            {
                public static bool Run()
                {
                    using var services = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(
                        Orleans.Serialization.ServiceCollectionExtensions.AddSerializerContext(
                            new Microsoft.Extensions.DependencyInjection.ServiceCollection(), new DemoContext()));
                    var serializer = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Orleans.Serialization.Serializer>(services);
                    var copier = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Orleans.Serialization.DeepCopier>(services);
                    var provider = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Orleans.Serialization.Serializers.CodecProvider>(services);
                    if (!object.ReferenceEquals(provider.GetCodec<DynamicPayload<System.Collections.Generic.List<int>>>(),
                            provider.GetBaseCodec<DynamicPayload<System.Collections.Generic.List<int>>>())
                        || !object.ReferenceEquals(provider.GetDeepCopier<DynamicPayload<System.Collections.Generic.List<int>>>(),
                            provider.GetBaseCopier<DynamicPayload<System.Collections.Generic.List<int>>>())) return false;
                    var original = new DynamicPayload<System.Collections.Generic.List<int>>(new() { 13, 17 });
                    var restored = serializer.Deserialize<DynamicPayload<System.Collections.Generic.List<int>>>(serializer.SerializeToArray(original));
                    var copy = copier.Copy(original);
                    copy.Value[0] = 23;
                    return restored.Value[0] == 13 && restored.Value[1] == 17
                        && original.Value[0] == 13 && copy.Value[0] == 23 && copy.Value[1] == 17
                        && !object.ReferenceEquals(original, restored)
                        && !object.ReferenceEquals(original, copy)
                        && !object.ReferenceEquals(original.Value, copy.Value);
                }
            }
            """, $"DynamicConsumer{Guid.NewGuid():N}", EmitReference(producerOutput));
        consumer = consumer.AddReferences(
            MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.DependencyInjection.ServiceProvider).Assembly.Location));
        var (consumerOutput, consumerResult) = Generate(consumer, "v10.0", hotReload: false);
        Assert.Empty(consumerResult.Diagnostics);
        AssertNoCompilationErrors(consumerOutput);
        var executable = producerOutput.AddSyntaxTrees(consumerOutput.SyntaxTrees)
            .AddReferences(MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.DependencyInjection.ServiceProvider).Assembly.Location));
        foreach (var tree in executable.SyntaxTrees)
        {
            var root = (CompilationUnitSyntax)tree.GetRoot(TestContext.Current.CancellationToken);
            executable = executable.ReplaceSyntaxTree(tree,
                tree.WithRootAndOptions(root.WithAttributeLists(default), tree.Options));
        }
        using var image = new MemoryStream();
        var emitted = executable.Emit(image, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        var assembly = System.Reflection.Assembly.Load(image.ToArray());
        Assert.Equal(true, assembly.GetType("DynamicProducerProof")!.GetMethod("Run")!.Invoke(null, null));
    }

    [Theory]
    [InlineData("v8.0", MetadataImportOptions.Public)]
    [InlineData("v8.0", MetadataImportOptions.All)]
    [InlineData("v10.0", MetadataImportOptions.Public)]
    [InlineData("v10.0", MetadataImportOptions.All)]
    public Task ReferencedImplicitMemberDependenciesUseProducerImplementations(
        string frameworkVersion, MetadataImportOptions metadataImport)
        => VerifyProducerDependencies(frameworkVersion, metadataImport, privateField: false);

    [Theory]
    [InlineData(MetadataImportOptions.Public)]
    [InlineData(MetadataImportOptions.All)]
    public Task ReferencedStaticPrivateFieldRoundTripsAndCopies(MetadataImportOptions metadataImport)
        => VerifyProducerDependencies("v10.0", metadataImport, privateField: true);

    private static async Task VerifyProducerDependencies(
        string frameworkVersion, MetadataImportOptions metadataImport, bool privateField)
    {
        var declaration = privateField ? "ImplicitPayload" : "ImplicitPayload<T>";
        var closedType = privateField ? "ImplicitPayload" : "ImplicitPayload<byte>";
        var members = privateField ? """
                [Orleans.Id(0)] private System.Collections.Generic.List<int> _values = new();
                public System.Collections.Generic.List<int> Values { get => _values; set => _values = value; }
                [Orleans.Id(1)] public byte[] Flat { get; set; } = System.Array.Empty<byte>();
                [Orleans.Id(2)] public byte[][] Nested { get; set; } = System.Array.Empty<byte[]>();
                """ : """
                public System.Collections.Generic.List<T> Values { get; set; } = new();
                public T[] Flat { get; set; } = System.Array.Empty<T>();
                public T[][] Nested { get; set; } = System.Array.Empty<T[]>();
                """;
        var producer = await TestCompilationHelper.CreateCompilation($$"""
            [Orleans.GenerateSerializer]
            public sealed class {{declaration}}
            {
                {{members}}
            }
            """, $"ImplicitProducer{Guid.NewGuid():N}");
        var (producerOutput, producerResult) = Generate(producer, frameworkVersion, hotReload: false,
            generateFieldIds: "PublicProperties");
        Assert.Empty(producerResult.Diagnostics);
        AssertNoCompilationErrors(producerOutput);
        var reference = EmitReference(producerOutput);
        var consumer = await TestCompilationHelper.CreateCompilation($$"""
            [Orleans.GenerateSerializerContext<{{closedType}}>]
            public partial class DemoContext : Orleans.Serialization.SerializerContext { }
            """, $"ImplicitConsumer{Guid.NewGuid():N}", reference);
        consumer = consumer.WithOptions(consumer.Options.WithMetadataImportOptions(metadataImport));
        var (output, result) = Generate(consumer, frameworkVersion, hotReload: false);
        Assert.Empty(result.Diagnostics);
        AssertNoCompilationErrors(output);
        var context = Assert.Single(result.Results.SelectMany(static result => result.GeneratedSources),
            static source => source.HintName.Contains(".context.", StringComparison.Ordinal)).SourceText.ToString();
        Assert.Contains(privateField ? "ListCodec<int>" : "ListCodec<byte>", context, StringComparison.Ordinal);
        Assert.Contains(privateField ? "ListCopier<int>" : "ListCopier<byte>", context, StringComparison.Ordinal);
        if (!privateField)
        {
            Assert.Contains("ArrayCodec<byte>", context, StringComparison.Ordinal);
            Assert.Contains("ArrayCopier<byte>", context, StringComparison.Ordinal);
        }
        Assert.Contains("ByteArrayCodec", context, StringComparison.Ordinal);

        var proof = CSharpSyntaxTree.ParseText($$"""
            public static class ProducerGraphProof
            {
                public static bool Run()
                {
                    using var services = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(
                        Orleans.Serialization.ServiceCollectionExtensions.AddSerializerContext(
                            new Microsoft.Extensions.DependencyInjection.ServiceCollection(), new DemoContext()));
                    var serializer = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Orleans.Serialization.Serializer>(services);
                    var copier = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Orleans.Serialization.DeepCopier>(services);
                    var values = new byte[] { 13, 17 };
                    var original = new {{closedType}} { Values = new() { 13, 17 }, Flat = values, Nested = new[] { values, values } };
                    var result = serializer.Deserialize<{{closedType}}>(serializer.SerializeToArray(original));
                    var copy = copier.Copy(original);
                    copy.Values[0] = 23;
                    copy.Nested[0][0] = 29;
                    return result.Values[0] == 13 && result.Values[1] == 17
                        && object.ReferenceEquals(result.Flat, result.Nested[0])
                        && object.ReferenceEquals(result.Nested[0], result.Nested[1])
                        && original.Values[0] == 13 && original.Nested[0][0] == 13
                        && copy.Values[0] == 23 && copy.Flat[0] == 29 && copy.Nested[1][0] == 29
                        && !object.ReferenceEquals(original.Values, copy.Values)
                        && !object.ReferenceEquals(original.Nested[0], copy.Nested[0]);
                }
            }
            """, new CSharpParseOptions(LanguageVersion.Preview), cancellationToken: TestContext.Current.CancellationToken);
        var executable = producerOutput.AddSyntaxTrees(output.SyntaxTrees)
            .AddSyntaxTrees(proof)
            .AddReferences(MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.DependencyInjection.ServiceProvider).Assembly.Location));
        foreach (var tree in executable.SyntaxTrees)
        {
            var root = (CompilationUnitSyntax)tree.GetRoot(TestContext.Current.CancellationToken);
            executable = executable.ReplaceSyntaxTree(tree,
                tree.WithRootAndOptions(root.WithAttributeLists(default), tree.Options));
        }
        using var image = new MemoryStream();
        var emitted = executable.Emit(image, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        var assembly = System.Reflection.Assembly.Load(image.ToArray());
        Assert.Equal(true, assembly.GetType("ProducerGraphProof")!.GetMethod("Run")!.Invoke(null, null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReferencedGenericReadonlyFieldAcceptsNet8Producer(bool referenceAssembly)
    {
        var reference = await CompileProducer("[Id(0)] public readonly T Value;", generic: true,
            frameworkVersion: "v8.0", hotReload: false, referenceAssembly,
            dynamicCodec: true, dynamicCopier: true);
        await AssertAccepted(reference);
    }

    [Theory]
    [InlineData("v8.0", false, true, false)]
    [InlineData("v8.0", false, true, true)]
    [InlineData("v10.0", true, true, false)]
    [InlineData("v10.0", true, true, true)]
    [InlineData("v10.0", true, false, false)]
    [InlineData("v10.0", true, false, true)]
    public async Task ReferencedPrivateFieldAcceptsDynamicProducer(
        string frameworkVersion, bool hotReload, bool generic, bool referenceAssembly)
    {
        var memberType = generic ? "T" : "int";
        var reference = await CompileProducer(
            $"[Id(0)] private {memberType} _value; public {memberType} Value => _value;",
            generic, frameworkVersion, hotReload, referenceAssembly, dynamicCodec: true, dynamicCopier: true);
        await AssertAccepted(reference, generic);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task ReferencedHotReloadReadonlyFieldAcceptsDynamicProducer(bool generic, bool referenceAssembly)
    {
        var reference = await CompileProducer($"[Id(0)] public readonly {(generic ? "T" : "int")} Value;",
            generic, frameworkVersion: "v10.0", hotReload: true, referenceAssembly,
            dynamicCodec: true, dynamicCopier: true);
        await AssertAccepted(reference, generic);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReferencedCopierOnlyDynamicAccessorsAreAccepted(bool referenceAssembly)
    {
        var reference = await CompileProducer("[Id(0)] public readonly T Value;",
            generic: true, frameworkVersion: "v8.0", hotReload: false, referenceAssembly,
            dynamicCodec: false, dynamicCopier: true, codecFrameworkVersion: "v10.0");
        await AssertAccepted(reference);
    }

    [Theory]
    [InlineData("v8.0", "v10.0", false, false, false)]
    [InlineData("v8.0", "v10.0", true, false, true)]
    [InlineData("v10.0", "v10.0", false, true, false)]
    [InlineData("v10.0", "v10.0", true, true, true)]
    [InlineData("v8.0", "v8.0", false, false, true)]
    [InlineData("v10.0", "v8.0", false, false, true)]
    public async Task ReferencedStaticImplementationsAreAccepted(
        string producerFramework, string consumerFramework, bool consumerHotReload, bool readOnly, bool referenceAssembly)
    {
        var member = readOnly ? "[Id(0)] public readonly T Value;" : "[Id(0)] public T Value;";
        var reference = await CompileProducer(member, generic: true, producerFramework, hotReload: false, referenceAssembly);
        var (compilation, result) = await GenerateConsumer(reference, generic: true, consumerFramework, consumerHotReload);

        Assert.Empty(result.Diagnostics);
        AssertNoCompilationErrors(compilation);
        var context = Assert.Single(result.Results.SelectMany(static result => result.GeneratedSources),
            static source => source.HintName.Contains(".context.", StringComparison.Ordinal)).SourceText.ToString();
        Assert.Contains("new global::OrleansCodeGen.Producer.Codec_Payload<int>", context, StringComparison.Ordinal);
        Assert.Contains("new global::OrleansCodeGen.Producer.Copier_Payload<int>", context, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Codec_Payload", false)]
    [InlineData("Codec_Payload", true)]
    [InlineData("Copier_Payload", false)]
    [InlineData("Copier_Payload", true)]
    public async Task ReferencedImplementationsWithoutAccessorMarkersAreAccepted(
        string implementation, bool referenceAssembly)
    {
        var reference = await CompileProducer("[Id(0)] public T Value;", generic: true,
            frameworkVersion: "v10.0", hotReload: false, referenceAssembly);
        var compilation = await AssertAccepted(reference);
        var symbol = compilation.GetTypeByMetadataName($"OrleansCodeGen.Producer.{implementation}`1");
        Assert.NotNull(symbol);
        Assert.DoesNotContain(symbol.GetAttributes(),
            static attribute => attribute.AttributeClass?.ToDisplayString() == "System.ComponentModel.DescriptionAttribute");
    }

    private static async Task<Compilation> AssertAccepted(MetadataReference[] reference, bool generic = true)
    {
        var (compilation, result) = await GenerateConsumer(reference, generic, frameworkVersion: "v10.0", hotReload: false);
        Assert.Empty(result.Diagnostics);
        AssertNoCompilationErrors(compilation);
        var context = Assert.Single(result.Results.SelectMany(static result => result.GeneratedSources),
            static source => source.HintName.Contains(".context.", StringComparison.Ordinal)).SourceText.ToString();
        var typeArguments = generic ? "<int>" : "";
        Assert.Contains($"new global::OrleansCodeGen.Producer.Codec_Payload{typeArguments}", context, StringComparison.Ordinal);
        Assert.Contains($"new global::OrleansCodeGen.Producer.Copier_Payload{typeArguments}", context, StringComparison.Ordinal);
        EmitReference(compilation);
        return compilation;
    }

    private static async Task<MetadataReference[]> CompileProducer(
        string member, bool generic, string frameworkVersion, bool hotReload, bool referenceAssembly,
        bool dynamicCodec = false, bool dynamicCopier = false,
        string? codecFrameworkVersion = null)
    {
        var sourceCompilation = await TestCompilationHelper.CreateCompilation($$"""
            using Orleans;
            namespace Producer;
            [GenerateSerializer]
            public sealed class Payload{{(generic ? "<T>" : "")}}
            {
                {{member}}
            }
            """, "ReferencedContextProducer");
        var (compilation, result) = Generate(sourceCompilation, frameworkVersion, hotReload);
        Assert.Empty(result.Diagnostics);
        AssertNoCompilationErrors(compilation);
        if (codecFrameworkVersion is not null)
        {
            // Exercise independently generated codec and copier accessor strategies.
            var (codecCompilation, codecResult) = Generate(sourceCompilation, codecFrameworkVersion, hotReload);
            Assert.Empty(codecResult.Diagnostics);
            AssertNoCompilationErrors(codecCompilation);
            var replacement = Assert.Single(codecCompilation.GetTypeByMetadataName(
                $"OrleansCodeGen.Producer.Codec_Payload{(generic ? "`1" : "")}")!.DeclaringSyntaxReferences)
                .GetSyntax(TestContext.Current.CancellationToken);
            var declaration = Assert.Single(compilation.GetTypeByMetadataName(
                $"OrleansCodeGen.Producer.Codec_Payload{(generic ? "`1" : "")}")!.DeclaringSyntaxReferences)
                .GetSyntax(TestContext.Current.CancellationToken);
            var tree = declaration.SyntaxTree;
            compilation = compilation.ReplaceSyntaxTree(tree, tree.WithRootAndOptions(
                tree.GetRoot(TestContext.Current.CancellationToken).ReplaceNode(declaration, replacement), tree.Options));
        }

        AssertProducerAccessorShape(compilation, "Codec_Payload", generic, dynamicCodec);
        AssertProducerAccessorShape(compilation, "Copier_Payload", generic, dynamicCopier);

        if (!referenceAssembly)
        {
            return [EmitReference(compilation)];
        }

        // Keep complete model metadata alongside reference-only generated implementations.
        var contracts = sourceCompilation.WithAssemblyName("ReferencedContextContracts");
        var contractsReference = EmitReference(contracts);
        var implementations = CSharpCompilation.Create("ReferencedContextProducer",
            compilation.SyntaxTrees.Skip(1), compilation.References.Append(contractsReference),
            (CSharpCompilationOptions)compilation.Options);
        return [contractsReference, EmitReference(implementations, metadataOnly: true)];
    }

    private static MetadataReference EmitReference(Compilation compilation, bool metadataOnly = false)
    {
        using var image = new MemoryStream();
        var emitted = compilation.Emit(image, options: new EmitOptions(
            metadataOnly: metadataOnly, includePrivateMembers: !metadataOnly),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return MetadataReference.CreateFromImage(image.ToArray());
    }

    private static void AssertProducerAccessorShape(
        Compilation compilation, string name, bool generic, bool dynamicAccessors)
    {
        var implementation = compilation.GetTypeByMetadataName($"OrleansCodeGen.Producer.{name}{(generic ? "`1" : "")}");
        Assert.NotNull(implementation);
        var declaration = Assert.IsType<ClassDeclarationSyntax>(Assert.Single(implementation.DeclaringSyntaxReferences)
            .GetSyntax(TestContext.Current.CancellationToken));
        Assert.Equal(dynamicAccessors, declaration.ToString().Contains("Utilities.FieldAccessor", StringComparison.Ordinal));
        Assert.Equal(dynamicAccessors, implementation.GetMembers().OfType<IFieldSymbol>()
            .Any(static field => field.IsStatic && field.Type.TypeKind == TypeKind.Delegate));
        Assert.DoesNotContain(implementation.GetAttributes(),
            static attribute => attribute.AttributeClass?.ToDisplayString() == "System.ComponentModel.DescriptionAttribute");
    }

    private static async Task<(Compilation Compilation, GeneratorDriverRunResult Result)> GenerateConsumer(
        MetadataReference[] reference, bool generic, string frameworkVersion, bool hotReload)
        => Generate(await TestCompilationHelper.CreateCompilation($$"""
            [Orleans.GenerateSerializerContext<Producer.Payload{{(generic ? "<int>" : "")}}>]
            public partial class DemoContext : Orleans.Serialization.SerializerContext { }
            """, "ReferencedContextConsumer", reference), frameworkVersion, hotReload);

    private static (Compilation Compilation, GeneratorDriverRunResult Result) Generate(
        CSharpCompilation compilation, string frameworkVersion, bool hotReload, string generateFieldIds = "None")
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        compilation = compilation.ReplaceSyntaxTree(compilation.SyntaxTrees.Single(),
            compilation.SyntaxTrees.Single().WithRootAndOptions(
                compilation.SyntaxTrees.Single().GetRoot(TestContext.Current.CancellationToken), parseOptions));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new OrleansSerializationSourceGenerator().AsSourceGenerator()], parseOptions: parseOptions,
            optionsProvider: TestCompilationHelper.CreateOptionsProvider(new Dictionary<string, string>
            {
                ["build_property.TargetFrameworkVersion"] = frameworkVersion,
                ["build_property.orleanshotreload"] = hotReload ? "true" : "false",
                ["build_property.orleans_generatefieldids"] = generateFieldIds,
            }));
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _, TestContext.Current.CancellationToken);
        return (output, driver.GetRunResult());
    }

    private static void AssertNoCompilationErrors(Compilation compilation)
        => Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
}
