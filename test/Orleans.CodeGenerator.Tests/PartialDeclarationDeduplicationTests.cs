using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.Diagnostics;

namespace Orleans.CodeGenerator.Tests;

public class PartialDeclarationDeduplicationTests
{
    [Fact]
    public async Task PartialGenerateSerializerAttributesProduceOneSerializerArtifactSetAndMetadataSet()
    {
        const string code = """
            using Orleans;

            namespace TestProject;

            [GenerateSerializer]
            public partial class PartialDto
            {
                [Id(0)]
                public string First { get; set; } = string.Empty;
            }

            [GenerateSerializer]
            public partial class PartialDto
            {
                [Id(1)]
                public int Second { get; set; }
            }
            """;

        var compilation = await CreateCompilation(code);
        var result = RunSourceGenerator(compilation);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(1, CountGeneratedClassDeclarations(result, "Codec_PartialDto"));
        Assert.Equal(1, CountGeneratedClassDeclarations(result, "Copier_PartialDto"));
        Assert.Equal(1, CountGeneratedClassDeclarations(result, "Activator_PartialDto"));
        Assert.Equal(1, CountMetadataTypeRegistrations(result, "Serializers", "Codec_PartialDto"));
        Assert.Equal(1, CountMetadataTypeRegistrations(result, "Copiers", "Copier_PartialDto"));
        Assert.Equal(1, CountMetadataTypeRegistrations(result, "Activators", "Activator_PartialDto"));
    }

    [Fact]
    public async Task PartialGenerateSerializerAttributesWithInvalidMemberProduceOneLogicalDiagnostic()
    {
        const string code = """
            using Orleans;

            namespace TestProject;

            [GenerateSerializer]
            public partial class InvalidPartialDto
            {
                [Id(0)]
                public string Name => "computed";
            }

            [GenerateSerializer]
            public partial class InvalidPartialDto
            {
                [Id(1)]
                public int Value { get; set; }
            }
            """;

        var compilation = await CreateCompilation(code);
        var result = RunSourceGenerator(compilation);

        var diagnostics = result.Diagnostics
            .Where(static diagnostic => diagnostic.Id == DiagnosticRuleId.InaccessibleSetter)
            .ToArray();

        Assert.Single(diagnostics);
    }

    [Fact]
    public async Task ReorderedPartialDeclarationsWithInvalidMember_ProducesOneStableDiagnostic()
    {
        const string invalidPart = """
            using Orleans;

            namespace TestProject;

            [GenerateSerializer]
            public partial class InvalidPartialDto
            {
                [Id(0)]
                public string Name => "computed";
            }
            """;

        const string validPart = """
            using Orleans;

            namespace TestProject;

            [GenerateSerializer]
            public partial class InvalidPartialDto
            {
                [Id(1)]
                public int Value { get; set; }
            }
            """;

        var compilation = await CreateCompilation(
            "PartialOrderingProject",
            ("InvalidPart.cs", invalidPart),
            ("ValidPart.cs", validPart));
        var reorderedCompilation = await CreateCompilation(
            "PartialOrderingProject",
            ("ValidPart.cs", validPart),
            ("InvalidPart.cs", invalidPart));

        var result = RunSourceGenerator(compilation);
        var reorderedResult = RunSourceGenerator(reorderedCompilation);

        var diagnostics = result.Diagnostics
            .Where(static diagnostic => diagnostic.Id == DiagnosticRuleId.InaccessibleSetter)
            .ToArray();
        var reorderedDiagnostics = reorderedResult.Diagnostics
            .Where(static diagnostic => diagnostic.Id == DiagnosticRuleId.InaccessibleSetter)
            .ToArray();

        Assert.Single(diagnostics);
        Assert.Single(reorderedDiagnostics);
        AssertDiagnosticsIdentical(diagnostics, reorderedDiagnostics);
        AssertGeneratedSourcesIdentical(result, reorderedResult);
    }

    [Fact]
    public async Task DirectAndInheritedGenerateMethodSerializersDiscoveryProducesOneProxyAndMetadataSet()
    {
        const string code = """
            using Orleans;
            using Orleans.Runtime;
            using System.Threading.Tasks;

            namespace TestProject;

            [GenerateMethodSerializers(typeof(GrainReference))]
            public interface IBaseGrain : IGrainWithIntegerKey
            {
                Task Ping();
            }

            [GenerateMethodSerializers(typeof(GrainReference))]
            public interface IDerivedGrain : IBaseGrain
            {
                Task Pong();
            }
            """;

        var compilation = await CreateCompilation(code);
        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        var result = RunSourceGenerator(compilation);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(1, CountGeneratedClassDeclarations(result, "Proxy_IDerivedGrain"));
        Assert.Equal(1, CountGeneratedClassDeclarationsWithPrefix(result, "Invokable_IDerivedGrain_GrainReference_"));
        Assert.Equal(1, CountMetadataTypeRegistrations(result, "InterfaceProxies", "Proxy_IDerivedGrain"));
        Assert.Equal(1, CountMetadataTypeRegistrations(result, "Interfaces", "IDerivedGrain"));
    }

    private static GeneratorRunResult RunSourceGenerator(CSharpCompilation compilation)
    {
        var generator = new OrleansSerializationSourceGenerator().AsSourceGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [generator],
            driverOptions: new GeneratorDriverOptions(default));

