using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.Diagnostics;

namespace Orleans.CodeGenerator.Tests;

public class ReferencedAssemblyDiagnosticParityTests
{
    [Fact]
    public async Task GenerateCodeForDeclaringAssembly_ReportsInaccessibleSerializableTypesFromReferencedAssembly()
    {
        const string libraryCode = """
            using Orleans;

            namespace LibraryProject;

            public sealed class Marker
            {
            }

            [GenerateSerializer]
            internal sealed class InternalDto
            {
                [Id(0)]
                public string Value { get; set; } = string.Empty;
            }
            """;

        const string consumerCode = """
            using Orleans;

            [assembly: GenerateCodeForDeclaringAssembly(typeof(LibraryProject.Marker))]
            """;

        var result = await RunSourceGeneratorForConsumer(libraryCode, consumerCode);
        var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Id == InaccessibleSerializableTypeDiagnostic.RuleId);

        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(Location.None, diagnostic.Location);
        Assert.Contains("InternalDto", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateCodeForDeclaringAssembly_ReportsImplicitFieldIdFailuresFromReferencedAssembly()
    {
        const string libraryCode = """
            using Orleans;

            namespace LibraryProject;

            public sealed class Marker
            {
            }

            [GenerateSerializer]
            public sealed class AutoDto
            {
                public string Value { get; set; } = string.Empty;
                public int Count { get; set; }
            }
            """;

        const string consumerCode = """
            using Orleans;

            [assembly: GenerateCodeForDeclaringAssembly(typeof(LibraryProject.Marker))]
            """;

        var result = await RunSourceGeneratorForConsumer(libraryCode, consumerCode);
        var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Id == CanNotGenerateImplicitFieldIdsDiagnostic.DiagnosticId);

        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(Location.None, diagnostic.Location);
        Assert.Contains("AutoDto", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateCodeForDeclaringAssembly_EmitsReferencedSerializersLocalProxiesAndMetadataOnce()
    {
        const string libraryCode = """
            using Orleans;

            namespace LibraryProject;

            public sealed class Marker
            {
            }

            [GenerateSerializer]
            public sealed class PublicReferencedDto
            {
                [Id(0)]
                public string Value { get; set; } = string.Empty;
            }
            """;

        const string consumerCode = """
            using Orleans;
            using System.Threading.Tasks;

            [assembly: GenerateCodeForDeclaringAssembly(typeof(LibraryProject.Marker))]

            namespace ConsumerProject;

            public interface IConsumerGrain : IGrainWithIntegerKey
            {
                Task<LibraryProject.PublicReferencedDto> Get();
            }
            """;

        var (result, consumerCompilation) = await RunSourceGeneratorForConsumerWithCompilation(libraryCode, consumerCode);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(1, CountGeneratedClassDeclarations(result, "Codec_PublicReferencedDto"));
        Assert.Equal(1, CountGeneratedClassDeclarations(result, "Copier_PublicReferencedDto"));
        Assert.Equal(1, CountGeneratedClassDeclarations(result, "Activator_PublicReferencedDto"));
        Assert.Equal(1, CountGeneratedClassDeclarations(result, "Proxy_IConsumerGrain"));
        Assert.Equal(1, CountMetadataTypeRegistrations(result, "Serializers", "Codec_PublicReferencedDto"));
        Assert.Equal(1, CountMetadataTypeRegistrations(result, "Copiers", "Copier_PublicReferencedDto"));
        Assert.Equal(1, CountMetadataTypeRegistrations(result, "Activators", "Activator_PublicReferencedDto"));
        Assert.Equal(1, CountMetadataTypeRegistrations(result, "InterfaceProxies", "Proxy_IConsumerGrain"));
        Assert.Equal(1, CountMetadataTypeRegistrations(result, "Interfaces", "IConsumerGrain"));

        var outputCompilation = consumerCompilation.AddSyntaxTrees(CreateGeneratedSyntaxTrees(result));
        AssertNoCompilationErrors(outputCompilation);
    }

    private static async Task<GeneratorRunResult> RunSourceGeneratorForConsumer(
        string libraryCode,
        string consumerCode)
        => (await RunSourceGeneratorForConsumerWithCompilation(libraryCode, consumerCode)).Result;

    private static async Task<(GeneratorRunResult Result, CSharpCompilation ConsumerCompilation)> RunSourceGeneratorForConsumerWithCompilation(
        string libraryCode,
        string consumerCode)
    {
        var libraryCompilation = await TestCompilationHelper.CreateCompilation(libraryCode, "LibraryProject");
        Assert.Empty(libraryCompilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        var consumerCompilation = await TestCompilationHelper.CreateCompilation(
            consumerCode,
            "ConsumerProject",
            libraryCompilation.ToMetadataReference());
        Assert.Empty(consumerCompilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        return (RunSourceGenerator(consumerCompilation), consumerCompilation);
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
        => GetGeneratedCompilationUnits(result)
            .SelectMany(static root => root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            .Count(declaration => string.Equals(declaration.Identifier.ValueText, className, StringComparison.Ordinal));

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
        return string.Equals(GetGeneratedClassIdentifier(typeName), registeredTypeName, StringComparison.Ordinal);
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

    private static IEnumerable<SyntaxTree> CreateGeneratedSyntaxTrees(GeneratorRunResult result)
    {
        foreach (var source in result.GeneratedSources)
        {
            yield return CSharpSyntaxTree.ParseText(source.SourceText, path: source.HintName);
        }
    }

    private static string GetGeneratedClassIdentifier(string typeName)
    {
        var genericMarkerIndex = typeName.IndexOf('<');
        return genericMarkerIndex >= 0 ? typeName[..genericMarkerIndex] : typeName;
    }

    private static void AssertNoCompilationErrors(Compilation compilation)
    {
        var errors = compilation.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(static diagnostic => diagnostic.ToString())
            .ToArray();

        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors));
    }
}
