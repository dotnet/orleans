using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Orleans.CodeGenerator.Diagnostics;

namespace Orleans.CodeGenerator.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("CodeGen")]
public sealed class ImplementationSourceOutputTests
{
    private const string Source = """
        using Orleans;
        using System.Threading.Tasks;

        namespace TestProject;

        [GenerateSerializer]
        public sealed class Payload
        {
            [Id(0)]
            public int Value { get; set; }
        }

        public interface ITestGrain : IGrainWithIntegerKey
        {
            Task<Payload> GetPayload();
        }
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostsCanOmitAllImplementationsWhilePreservingContextDeclarations(bool includeContext)
    {
        var source = includeContext
            ? Source + """

                [GenerateSerializerContext<Payload>]
                public partial class TestContext : Orleans.Serialization.SerializerContext { }
                """
            : Source;
        var compilation = await TestCompilationHelper.CreateCompilation(source);
        var (complete, completeResult) = Generate(compilation, IncrementalGeneratorOutputKind.None);
        var (omitted, omittedResult) = Generate(compilation, IncrementalGeneratorOutputKind.Implementation);

        Assert.Empty(completeResult.Diagnostics);
        Assert.Empty(omittedResult.Diagnostics);
        Assert.Empty(complete.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Empty(omitted.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Contains(completeResult.GeneratedSources, static source => source.HintName.Contains(".ser.", StringComparison.Ordinal));
        Assert.Contains(completeResult.GeneratedSources, static source => source.HintName.Contains(".proxy.", StringComparison.Ordinal));
        Assert.Contains(completeResult.GeneratedSources, static source => source.HintName.EndsWith(".metadata.g.cs", StringComparison.Ordinal));
        Assert.Contains(completeResult.GeneratedSources, static source => source.HintName == "TestProject.orleans.g.cs");

        if (includeContext)
        {
            var declaration = Assert.Single(omittedResult.GeneratedSources);
            Assert.Contains(".context-declaration.", declaration.HintName, StringComparison.Ordinal);
            Assert.DoesNotContain("OrleansCodeGen.", declaration.SourceText.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("AddSerializer", declaration.SourceText.ToString(), StringComparison.Ordinal);
            var expected = completeResult.GeneratedSources.Where(static source =>
                source.HintName.Contains(".context-declaration.", StringComparison.Ordinal));
            Assert.Equal(GetSources(expected), GetSources(omittedResult.GeneratedSources));
            Assert.Contains(completeResult.GeneratedSources, static source => source.HintName.Contains(".context.", StringComparison.Ordinal));
        }
        else
        {
            Assert.Empty(omittedResult.GeneratedSources);
        }
    }

    [Fact]
    public async Task ReferencedSerializersUseImplementationOutput()
    {
        var library = await TestCompilationHelper.CreateCompilation(
            """
            using Orleans;

            namespace ReferenceProject;

            public sealed class Marker { }

            [GenerateSerializer]
            public sealed class ReferencedPayload
            {
                [Id(0)]
                public int Value { get; set; }
            }
            """,
            "ReferenceProject");
        var compilation = await TestCompilationHelper.CreateCompilation(
            """
            using Orleans;

            [assembly: GenerateCodeForDeclaringAssembly(typeof(ReferenceProject.Marker))]
            """,
            additionalReferences: library.ToMetadataReference());
        var (_, complete) = Generate(compilation, IncrementalGeneratorOutputKind.None);
        var (omittedCompilation, omitted) = Generate(compilation, IncrementalGeneratorOutputKind.Implementation);

        Assert.Empty(complete.Diagnostics);
        Assert.Empty(omitted.Diagnostics);
        Assert.Contains(complete.GeneratedSources, static source => source.HintName.Contains(".ser.", StringComparison.Ordinal));
        Assert.Empty(omitted.GeneratedSources);
        Assert.Empty(omittedCompilation.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public async Task ContractDiagnosticsRemainAvailableWithoutImplementationOutputs()
    {
        var compilation = await TestCompilationHelper.CreateCompilation(
            """
            using Orleans;

            [GenerateSerializer]
            public sealed class InvalidPayload
            {
                [Id(0)]
                public string Value => "computed";
            }

            public interface IInvalidGrain : IGrainWithIntegerKey
            {
                string GetValue();
            }

            [GenerateSerializerContext<System.IO.Stream>]
            public partial class UnsupportedContext : Orleans.Serialization.SerializerContext { }
            """);
        var (_, complete) = Generate(compilation, IncrementalGeneratorOutputKind.None);
        var (_, omitted) = Generate(compilation, IncrementalGeneratorOutputKind.Implementation);

        Assert.Contains(omitted.Diagnostics, static diagnostic => diagnostic.Id == DiagnosticRuleId.InaccessibleSetter);
        Assert.Contains(omitted.Diagnostics, static diagnostic => diagnostic.Id == DiagnosticRuleId.InvalidRpcMethodReturnType);
        Assert.Contains(omitted.Diagnostics, static diagnostic => diagnostic.Id == DiagnosticRuleId.UnsupportedSerializerContextType);
        Assert.Equal(
            complete.Diagnostics.Select(static diagnostic => diagnostic.ToString()).Order(StringComparer.Ordinal),
            omitted.Diagnostics.Select(static diagnostic => diagnostic.ToString()).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task EmissionDiagnosticsUseRegularOutput()
    {
        var compilation = await TestCompilationHelper.CreateCompilation("");
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new SerializerOutputGenerator().AsSourceGenerator()],
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.Implementation));
        driver = driver.RunGenerators(compilation, TestContext.Current.CancellationToken);
        var result = Assert.Single(driver.GetRunResult().Results);

        Assert.Equal("TEST001", Assert.Single(result.Diagnostics).Id);
        Assert.Empty(result.GeneratedSources);
    }

    private static IEnumerable<(string HintName, string Source)> GetSources(IEnumerable<GeneratedSourceResult> sources) =>
        sources.OrderBy(static source => source.HintName, StringComparer.Ordinal)
            .Select(static source => (source.HintName, source.SourceText.ToString()));

    private static (Compilation Compilation, GeneratorRunResult Result) Generate(
        CSharpCompilation compilation,
        IncrementalGeneratorOutputKind disabledOutputs)
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new OrleansSerializationSourceGenerator().AsSourceGenerator()],
            optionsProvider: TestCompilationHelper.CreateOptionsProvider(),
            driverOptions: new GeneratorDriverOptions(disabledOutputs));
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation, out var outputCompilation, out _, TestContext.Current.CancellationToken);
        return (outputCompilation, Assert.Single(driver.GetRunResult().Results));
    }

    private sealed class SerializerOutputGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var outputs = context.CompilationProvider.SelectMany(static (_, _) => ImmutableArray.Create(
                SourceOutputResult.FromDiagnostic(Diagnostic.Create(
                    new DiagnosticDescriptor("TEST001", "Emission failure", "Emission failure", "Test", DiagnosticSeverity.Error, isEnabledByDefault: true),
                    Location.None)),
                SourceOutputResult.FromSource(new GeneratedSourceEntry("serializer.g.cs", "internal sealed class GeneratedSerializer { }"))));
            GeneratedSourceOutput.RegisterImplementationSourceOutputs(context, outputs);
        }
    }
}
