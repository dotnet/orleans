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
}
