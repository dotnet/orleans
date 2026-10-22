using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Orleans.CodeGenerator.Tests;

/// <summary>
/// Regression tests for https://github.com/dotnet/orleans/issues/9290: the code generator must escape
/// assembly names which are C# reserved identifiers when using them to construct the generated metadata namespace.
/// </summary>
public class ReservedIdentifierNamespaceTests
{
    [Theory]
    [InlineData("default")]
    [InlineData("class")]
    [InlineData("namespace")]
    public async Task AssemblyNamedAfterReservedKeywordProducesValidGeneratedNamespace(string assemblyName)
    {
        const string code = """
            using Orleans;

            namespace TestProject;

            [GenerateSerializer]
            public sealed class Data
            {
                [Id(0)]
                public int Value { get; set; }
            }
            """;

        var cancellationToken = TestContext.Current.CancellationToken;
        var compilation = await TestCompilationHelper.CreateCompilation(code, assemblyName);
        AssertNoErrors(compilation.GetDiagnostics(cancellationToken));

        var generator = new OrleansSerializationSourceGenerator().AsSourceGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [generator],
            driverOptions: new GeneratorDriverOptions(default));

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var generatorDiagnostics, cancellationToken);
        AssertNoErrors(generatorDiagnostics);

        // The generated metadata source must compile without syntax errors, i.e. the namespace segment
        // derived from the reserved-keyword assembly name must be escaped (e.g. "OrleansCodeGen.@default").
        AssertNoErrors(outputCompilation.GetDiagnostics(cancellationToken));

        var metadataSource = driver.GetRunResult().Results
            .Single()
            .GeneratedSources
            .Single(source => source.HintName.Contains(".orleans.metadata.g.cs", StringComparison.Ordinal));

        var namespaceDeclaration = CSharpSyntaxTree.ParseText(metadataSource.SourceText.ToString().TrimStart('﻿'), cancellationToken: cancellationToken)
            .GetCompilationUnitRoot(cancellationToken)
            .DescendantNodes()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .Single();

        Assert.Equal($"OrleansCodeGen.@{assemblyName}", namespaceDeclaration.Name.ToString());
    }

    private static void AssertNoErrors(IEnumerable<Diagnostic> diagnostics)
    {
        var errors = diagnostics
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();

        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(static error => error.ToString())));
    }
}
