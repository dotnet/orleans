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

    [Theory]
    [InlineData("@event")]
    [InlineData("Outer.@class.Inner")]
    public async Task NamespaceWithReservedKeywordProducesValidGeneratedCode(string namespaceName)
    {
        var code = $$"""
            using System.Threading.Tasks;
            using Orleans;

            namespace {{namespaceName}};

            [GenerateSerializer]
            public enum Payload : int { Zero, One }

            [GenerateSerializer]
            public class Data
            {
                [Id(0)]
                public Payload Value { get; set; }
            }

            [GenerateSerializer]
            public sealed class Derived : Data
            {
                [Id(0)]
                public int Other { get; set; }
            }

            [GenerateSerializer]
            public sealed class Generic<T>
            {
                [Id(0)]
                public T Value { get; set; } = default!;
            }

            public interface IMyGrain : IGrainWithIntegerKey
            {
                Task<Data> Get(Payload payload);
            }
            """;

        var cancellationToken = TestContext.Current.CancellationToken;
        var compilation = await TestCompilationHelper.CreateCompilation(code);
        AssertNoErrors(compilation.GetDiagnostics(cancellationToken));

        var generator = new OrleansSerializationSourceGenerator().AsSourceGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [generator],
            driverOptions: new GeneratorDriverOptions(default));

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var generatorDiagnostics, cancellationToken);
        AssertNoErrors(generatorDiagnostics);
        AssertNoErrors(outputCompilation.GetDiagnostics(cancellationToken));

        // Invokables are matched by metadata name when the manifest is written, so a mismatch drops them without a compile error.
        var metadataSource = driver.GetRunResult().Results
            .Single()
            .GeneratedSources
            .Single(source => source.HintName.Contains(".orleans.metadata.g.cs", StringComparison.Ordinal));
        Assert.Contains("Invokable_IMyGrain", metadataSource.SourceText.ToString(), StringComparison.Ordinal);

        // Metadata names don't contain the escape character.
        var generatedNamespace = "OrleansCodeGen." + namespaceName.Replace("@", string.Empty, StringComparison.Ordinal);
        Assert.NotNull(outputCompilation.GetTypeByMetadataName($"{generatedNamespace}.Codec_Payload"));
        Assert.NotNull(outputCompilation.GetTypeByMetadataName($"{generatedNamespace}.Codec_Generic`1"));
        Assert.NotNull(outputCompilation.GetTypeByMetadataName($"{generatedNamespace}.Proxy_IMyGrain"));
    }

    [Fact]
    public async Task TypeNestedInReservedKeywordProducesValidGeneratedCode()
    {
        const string code = """
            using Orleans;

            namespace TestProject;

            public static class @default
            {
                [GenerateSerializer]
                public sealed class Nested
                {
                    [Id(0)]
                    public int Value { get; set; }
                }
            }
            """;

        var cancellationToken = TestContext.Current.CancellationToken;
        var compilation = await TestCompilationHelper.CreateCompilation(code);
        AssertNoErrors(compilation.GetDiagnostics(cancellationToken));

        var generator = new OrleansSerializationSourceGenerator().AsSourceGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [generator],
            driverOptions: new GeneratorDriverOptions(default));

        driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var generatorDiagnostics, cancellationToken);
        AssertNoErrors(generatorDiagnostics);
        AssertNoErrors(outputCompilation.GetDiagnostics(cancellationToken));
    }

    private static void AssertNoErrors(IEnumerable<Diagnostic> diagnostics)
    {
        var errors = diagnostics
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();

        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(static error => error.ToString())));
    }
}
