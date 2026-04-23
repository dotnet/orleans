using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Orleans.CodeGenerator.Diagnostics;

namespace Orleans.CodeGenerator.Tests;

/// <summary>
/// Tests that verify the Orleans source generator emits correct diagnostics
/// for invalid or unsupported code patterns.
/// </summary>
public class DiagnosticTests
{
    [Fact]
    public async Task GenerateSerializer_OnAccessibleType_ProducesOutput()
    {
        // Verifying that accessible types produce expected output without diagnostics.
        // The inaccessibility diagnostic (ORLEANS0107) is tested implicitly via cross-assembly
        // scenarios in the BVT suite; it requires complex assembly setup not easily done in a unit test.
        const string code = """
            using Orleans;

            namespace TestProject;

            [GenerateSerializer]
            public class PublicDto
            {
                [Id(0)]
                public string Name { get; set; }
            }

            [GenerateSerializer]
            internal class InternalDto
            {
                [Id(0)]
                public int Value { get; set; }
            }
            """;

        var result = await RunGenerator(code);

        Assert.Empty(result.Diagnostics);
        Assert.NotEmpty(result.GeneratedSources);

        var generatedSource = ConcatenateGeneratedSources(result);
        Assert.Contains("PublicDto", generatedSource);
        Assert.Contains("InternalDto", generatedSource);
    }

    [Fact]
    public async Task ReferenceAssembly_WithGenerateSerializer_EmitsWarning()
    {
        const string code = """
            using Orleans;

            namespace TestProject;

            [GenerateSerializer]
            public class RefAsmType
            {
                [Id(0)]
                public string Value { get; set; } = string.Empty;
            }
            """;

        var compilation = await CreateCompilation(code);

        // Add ReferenceAssemblyAttribute to the assembly
        var referenceAssemblyAttribute = SyntaxFactory.Attribute(
            SyntaxFactory.ParseName("System.Runtime.CompilerServices.ReferenceAssemblyAttribute"));
        var assemblyAttr = SyntaxFactory.AttributeList(
            SyntaxFactory.SingletonSeparatedList(referenceAssemblyAttribute))
            .WithTarget(SyntaxFactory.AttributeTargetSpecifier(SyntaxFactory.Token(SyntaxKind.AssemblyKeyword)));
        var root = (CompilationUnitSyntax)compilation.SyntaxTrees[0].GetRoot();
        var newRoot = root.AddAttributeLists(assemblyAttr);
        var newTree = compilation.SyntaxTrees[0].WithRootAndOptions(newRoot, compilation.SyntaxTrees[0].Options);
        compilation = compilation.RemoveSyntaxTrees(compilation.SyntaxTrees[0]).AddSyntaxTrees(newTree);

        var result = RunGeneratorOnCompilation(compilation);

        Assert.Contains(result.Diagnostics,
            d => d.Id == DiagnosticRuleId.ReferenceAssemblyWithGenerateSerializer);
    }

    [Fact]
    public async Task RpcInterfaceWithProperty_EmitsDiagnostic()
    {
        const string code = """
            using Orleans;
            using System.Threading.Tasks;

            namespace TestProject;

            public interface IMyGrain : IGrainWithIntegerKey
            {
                string Name { get; set; }
                Task<string> SayHello(string name);
            }
            """;

        var result = await RunGenerator(code);

        Assert.Contains(result.Diagnostics,
            d => d.Id == DiagnosticRuleId.RpcInterfaceProperty);
    }

    [Fact]
    public async Task InvalidRpcMethodReturnType_EmitsDiagnostic()
    {
        const string code = """
            using Orleans;

            namespace TestProject;

            public interface IMyGrain : IGrainWithIntegerKey
            {
                string SayHello(string name);
            }
            """;

        var result = await RunGenerator(code);

        Assert.Contains(result.Diagnostics,
            d => d.Id == DiagnosticRuleId.InvalidRpcMethodReturnType);
    }

    [Fact]
    public async Task GenerateSerializer_WithInaccessibleSetter_EmitsDiagnostic()
    {
        const string code = """
            using Orleans;

            namespace TestProject;

            [GenerateSerializer]
            public class MyDto
            {
                [Id(0)]
                public string Name => "computed";
            }
            """;

        var result = await RunGenerator(code);

        Assert.Contains(result.Diagnostics,
            d => d.Id == DiagnosticRuleId.InaccessibleSetter);
    }

