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
    public async Task GenerateCodeForDeclaringAssembly_EmitsReferencedSerializersProxiesAndMetadataOnce()
    {
        const string libraryCode = """
            using Orleans;
            using System.Threading.Tasks;

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

            public interface ILibraryGrain : IGrainWithIntegerKey
            {
                Task<PublicReferencedDto> Get();
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
        Assert.Equal(1, CountGeneratedClassDeclarations(result, "Proxy_ILibraryGrain"));
        Assert.Equal(1, CountGeneratedClassDeclarations(result, "Proxy_IConsumerGrain"));
        Assert.Equal(1, CountMetadataTypeRegistrations(result, "Serializers", "Codec_PublicReferencedDto"));
        Assert.Equal(1, CountMetadataTypeRegistrations(result, "Copiers", "Copier_PublicReferencedDto"));
        Assert.Equal(1, CountMetadataTypeRegistrations(result, "Activators", "Activator_PublicReferencedDto"));
        Assert.Equal(1, CountMetadataTypeRegistrations(result, "InterfaceProxies", "Proxy_ILibraryGrain"));
        Assert.Equal(1, CountMetadataTypeRegistrations(result, "InterfaceProxies", "Proxy_IConsumerGrain"));
        Assert.Equal(1, CountMetadataTypeRegistrations(result, "Interfaces", "ILibraryGrain"));
        Assert.Equal(1, CountMetadataTypeRegistrations(result, "Interfaces", "IConsumerGrain"));

        var outputCompilation = consumerCompilation.AddSyntaxTrees(CreateGeneratedSyntaxTrees(result));
        AssertNoCompilationErrors(outputCompilation);
    }

    [Fact]
    public async Task GenerateCodeForDeclaringAssembly_EmitsInheritedInvokablesFromReferencedInterfaces()
    {
        const string baseLibraryCode = """
            using Orleans;
            using System.Threading.Tasks;

            namespace BaseLibrary;

            public interface IBaseGrain : IGrainWithIntegerKey
            {
                Task Ping();
            }
            """;

        const string derivedLibraryCode = """
            namespace DerivedLibrary;

            public sealed class Marker
            {
            }

            public interface IDerivedGrain : BaseLibrary.IBaseGrain
            {
            }
            """;

        const string consumerCode = """
            using Orleans;

            [assembly: GenerateCodeForDeclaringAssembly(typeof(DerivedLibrary.Marker))]
            """;

        var baseLibraryCompilation = await TestCompilationHelper.CreateCompilation(baseLibraryCode, "BaseLibrary");
        var derivedLibraryCompilation = await TestCompilationHelper.CreateCompilation(
            derivedLibraryCode,
            "DerivedLibrary",
            baseLibraryCompilation.ToMetadataReference());
        var consumerCompilation = await TestCompilationHelper.CreateCompilation(
            consumerCode,
            "ConsumerProject",
            baseLibraryCompilation.ToMetadataReference(),
            derivedLibraryCompilation.ToMetadataReference());

        var result = RunSourceGenerator(consumerCompilation);

        Assert.Empty(result.Diagnostics);
        Assert.Contains(
            GetGeneratedCompilationUnits(result)
                .SelectMany(static root => root.DescendantNodes().OfType<ClassDeclarationSyntax>()),
            static declaration => declaration.Identifier.ValueText.StartsWith("Invokable_IBaseGrain_", StringComparison.Ordinal));
        AssertNoCompilationErrors(consumerCompilation.AddSyntaxTrees(CreateGeneratedSyntaxTrees(result)));
    }

    [Fact]
    public async Task GenerateCodeForDeclaringAssembly_DoesNotRegisterExistingGeneratedInvokables()
    {
        const string libraryCode = """
            using Orleans;
            using System.Threading.Tasks;

            namespace LibraryProject;

            public sealed class Marker
            {
            }

            public interface ILibraryGrain : IGrainWithIntegerKey
            {
                Task Ping();
            }
            """;

        const string consumerCode = """
            using Orleans;

            [assembly: GenerateCodeForDeclaringAssembly(typeof(LibraryProject.Marker))]
            """;

        var libraryCompilation = await TestCompilationHelper.CreateCompilation(libraryCode, "LibraryProject");
        GeneratorDriver libraryDriver = CSharpGeneratorDriver.Create(new OrleansSerializationSourceGenerator().AsSourceGenerator());
        libraryDriver = libraryDriver.RunGeneratorsAndUpdateCompilation(
            libraryCompilation,
            out var generatedLibraryCompilation,
            out var libraryGeneratorDiagnostics,
            TestContext.Current.CancellationToken);
        Assert.Empty(libraryGeneratorDiagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        AssertNoCompilationErrors(generatedLibraryCompilation);

        var consumerCompilation = await TestCompilationHelper.CreateCompilation(
            consumerCode,
            "ConsumerProject",
            generatedLibraryCompilation.ToMetadataReference());
        var result = RunSourceGenerator(consumerCompilation);

        Assert.Empty(result.Diagnostics);
        AssertNoCompilationErrors(consumerCompilation.AddSyntaxTrees(CreateGeneratedSyntaxTrees(result)));
    }

    [Fact]
    public async Task GenerateCodeForDeclaringAssembly_ReportsTypesWithoutDeclaringAssemblies()
    {
        const string consumerCode = """
            using Orleans;

            [assembly: GenerateCodeForDeclaringAssembly(typeof(int[]))]
            """;

        var consumerCompilation = await TestCompilationHelper.CreateCompilation(consumerCode, "ConsumerProject");
        var result = RunSourceGenerator(consumerCompilation);

        var diagnostic = Assert.Single(
            result.Diagnostics,
            static diagnostic => diagnostic.Id == GenerateCodeForDeclaringAssemblyAttribute_NoDeclaringAssembly_Diagnostic.DiagnosticId);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
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
            || !IsRegistrationMethod(addExpression, collectionName)
            || invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression is not TypeOfExpressionSyntax typeOfExpression)
        {
            return false;
        }

        var typeName = typeOfExpression.Type.ToString().Split('.').Last();
        return string.Equals(GetGeneratedClassIdentifier(typeName), registeredTypeName, StringComparison.Ordinal);
    }

    private static bool IsRegistrationMethod(MemberAccessExpressionSyntax expression, string collectionName)
        => GetRegistrationMethodName(collectionName) is { } methodName
            ? string.Equals(expression.Name.Identifier.ValueText, methodName, StringComparison.Ordinal)
            : expression is
                {
                    Name.Identifier.ValueText: "Add",
                    Expression: MemberAccessExpressionSyntax collectionExpression,
                }
                && string.Equals(collectionExpression.Name.Identifier.ValueText, collectionName, StringComparison.Ordinal);

    private static string? GetRegistrationMethodName(string collectionName) => collectionName switch
    {
        "Activators" => "AddActivator",
        "Converters" => "AddConverter",
        "Copiers" => "AddCopier",
        "FieldCodecs" => "AddFieldCodec",
        "InterfaceImplementations" => "AddInterfaceImplementation",
        "InterfaceProxies" => "AddInterfaceProxy",
        "Interfaces" => "AddInterface",
        "Serializers" => "AddSerializer",
        _ => null,
    };

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
