using System.ComponentModel;
using System.IO;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace Orleans.CodeGenerator.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("CodeGen")]
public sealed class GeneratedSourceVisibilityTests
{
    [Theory]
    [InlineData("")]
    [InlineData("namespace TestProject;")]
    public async Task OnlyGeneratedTypesAreMarkedEditorBrowsableNever(string namespaceDeclaration)
    {
        var (compilation, result) = await Generate(namespaceDeclaration);
        Assert.Empty(result.Diagnostics);
        Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        Assert.Contains(result.GeneratedSources, static source => source.HintName.Contains(".orleans.ser.", StringComparison.Ordinal));
        Assert.Contains(result.GeneratedSources, static source => source.HintName.Contains(".orleans.proxy.", StringComparison.Ordinal));
        Assert.Contains(result.GeneratedSources, static source => source.HintName.Contains(".orleans.context.", StringComparison.Ordinal));
        Assert.Contains(result.GeneratedSources, static source => source.HintName.EndsWith(".orleans.metadata.g.cs", StringComparison.Ordinal));

        foreach (var source in result.GeneratedSources)
        {
            var tree = source.SyntaxTree;
            var root = tree.GetRoot(TestContext.Current.CancellationToken);
            Assert.DoesNotContain("#line hidden", source.SourceText.ToString(), StringComparison.Ordinal);
            Assert.All(root.DescendantTokens(), token =>
                Assert.NotEqual(LineVisibility.Hidden, tree.GetLineVisibility(token.SpanStart, TestContext.Current.CancellationToken)));

            var semanticModel = compilation.GetSemanticModel(tree);
            foreach (var declaration in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var type = semanticModel.GetDeclaredSymbol(declaration, TestContext.Current.CancellationToken);
                Assert.NotNull(type);
                if (!declaration.Modifiers.Any(SyntaxKind.PartialKeyword))
                {
                    AssertEditorBrowsableNever(type);
                }

                foreach (var member in type.GetMembers())
                {
                    Assert.DoesNotContain(member.GetAttributes(), static attribute =>
                        attribute.AttributeClass?.ToDisplayString() == "System.ComponentModel.EditorBrowsableAttribute");
                }
            }
        }

        var prefix = namespaceDeclaration.Length == 0 ? "" : "TestProject.";
        var grainInterface = compilation.GetTypeByMetadataName($"{prefix}IVisibilityGrain");
        Assert.NotNull(grainInterface);
        Assert.DoesNotContain(grainInterface.GetAttributes(), static attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.ComponentModel.EditorBrowsableAttribute");
        var authoredMethod = Assert.Single(grainInterface.GetMembers("GetPayload"));
        Assert.DoesNotContain(authoredMethod.GetAttributes(), static attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.ComponentModel.EditorBrowsableAttribute");

        var context = compilation.GetTypeByMetadataName($"{prefix}VisibilityContext");
        Assert.NotNull(context);
        Assert.DoesNotContain(context.GetAttributes(), static attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.CodeDom.Compiler.GeneratedCodeAttribute");
        Assert.DoesNotContain(context.GetAttributes(), static attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.ComponentModel.EditorBrowsableAttribute");
        var configureMethod = Assert.Single(context.GetMembers("ConfigureInner"));
        Assert.Contains(configureMethod.GetAttributes(), static attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.CodeDom.Compiler.GeneratedCodeAttribute");
    }

    [Fact]
    public async Task GeneratedDiagnosticsRetainSourceLocations()
    {
        var (compilation, result) = await Generate("namespace TestProject;");
        var source = Assert.Single(result.GeneratedSources, static source =>
            source.HintName.Contains(".orleans.proxy.", StringComparison.Ordinal));
        var root = source.SyntaxTree.GetRoot(TestContext.Current.CancellationToken);
        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().First();
        var newRoot = root.ReplaceNode(
            method.ReturnType,
            SyntaxFactory.IdentifierName("MissingGeneratedType").WithTriviaFrom(method.ReturnType));
        var tree = source.SyntaxTree.WithRootAndOptions(newRoot, source.SyntaxTree.Options);
        compilation = compilation.ReplaceSyntaxTree(source.SyntaxTree, tree);

        var diagnostic = Assert.Single(compilation.GetDiagnostics(TestContext.Current.CancellationToken),
            diagnostic => diagnostic.Id == "CS0246" && diagnostic.Location.SourceTree == tree);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.NotEqual(LineVisibility.Hidden, tree.GetLineVisibility(diagnostic.Location.SourceSpan.Start, TestContext.Current.CancellationToken));
        Assert.Equal(tree.FilePath, tree.GetMappedLineSpan(diagnostic.Location.SourceSpan, TestContext.Current.CancellationToken).Path);
        Assert.Equal("MissingGeneratedType", tree.GetText(TestContext.Current.CancellationToken).ToString(diagnostic.Location.SourceSpan));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletionHidesGeneratedTypesFromMetadataWhileMembersRemainBrowsable(bool referenceAsMetadata)
    {
        var (compilation, _) = await Generate("namespace TestProject;");
        compilation = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(
            "namespace OrleansCodeGen.TestProject { public sealed class VisibleControl { } }",
            options: (CSharpParseOptions)compilation.SyntaxTrees.First().Options,
            path: "VisibleControl.cs",
            cancellationToken: TestContext.Current.CancellationToken));
        using var workspace = new AdhocWorkspace();
        var project = CreateEditorProject(workspace, compilation, referenceAsMetadata);
        var members = await GetCompletionNames(project, """
            class Consumer
            {
                void Use(OrleansCodeGen.TestProject.Codec_Payload codec)
                {
                    codec.$$
                }
            }
            """);
        Assert.Contains("ToString", members);
        foreach (var name in new[] { "Serialize", "Deserialize", "WriteField", "ReadValue" })
        {
            Assert.Contains(name, members);
        }

        var types = await GetCompletionNames(project, """
            class Consumer
            {
                void Use()
                {
                    OrleansCodeGen.TestProject.$$
                }
            }
            """);
        Assert.Contains("VisibleControl", types);
        foreach (var name in new[] { "Codec_Payload", "Copier_Payload" })
        {
            if (referenceAsMetadata)
            {
                Assert.DoesNotContain(name, types);
            }
            else
            {
                Assert.Contains(name, types);
            }
        }
    }

    [Fact]
    public async Task ImplementationSearchFindsGeneratedCodecMethodsDespiteBrowsingMetadata()
    {
        var (generated, _) = await Generate("namespace TestProject;");
        using var workspace = new AdhocWorkspace();
        var project = CreateEditorProject(workspace, generated, referenceAsMetadata: false);
        var compilation = await project.GetCompilationAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(compilation);
        var fieldCodec = compilation.GetTypeByMetadataName("Orleans.Serialization.Codecs.IFieldCodec`1");
        Assert.NotNull(fieldCodec);
        var writeField = Assert.Single(fieldCodec.GetMembers("WriteField"));

        var implementations = await SymbolFinder.FindImplementationsAsync(
            writeField, project.Solution, cancellationToken: TestContext.Current.CancellationToken);
        var implementation = Assert.Single(implementations, static candidate =>
            candidate.Name == "WriteField"
            && candidate.ContainingType?.ToDisplayString() == "OrleansCodeGen.TestProject.Codec_Payload");
        AssertEditorBrowsableNever(implementation.ContainingType);
        Assert.DoesNotContain(implementation.GetAttributes(), static attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.ComponentModel.EditorBrowsableAttribute");
    }

    [Theory]
    [InlineData("")]
    [InlineData("namespace TestProject;")]
    public async Task XmlDocumentationContainsOnlyAuthoredApiComments(string namespaceDeclaration)
    {
        var (compilation, _) = await Generate(namespaceDeclaration);
        using var assembly = new MemoryStream();
        using var documentation = new MemoryStream();
        var result = compilation.Emit(assembly, xmlDocumentationStream: documentation, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        documentation.Position = 0;
        var xml = XDocument.Load(documentation);
        var prefix = namespaceDeclaration.Length == 0 ? "" : "TestProject.";
        Assert.Equal(
            new[]
            {
                $"M:{prefix}IVisibilityGrain.GetPayload",
                $"M:{prefix}VisibilityGrain.GetPayload",
                $"P:{prefix}Payload.Value",
                $"T:{prefix}IVisibilityGrain",
                $"T:{prefix}Payload",
                $"T:{prefix}VisibilityContext",
                $"T:{prefix}VisibilityGrain",
            }.OrderBy(static name => name, StringComparer.Ordinal),
            xml.Descendants("member")
                .Select(static member => member.Attribute("name")!.Value)
                .OrderBy(static name => name, StringComparer.Ordinal));
    }

    private static void AssertEditorBrowsableNever(ISymbol symbol)
    {
        var attribute = Assert.Single(symbol.GetAttributes(), static attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.ComponentModel.EditorBrowsableAttribute");
        Assert.Equal((int)EditorBrowsableState.Never, attribute.ConstructorArguments[0].Value);
    }

    private static Project CreateEditorProject(AdhocWorkspace workspace, Compilation compilation, bool referenceAsMetadata)
    {
        var project = workspace.AddProject("EditorProject", LanguageNames.CSharp)
            .WithCompilationOptions(compilation.Options)
            .WithParseOptions(compilation.SyntaxTrees.First().Options)
            .WithMetadataReferences(compilation.References);
        if (referenceAsMetadata)
        {
            using var image = new MemoryStream();
            var emitted = compilation.Emit(image, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
            return project.AddMetadataReference(MetadataReference.CreateFromImage(image.ToArray()));
        }

        var index = 0;
        foreach (var tree in compilation.SyntaxTrees)
        {
            var path = string.IsNullOrEmpty(tree.FilePath) ? $"Input{index++}.cs" : tree.FilePath;
            project = project.AddDocument(Path.GetFileName(path), tree.GetText(TestContext.Current.CancellationToken), filePath: path).Project;
        }

        return project;
    }

    private static async Task<string[]> GetCompletionNames(Project project, string source)
    {
        const string marker = "$$";
        var position = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.NotEqual(-1, position);
        var document = project.AddDocument("Consumer.cs", SourceText.From(source.Remove(position, marker.Length)));
        var service = CompletionService.GetService(document);
        Assert.NotNull(service);
        var completions = await service.GetCompletionsAsync(
            document, position, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(completions);
        return completions.ItemsList.Select(static item => item.DisplayText).ToArray();
    }

    private static async Task<(Compilation Compilation, GeneratorRunResult Result)> Generate(string namespaceDeclaration)
    {
        var source = $$"""
            using Orleans;
            using System.Threading.Tasks;

            {{namespaceDeclaration}}

            /// <summary>A serializable payload.</summary>
            [GenerateSerializer]
            public sealed class Payload
            {
                /// <summary>Gets or sets the value.</summary>
                [Id(0)]
                public int Value { get; set; }
            }

            /// <summary>A grain contract.</summary>
            public interface IVisibilityGrain : IGrainWithIntegerKey
            {
                /// <summary>Gets the payload.</summary>
                Task<Payload> GetPayload();

                Task Ping(System.Threading.CancellationToken cancellationToken);
                Task<T> Echo<T>(T value);
            }

            /// <summary>An authored implementation.</summary>
            public sealed class VisibilityGrain : IVisibilityGrain
            {
                /// <summary>Gets the payload.</summary>
                public Task<Payload> GetPayload() => Task.FromResult(new Payload());

                public Task Ping(System.Threading.CancellationToken cancellationToken) => Task.CompletedTask;
                public Task<T> Echo<T>(T value) => Task.FromResult(value);
            }

            [GenerateSerializer]
            public enum Flavor { First, Second }

            [GenerateSerializer]
            public struct ValuePayload
            {
                [Id(0)]
                public int Value;
            }

            [GenerateSerializer]
            public class BasePayload
            {
                [Id(0)]
                public int Value { get; set; }
            }

            /// <summary>An authored serializer context.</summary>
            [OrleansSerializable<int>]
            public partial class VisibilityContext : Orleans.Serialization.SerializerContext { }
            """;
        var parseOptions = CSharpParseOptions.Default.WithDocumentationMode(DocumentationMode.Diagnose);
        var compilation = await TestCompilationHelper.CreateCompilation(source);
        compilation = compilation.ReplaceSyntaxTree(
            compilation.SyntaxTrees.Single(),
            CSharpSyntaxTree.ParseText(source, parseOptions));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new OrleansSerializationSourceGenerator().AsSourceGenerator()],
            parseOptions: parseOptions,
            optionsProvider: TestCompilationHelper.CreateOptionsProvider());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics, TestContext.Current.CancellationToken);
        Assert.Empty(diagnostics);
        return (outputCompilation, Assert.Single(driver.GetRunResult().Results));
    }
}