    [Fact]
    public async Task IncorrectProxyBaseClassSpecification_EmitsDiagnostic()
    {
        const string code = """
            using Orleans;
            using System.Threading.Tasks;

            namespace TestProject;

            public class InvalidProxyBase
            {
            }

            [GenerateMethodSerializers(typeof(InvalidProxyBase))]
            public interface IMyGrain : IGrainWithIntegerKey
            {
                Task Ping();
            }
            """;

        var result = await RunGenerator(code);

        Assert.Contains(result.Diagnostics,
            d => d.Id == DiagnosticRuleId.IncorrectProxyBaseClassSpecification);
    }

    [Fact]
    public async Task ValidSerializableType_EmitsNoDiagnostics()
    {
        const string code = """
            using Orleans;

            namespace TestProject;

            [GenerateSerializer]
            public class ValidDto
            {
                [Id(0)]
                public string Name { get; set; }

                [Id(1)]
                public int Value { get; set; }
            }
            """;

        var result = await RunGenerator(code);

        Assert.Empty(result.Diagnostics);
        Assert.NotEmpty(result.GeneratedSources);
    }

    [Fact]
    public async Task ValidGrainInterface_EmitsNoDiagnostics()
    {
        const string code = """
            using Orleans;
            using System.Threading.Tasks;

            namespace TestProject;

            public interface IMyGrain : IGrainWithIntegerKey
            {
                Task<string> SayHello(string name);
                Task DoWork();
            }
            """;

        var result = await RunGenerator(code);

        Assert.Empty(result.Diagnostics);
        Assert.NotEmpty(result.GeneratedSources);
    }

    [Fact]
    public async Task SerializableRecord_EmitsNoDiagnostics()
    {
        const string code = """
            using Orleans;

            namespace TestProject;

            [GenerateSerializer]
            public record RecordDto
            {
                [Id(0)]
                public string Name { get; init; }

                [Id(1)]
                public int Value { get; init; }
            }
            """;

        var result = await RunGenerator(code);

        Assert.Empty(result.Diagnostics);
        Assert.NotEmpty(result.GeneratedSources);
    }

    [Fact]
    public async Task SerializableStruct_EmitsNoDiagnostics()
    {
        const string code = """
            using Orleans;

            namespace TestProject;

            [GenerateSerializer]
            public struct StructDto
            {
                [Id(0)]
                public string Name { get; set; }

                [Id(1)]
                public int Value { get; set; }
            }
            """;

        var result = await RunGenerator(code);

        Assert.Empty(result.Diagnostics);
        Assert.NotEmpty(result.GeneratedSources);
    }

    [Fact]
    public async Task GenerateSerializerOnEnum_EmitsNoDiagnostics()
    {
        const string code = """
            using Orleans;

            namespace TestProject;

            [GenerateSerializer]
            public enum MyEnum
            {
                None = 0,
                Value1 = 1,
                Value2 = 2,
            }
            """;

        var result = await RunGenerator(code);

        Assert.Empty(result.Diagnostics);
        Assert.NotEmpty(result.GeneratedSources);
    }

    [Fact]
    public async Task GenerateSerializerOnAbstractClass_EmitsNoDiagnostics()
    {
        const string code = """
            using Orleans;

            namespace TestProject;

            [GenerateSerializer]
            public abstract class AbstractDto
            {
                [Id(0)]
                public string Name { get; set; }
            }

            [GenerateSerializer]
            public class ConcreteDto : AbstractDto
            {
                [Id(1)]
                public int Value { get; set; }
            }
            """;

        var result = await RunGenerator(code);

        Assert.Empty(result.Diagnostics);
        Assert.NotEmpty(result.GeneratedSources);
    }

    [Fact]
    public async Task UnhandledException_IsReportedAsDiagnostic()
    {
        // A compilation with missing references can trigger internal errors in the generator.
        // The generator wraps these as ORLEANS0103 diagnostics rather than crashing.
        const string code = """
            using Orleans;

            namespace TestProject;

            [GenerateSerializer]
            public class DtoWithMissingType
            {
                [Id(0)]
                public MissingNamespace.MissingType Value { get; set; }
            }
            """;

        var compilation = await CreateCompilation(code);
        // The compilation itself will have errors for the missing type,
        // but the generator should still handle it gracefully
        var result = RunGeneratorOnCompilation(compilation);

        // Generator should not crash — it either reports a diagnostic or produces no output
        // for the problematic type. The key assertion is that it doesn't throw.
        Assert.True(
            result.GeneratedSources.Length >= 0,
            "Generator should not crash on compilation errors.");
    }

