using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Orleans.CodeGenerator.Tests;

/// <summary>
/// Characterization tests for generated source hint-name collisions.
/// </summary>
public class HintNameCollisionTests
{
    [Fact]
    public async Task GenerateSerializerTypesWithCollidingSanitizedHintNamesAreBothEmitted()
    {
        const string code = """
            using Orleans;

            namespace TestProject;

            [GenerateSerializer]
            public sealed class Colliding<T>
            {
                [Id(0)]
                public T Value { get; set; } = default!;
            }

            [GenerateSerializer]
            public sealed class Colliding_T
            {
                [Id(0)]
                public int Value { get; set; }
            }
            """;

        Assert.Equal(
            SanitizeHintComponent("global::TestProject.Colliding<T>"),
            SanitizeHintComponent("global::TestProject.Colliding_T"));

        var (result, outputCompilation) = await RunGenerator(code);

        Assert.Empty(result.Diagnostics);

        var serializerSources = GetGeneratedSources(result, ".orleans.ser.");
        Assert.Equal(2, serializerSources.Length);

        var serializerClassNames = GetGeneratedClassNames(serializerSources, "Codec_");
        Assert.Contains("Codec_Colliding", serializerClassNames);
        Assert.Contains("Codec_Colliding_T", serializerClassNames);

        AssertNoCompilationErrors(outputCompilation);
    }

    [Fact]
    public async Task GrainInterfacesWithCollidingSanitizedProxyHintNamesAreBothEmitted()
    {
        const string code = """
            using Orleans;
            using System.Threading.Tasks;

            namespace TestProject;

            public interface ICollidingGrain<T> : IGrainWithGuidKey
            {
                Task Ping();
            }

            public interface ICollidingGrain_T : IGrainWithGuidKey
            {
                Task Ping();
            }
            """;

        Assert.Equal(
            SanitizeHintComponent("global::TestProject.ICollidingGrain<T>"),
            SanitizeHintComponent("global::TestProject.ICollidingGrain_T"));

        var (result, outputCompilation) = await RunGenerator(code);

        Assert.Empty(result.Diagnostics);

        var proxySources = GetGeneratedSources(result, ".orleans.proxy.");
        Assert.Equal(2, proxySources.Length);

        var proxyClassNames = GetGeneratedClassNames(proxySources, "Proxy_");
        Assert.Contains("Proxy_ICollidingGrain", proxyClassNames);
        Assert.Contains("Proxy_ICollidingGrain_T", proxyClassNames);

        AssertNoCompilationErrors(outputCompilation);
    }

    private static async Task<(GeneratorRunResult Result, Compilation OutputCompilation)> RunGenerator(
        string code,
        string assemblyName = "TestProject")
    {
        var compilation = await TestCompilationHelper.CreateCompilation(code, assemblyName);
        AssertNoCompilationErrors(compilation);

        var generator = new OrleansSerializationSourceGenerator().AsSourceGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [generator],
            driverOptions: new GeneratorDriverOptions(default));

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var generatorDiagnostics);
        AssertNoErrors(generatorDiagnostics);

        return (driver.GetRunResult().Results.Single(), outputCompilation);
    }

    private static GeneratedSourceResult[] GetGeneratedSources(GeneratorRunResult result, string hintNameFragment)
        => result.GeneratedSources
            .Where(source => source.HintName.Contains(hintNameFragment, StringComparison.Ordinal))
            .OrderBy(source => source.HintName, StringComparer.Ordinal)
            .ToArray();

    private static string[] GetGeneratedClassNames(IEnumerable<GeneratedSourceResult> generatedSources, string classNamePrefix)
        => generatedSources
            .SelectMany(static source => CSharpSyntaxTree.ParseText(source.SourceText.ToString().TrimStart('\uFEFF'))
                .GetCompilationUnitRoot()
                .DescendantNodes()
                .OfType<ClassDeclarationSyntax>())
            .Select(static declaration => declaration.Identifier.ValueText)
            .Where(name => name.StartsWith(classNamePrefix, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

    private static void AssertNoCompilationErrors(Compilation compilation)
        => AssertNoErrors(compilation.GetDiagnostics());

    private static void AssertNoErrors(IEnumerable<Diagnostic> diagnostics)
    {
        var errors = diagnostics
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();

        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(static error => error.ToString())));
    }

    private static string SanitizeHintComponent(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousCharacterWasUnderscore = false;
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character) || character is '_' or '.')
            {
                builder.Append(character);
                previousCharacterWasUnderscore = false;
            }
            else if (!previousCharacterWasUnderscore)
            {
                builder.Append('_');
                previousCharacterWasUnderscore = true;
            }
        }

        var result = builder.ToString().Trim('_', '.');
        return result.Length > 0 ? result : "generated";
    }
}
