using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Orleans.CodeGenerator.Tests;

public sealed class GeneratedWarningSuppressionTests
{
    private const string MissingXmlCommentDiagnosticId = "CS1591";
    private static readonly CSharpParseOptions DocumentationParseOptions =
        CSharpParseOptions.Default.WithDocumentationMode(DocumentationMode.Diagnose);

    [Fact]
    public async Task GeneratedSerializerProxyAndMetadataSourcesDoNotProduceMissingXmlCommentErrors()
    {
        const string source = """
            using Orleans;
            using System.Threading.Tasks;

            namespace TestProject;

            /// <summary>
            /// A DTO which forces serializer generation.
            /// </summary>
            [GenerateSerializer]
            public sealed class WarningDto
            {
                /// <summary>
                /// Gets or sets the name.
                /// </summary>
                [Id(0)]
                public string Name { get; set; } = string.Empty;
            }

            /// <summary>
            /// A grain interface which forces proxy and invokable generation.
            /// </summary>
            public interface IWarningGrain : IGrainWithIntegerKey
            {
                /// <summary>
                /// Gets the generated DTO.
                /// </summary>
                Task<WarningDto> GetDto();
            }
            """;

        var compilation = await CreateCompilationWithDocumentationDiagnostics(source);
        var result = RunGenerator(compilation);

        Assert.Empty(result.Diagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Contains(result.GeneratedSources, static source => source.HintName.Contains(".orleans.ser.", StringComparison.Ordinal));
        Assert.Contains(result.GeneratedSources, static source => source.HintName.Contains(".orleans.proxy.", StringComparison.Ordinal));
        Assert.Contains(result.GeneratedSources, static source => source.HintName.EndsWith(".orleans.metadata.g.cs", StringComparison.Ordinal));

        var generatedCompilation = compilation.AddSyntaxTrees(CreateGeneratedSyntaxTrees(result));
        var generatedTreePaths = result.GeneratedSources
            .Select(static source => source.HintName)
            .ToHashSet(StringComparer.Ordinal);
        var missingXmlCommentErrors = generatedCompilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Id == MissingXmlCommentDiagnosticId
                && diagnostic.Severity == DiagnosticSeverity.Error
                && diagnostic.Location.SourceTree is { } tree
                && generatedTreePaths.Contains(tree.FilePath))
            .Select(static diagnostic => $"{diagnostic.Location.GetLineSpan().Path}: {diagnostic.Id} {diagnostic.Severity}: {diagnostic.GetMessage()}")
            .ToArray();

        Assert.True(
            missingXmlCommentErrors.Length == 0,
            string.Join(Environment.NewLine, missingXmlCommentErrors));
    }

    private static async Task<CSharpCompilation> CreateCompilationWithDocumentationDiagnostics(string source)
    {
        var compilation = await TestCompilationHelper.CreateCompilation(source);
        var syntaxTree = CSharpSyntaxTree.ParseText(
            source,
            DocumentationParseOptions,
            path: "WarningSuppressionInput.cs",
            encoding: Encoding.UTF8);
        var options = compilation.Options.WithSpecificDiagnosticOptions(
            compilation.Options.SpecificDiagnosticOptions.SetItem(MissingXmlCommentDiagnosticId, ReportDiagnostic.Error));

        return compilation
            .ReplaceSyntaxTree(compilation.SyntaxTrees.Single(), syntaxTree)
            .WithOptions(options);
    }

    private static GeneratorRunResult RunGenerator(CSharpCompilation compilation)
    {
        var generator = new OrleansSerializationSourceGenerator().AsSourceGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGenerators(compilation);
        return driver.GetRunResult().Results.Single();
    }

    private static IEnumerable<SyntaxTree> CreateGeneratedSyntaxTrees(GeneratorRunResult result)
    {
        foreach (var source in result.GeneratedSources)
        {
            yield return CSharpSyntaxTree.ParseText(
                source.SourceText,
                DocumentationParseOptions,
                path: source.HintName);
        }
    }
}
