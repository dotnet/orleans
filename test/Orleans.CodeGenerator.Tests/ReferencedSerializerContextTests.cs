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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReferencedGenericReadonlyFieldRejectsNet8Producer(bool referenceAssembly)
    {
        var reference = await CompileProducer("[Id(0)] public readonly T Value;", generic: true,
            frameworkVersion: "v8.0", hotReload: false, referenceAssembly,
            dynamicCodec: true, dynamicCopier: true);
        await AssertRejected(reference, "Codec_Payload");
    }

    [Theory]
    [InlineData("v8.0", false, true, false)]
    [InlineData("v8.0", false, true, true)]
    [InlineData("v10.0", true, true, false)]
    [InlineData("v10.0", true, true, true)]
    [InlineData("v10.0", true, false, false)]
    [InlineData("v10.0", true, false, true)]
    public async Task ReferencedPrivateFieldRejectsDynamicProducer(
        string frameworkVersion, bool hotReload, bool generic, bool referenceAssembly)
    {
        var memberType = generic ? "T" : "int";
        var reference = await CompileProducer(
            $"[Id(0)] private {memberType} _value; public {memberType} Value => _value;",
            generic, frameworkVersion, hotReload, referenceAssembly, dynamicCodec: true, dynamicCopier: true);
        await AssertRejected(reference, "Codec_Payload", generic);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task ReferencedHotReloadReadonlyFieldRejectsDynamicProducer(bool generic, bool referenceAssembly)
    {
        var reference = await CompileProducer($"[Id(0)] public readonly {(generic ? "T" : "int")} Value;",
            generic, frameworkVersion: "v10.0", hotReload: true, referenceAssembly,
            dynamicCodec: true, dynamicCopier: true);
        await AssertRejected(reference, "Codec_Payload", generic);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReferencedCopierOnlyDynamicAccessorsAreRejected(bool referenceAssembly)
    {
        var reference = await CompileProducer("[Id(0)] public readonly T Value;",
            generic: true, frameworkVersion: "v8.0", hotReload: false, referenceAssembly,
            dynamicCodec: false, dynamicCopier: true, codecFrameworkVersion: "v10.0");
        await AssertRejected(reference, "Copier_Payload");
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
    public async Task ReferencedImplementationsWithoutAccessorContractAreRejected(
        string implementation, bool referenceAssembly)
    {
        var reference = await CompileProducer("[Id(0)] public T Value;", generic: true,
            frameworkVersion: "v10.0", hotReload: false, referenceAssembly, removeContractFrom: implementation);
        await AssertRejected(reference, implementation, reason: "rebuild the referenced assembly");
    }

    private static async Task AssertRejected(
        MetadataReference[] reference, string implementation, bool generic = true,
        string reason = "statically generated field accessor")
    {
        var (compilation, result) = await GenerateConsumer(reference, generic, frameworkVersion: "v10.0", hotReload: false);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("ORLEANS0115", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        var message = diagnostic.GetMessage();
        Assert.True(message.Contains(implementation, StringComparison.Ordinal), message);
        Assert.True(message.Contains(reason, StringComparison.Ordinal), message);
        Assert.DoesNotContain(result.Results.SelectMany(static result => result.GeneratedSources),
            static source => source.HintName.Contains(".context.", StringComparison.Ordinal));
        Assert.All(compilation.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), static diagnostic =>
            {
                Assert.Equal("CS0534", diagnostic.Id);
                Assert.Contains("DemoContext", diagnostic.GetMessage(), StringComparison.Ordinal);
                Assert.Contains("ConfigureInner", diagnostic.GetMessage(), StringComparison.Ordinal);
            });
    }

    private static async Task<MetadataReference[]> CompileProducer(
        string member, bool generic, string frameworkVersion, bool hotReload, bool referenceAssembly,
        string? removeContractFrom = null, bool dynamicCodec = false, bool dynamicCopier = false,
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
            // Compile independently generated implementations so copier validation is not masked by codec rejection.
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

        AssertProducerContract(compilation, "Codec_Payload", generic, dynamicCodec);
        AssertProducerContract(compilation, "Copier_Payload", generic, dynamicCopier);

        if (removeContractFrom is not null)
        {
            foreach (var tree in compilation.SyntaxTrees)
            {
                var root = tree.GetRoot(TestContext.Current.CancellationToken);
                var attributes = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                    .Where(type => type.Identifier.ValueText == removeContractFrom)
                    .SelectMany(static type => type.AttributeLists)
                    .Where(static list => list.ToString().Contains("OrleansCodeGen.FieldAccessors.v1:", StringComparison.Ordinal))
                    .ToArray();
                if (attributes.Length > 0)
                {
                    var replacement = tree.WithRootAndOptions(root.RemoveNodes(attributes, SyntaxRemoveOptions.KeepNoTrivia)!, tree.Options);
                    compilation = compilation.ReplaceSyntaxTree(tree, replacement);
                }
            }
        }

        if (!referenceAssembly)
        {
            return [EmitReference(compilation)];
        }

        // Keep model metadata complete: model reconstruction deliberately rejects reference-only models.
        // Only the generated implementations need reference-only images to exercise accessor contract retention.
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

    private static void AssertProducerContract(
        Compilation compilation, string name, bool generic, bool dynamicAccessors)
    {
        var implementation = compilation.GetTypeByMetadataName($"OrleansCodeGen.Producer.{name}{(generic ? "`1" : "")}");
        Assert.NotNull(implementation);
        var declaration = Assert.IsType<ClassDeclarationSyntax>(Assert.Single(implementation.DeclaringSyntaxReferences)
            .GetSyntax(TestContext.Current.CancellationToken));
        Assert.Equal(dynamicAccessors, declaration.ToString().Contains("Utilities.FieldAccessor", StringComparison.Ordinal));
        Assert.Equal(dynamicAccessors, implementation.GetMembers().OfType<IFieldSymbol>()
            .Any(static field => field.IsStatic && field.Type.TypeKind == TypeKind.Delegate));
        var contract = Assert.Single(implementation.GetAttributes(),
            static attribute => attribute.AttributeClass?.ToDisplayString() == "System.ComponentModel.DescriptionAttribute");
        Assert.Equal($"OrleansCodeGen.FieldAccessors.v1:{(dynamicAccessors ? "Dynamic" : "Static")}",
            Assert.Single(contract.ConstructorArguments).Value);
    }

    private static async Task<(Compilation Compilation, GeneratorDriverRunResult Result)> GenerateConsumer(
        MetadataReference[] reference, bool generic, string frameworkVersion, bool hotReload)
        => Generate(await TestCompilationHelper.CreateCompilation($$"""
            [Orleans.GenerateSerializerContext(typeof(Producer.Payload{{(generic ? "<int>" : "")}}))]
            public partial class DemoContext : Orleans.Serialization.SerializerContext { }
            """, "ReferencedContextConsumer", reference), frameworkVersion, hotReload);

    private static (Compilation Compilation, GeneratorDriverRunResult Result) Generate(
        CSharpCompilation compilation, string frameworkVersion, bool hotReload)
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
            }));
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _, TestContext.Current.CancellationToken);
        return (output, driver.GetRunResult());
    }

    private static void AssertNoCompilationErrors(Compilation compilation)
        => Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
}
