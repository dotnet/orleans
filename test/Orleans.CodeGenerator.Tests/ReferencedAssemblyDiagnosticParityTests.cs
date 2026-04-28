using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
        Assert.Contains("AutoDto", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    private static async Task<GeneratorRunResult> RunSourceGeneratorForConsumer(
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

        return RunSourceGenerator(consumerCompilation);
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
}