        driver = driver.RunGenerators(compilation);
        return driver.GetRunResult().Results.Single();
    }

    private static int CountGeneratedClassDeclarations(GeneratorRunResult result, string className)
        => GetGeneratedClassDeclarations(result)
            .Count(declaration => string.Equals(declaration.Identifier.ValueText, className, StringComparison.Ordinal));

    private static int CountGeneratedClassDeclarationsWithPrefix(GeneratorRunResult result, string classNamePrefix)
        => GetGeneratedClassDeclarations(result)
            .Count(declaration => declaration.Identifier.ValueText.StartsWith(classNamePrefix, StringComparison.Ordinal));

    private static IEnumerable<ClassDeclarationSyntax> GetGeneratedClassDeclarations(GeneratorRunResult result)
    {
        foreach (var root in GetGeneratedCompilationUnits(result))
        {
            foreach (var declaration in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                yield return declaration;
            }
        }
    }

    private static int CountMetadataTypeRegistrations(GeneratorRunResult result, string collectionName, string registeredTypeName)
        => GetGeneratedCompilationUnits(result)
            .Where(static root => root.SyntaxTree.FilePath.EndsWith(".orleans.metadata.g.cs", StringComparison.Ordinal))
            .SelectMany(static root => root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            .Count(invocation => IsMetadataRegistration(invocation, collectionName, registeredTypeName));

    private static bool IsMetadataRegistration(InvocationExpressionSyntax invocation, string collectionName, string registeredTypeName)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax addExpression
            || !string.Equals(addExpression.Name.Identifier.ValueText, "Add", StringComparison.Ordinal)
            || addExpression.Expression is not MemberAccessExpressionSyntax collectionExpression
            || !string.Equals(collectionExpression.Name.Identifier.ValueText, collectionName, StringComparison.Ordinal)
            || invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression is not TypeOfExpressionSyntax typeOfExpression)
        {
            return false;
        }

        var typeName = typeOfExpression.Type.ToString().Split('.').Last();
        return string.Equals(typeName, registeredTypeName, StringComparison.Ordinal);
    }

    private static IEnumerable<CompilationUnitSyntax> GetGeneratedCompilationUnits(GeneratorRunResult result)
    {
        foreach (var source in result.GeneratedSources)
        {
            var sourceText = source.SourceText.ToString().TrimStart('\uFEFF');
            if (string.IsNullOrWhiteSpace(sourceText))
            {
                continue;
            }

            var tree = CSharpSyntaxTree.ParseText(sourceText, path: source.HintName);
            yield return tree.GetCompilationUnitRoot();
        }
    }

    private static Task<CSharpCompilation> CreateCompilation(string sourceCode, string assemblyName = "TestProject")
        => TestCompilationHelper.CreateCompilation(sourceCode, assemblyName);

    private static async Task<CSharpCompilation> CreateCompilation(
        string assemblyName,
        params (string Path, string Source)[] sources)
    {
        Assert.NotEmpty(sources);

        var compilation = await TestCompilationHelper.CreateCompilation(sources[0].Source, assemblyName);
        var firstTree = CSharpSyntaxTree.ParseText(sources[0].Source, path: sources[0].Path);
        compilation = compilation.ReplaceSyntaxTree(compilation.SyntaxTrees.Single(), firstTree);

        if (sources.Length == 1)
        {
            return compilation;
        }

        return compilation.AddSyntaxTrees(sources.Skip(1).Select(static source => CSharpSyntaxTree.ParseText(source.Source, path: source.Path)));
    }

    private static void AssertDiagnosticsIdentical(IEnumerable<Diagnostic> diagnostics, IEnumerable<Diagnostic> otherDiagnostics)
        => Assert.Equal(
            diagnostics.Select(GetDiagnosticShape).OrderBy(static value => value, StringComparer.Ordinal),
            otherDiagnostics.Select(GetDiagnosticShape).OrderBy(static value => value, StringComparer.Ordinal));

    private static string GetDiagnosticShape(Diagnostic diagnostic)
    {
        var lineSpan = diagnostic.Location.GetLineSpan();
        return string.Join(
            "|",
            diagnostic.Id,
            diagnostic.Severity.ToString(),
            diagnostic.GetMessage(),
            lineSpan.Path ?? string.Empty,
            lineSpan.StartLinePosition.Line.ToString(),
            lineSpan.StartLinePosition.Character.ToString());
    }

    private static void AssertGeneratedSourcesIdentical(GeneratorRunResult result, GeneratorRunResult other)
    {
        var sources = GetGeneratedSourceMap(result);
        var otherSources = GetGeneratedSourceMap(other);

        Assert.Equal(sources.Count, otherSources.Count);

        foreach (var (hintName, sourceText) in sources)
        {
            Assert.True(otherSources.TryGetValue(hintName, out var otherSourceText), $"Missing generated source '{hintName}'.");
            Assert.Equal(sourceText, otherSourceText);
        }
    }

    private static SortedDictionary<string, string> GetGeneratedSourceMap(GeneratorRunResult result)
        => new(
            result.GeneratedSources.ToDictionary(source => source.HintName, source => source.SourceText.ToString(), StringComparer.Ordinal),
            StringComparer.Ordinal);
}
