using System.ComponentModel;
using System.IO;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Orleans.CodeGenerator.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("CodeGen")]
public sealed class GeneratedSourceVisibilityTests
{
    [Theory]
    [InlineData("")]
    [InlineData("namespace TestProject;")]
    public async Task GeneratedTypesAndBrowsableMembersAreMarkedEditorBrowsableNever(string namespaceDeclaration)
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
            Assert.DoesNotContain("MethodImplAttribute", source.SourceText.ToString(), StringComparison.Ordinal);
            Assert.All(root.DescendantTokens(), token =>
                Assert.NotEqual(LineVisibility.Hidden, tree.GetLineVisibility(token.SpanStart)));

            var semanticModel = compilation.GetSemanticModel(tree);
            foreach (var declaration in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var type = semanticModel.GetDeclaredSymbol(declaration, TestContext.Current.CancellationToken);
                Assert.NotNull(type);
                if (!declaration.Modifiers.Any(SyntaxKind.PartialKeyword))
                {
                    AssertEditorBrowsableNever(type);
                }

                foreach (var member in type.GetMembers().Where(static member =>
                    !member.IsImplicitlyDeclared
                    && (member.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal
                        or Accessibility.Protected or Accessibility.ProtectedOrInternal or Accessibility.ProtectedAndInternal
                        || member is IMethodSymbol { ExplicitInterfaceImplementations.Length: > 0 })))
                {
                    if (member is IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet })
                    {
                        continue;
                    }

                    AssertEditorBrowsableNever(member);
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
        Assert.NotEqual(LineVisibility.Hidden, tree.GetLineVisibility(diagnostic.Location.SourceSpan.Start));
        Assert.Equal(tree.FilePath, diagnostic.Location.GetMappedLineSpan().Path);
        Assert.Equal("MissingGeneratedType", tree.GetText(TestContext.Current.CancellationToken).ToString(diagnostic.Location.SourceSpan));
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
            [GenerateSerializerContext<int>]
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