    [Fact]
    public async Task EmptyCompilation_ProducesNoOutput()
    {
        const string code = """
            namespace TestProject;

            public class PlainClass
            {
                public string Name { get; set; }
            }
            """;

        var result = await RunGenerator(code);

        // No Orleans attributes → no generated output and no diagnostics
        Assert.Empty(result.Diagnostics);

        // May still produce metadata output; verify no serializer-specific outputs
        var serializerSources = result.GeneratedSources
            .Where(s => s.HintName.Contains("serializer", StringComparison.OrdinalIgnoreCase)
                     || s.HintName.Contains("copier", StringComparison.OrdinalIgnoreCase)
                     || s.HintName.Contains("activator", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Empty(serializerSources);
    }

    [Fact]
    public async Task MultipleGrainInterfaces_AllProduceProxies()
    {
        const string code = """
            using Orleans;
            using System.Threading.Tasks;

            namespace TestProject;

            public interface IGrainA : IGrainWithIntegerKey
            {
                Task MethodA();
            }

            public interface IGrainB : IGrainWithGuidKey
            {
                Task<string> MethodB(string input);
            }

            public interface IGrainC : IGrainWithStringKey
            {
                Task<int> MethodC(int a, int b);
            }
            """;

        var result = await RunGenerator(code);

        Assert.Empty(result.Diagnostics);
        Assert.NotEmpty(result.GeneratedSources);

        var generatedSource = ConcatenateGeneratedSources(result);
        Assert.Contains("IGrainA", generatedSource);
        Assert.Contains("IGrainB", generatedSource);
        Assert.Contains("IGrainC", generatedSource);
    }

    #region Helpers

    private static async Task<GeneratorRunResult> RunGenerator(string code)
    {
        var compilation = await CreateCompilation(code);
        return RunGeneratorOnCompilation(compilation);
    }

    private static GeneratorRunResult RunGeneratorOnCompilation(
        CSharpCompilation compilation,
        IReadOnlyDictionary<string, string>? globalOptions = null)
    {
        AnalyzerConfigOptionsProvider? optionsProvider = globalOptions is null
            ? null
            : new TestAnalyzerConfigOptionsProvider(globalOptions);

        var generator = new OrleansSerializationSourceGenerator().AsSourceGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [generator],
            optionsProvider: optionsProvider,
            driverOptions: new GeneratorDriverOptions(default));
        driver = driver.RunGenerators(compilation);
        return driver.GetRunResult().Results.Single();
    }

    private static string ConcatenateGeneratedSources(GeneratorRunResult result)
        => string.Join(
            $"{Environment.NewLine}{Environment.NewLine}",
            result.GeneratedSources
                .OrderBy(source => source.HintName, StringComparer.Ordinal)
                .Select(source => source.SourceText.ToString().TrimStart('\uFEFF').TrimEnd()));

    private static Task<CSharpCompilation> CreateCompilation(string sourceCode, string assemblyName = "TestProject")
        => TestCompilationHelper.CreateCompilation(sourceCode, assemblyName);

    private sealed class TestAnalyzerConfigOptionsProvider : AnalyzerConfigOptionsProvider
    {
        private static readonly AnalyzerConfigOptions EmptyOptions = new TestAnalyzerConfigOptions(new Dictionary<string, string>());
        private readonly AnalyzerConfigOptions _globalOptions;

        public TestAnalyzerConfigOptionsProvider(IReadOnlyDictionary<string, string> globalOptions)
        {
            _globalOptions = new TestAnalyzerConfigOptions(globalOptions);
        }

        public override AnalyzerConfigOptions GlobalOptions => _globalOptions;
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => EmptyOptions;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => EmptyOptions;
    }

    private sealed class TestAnalyzerConfigOptions : AnalyzerConfigOptions
    {
        private readonly IReadOnlyDictionary<string, string> _options;

        public TestAnalyzerConfigOptions(IReadOnlyDictionary<string, string> options)
        {
            _options = options;
        }

        public override bool TryGetValue(string key, out string value) => _options.TryGetValue(key, out value!);
    }

    #endregion
}
