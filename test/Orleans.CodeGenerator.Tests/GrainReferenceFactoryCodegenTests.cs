using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Orleans.CodeGenerator.Diagnostics;

namespace Orleans.CodeGenerator.Tests;

public class GrainReferenceFactoryCodegenTests
{
    [Fact]
    public async Task GeneratedFactoriesUseDeclaredInterfaceAndDirectConstruction()
    {
        const string source = """
            using Orleans;
            namespace TestProject;
            public interface IBase : IGrainWithStringKey { }
            public interface IDerived : IBase { }
            public interface IGeneric<T> : IBase { }
            """;

        var (result, output) = Generate(await TestCompilationHelper.CreateCompilation(source));
        AssertCompiles(result, output);
        var generated = GetSource(result);
        Assert.Contains(
            ".Add(typeof(global::TestProject.IDerived), typeof(OrleansCodeGen.TestProject.Proxy_IDerived), static (shared, key) => new OrleansCodeGen.TestProject.Proxy_IDerived(shared, key))",
            generated, StringComparison.Ordinal);
        Assert.Contains(".Add(typeof(global::TestProject.IGeneric<>), typeof(OrleansCodeGen.TestProject.Proxy_IGeneric<>))", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("GetConstructor", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("GetInterfaces", generated, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClosedGenericFactoriesSupportConstraintsAndCoalesceDuplicates()
    {
        const string source = """
            using Orleans;
            [assembly: GenerateGrainReference(typeof(TestProject.IGeneric<int>))]
            [assembly: GenerateGrainReference(typeof(TestProject.IGeneric<int>))]
            [assembly: GenerateGrainReference(typeof(TestProject.Container<string>.INested<long>))]
            namespace TestProject;
            public interface IGeneric<T> : IGrainWithStringKey where T : struct { }
            public class Container<T> where T : class
            {
                public interface INested<U> : IGrainWithStringKey where U : struct { }
            }
            """;

        var (result, output) = Generate(await TestCompilationHelper.CreateCompilation(source));
        AssertCompiles(result, output);
        var generated = GetSource(result);
        Assert.Contains("static (shared, key) => new global::OrleansCodeGen.TestProject.Proxy_IGeneric<int>(shared, key)", generated, StringComparison.Ordinal);
        Assert.Contains("static (shared, key) => new global::OrleansCodeGen.TestProject.Container.Proxy_INested<string, long>(shared, key)", generated, StringComparison.Ordinal);
        Assert.Equal(1, generated.Split(
            ".Add(typeof(global::TestProject.IGeneric<int>)", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public async Task ClosedGenericFactoriesSupportReferencedInterfaces()
    {
        var contracts = await TestCompilationHelper.CreateCompilation("""
            using Orleans;
            namespace Contracts;
            public interface IGeneric<T> : IGrainWithStringKey
            {
                System.Threading.Tasks.Task<T> Echo(T value);
            }
            """, "Contracts");
        var (contractResult, contractOutput) = Generate(contracts);
        AssertCompiles(contractResult, contractOutput);
        using var stream = new MemoryStream();
        var emit = contractOutput.Emit(stream, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        var consumer = await TestCompilationHelper.CreateCompilation("""
            using Orleans;
            [assembly: GenerateGrainReference(typeof(Contracts.IGeneric<int>))]
            """, "Consumer", MetadataReference.CreateFromImage(stream.ToArray()));

        var (result, output) = Generate(consumer);

        AssertCompiles(result, output);
        var generated = GetSource(result);
        Assert.Contains("internal sealed class Proxy_IGeneric<T>", generated, StringComparison.Ordinal);
        Assert.Contains(
            ".Add(typeof(global::Contracts.IGeneric<int>), typeof(global::OrleansCodeGen.Contracts.Proxy_IGeneric<int>), static (shared, key) => new global::OrleansCodeGen.Contracts.Proxy_IGeneric<int>(shared, key))",
            generated, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("int")]
    [InlineData("TestProject.IGeneric<>")]
    [InlineData("TestProject.IPlain")]
    public async Task InvalidFactoryRegistrationProducesDiagnostic(string type)
    {
        var compilation = await TestCompilationHelper.CreateCompilation($$"""
            using Orleans;
            [assembly: GenerateGrainReference(typeof({{type}}))]
            namespace TestProject;
            public interface IGeneric<T> : IGrainWithStringKey { }
            public interface IPlain { }
            """);

        var (result, _) = Generate(compilation);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticRuleId.InvalidGrainReferenceFactory, diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.True(diagnostic.Location.IsInSource);
    }

    [Fact]
    public async Task NonGrainProxyBasesKeepTypeOnlyRegistration()
    {
        const string source = """
            using Orleans;
            using Orleans.Serialization.Invocation;
            using System.Threading.Tasks;
            namespace TestProject;
            public class PlainProxy
            {
                public PlainProxy() { }
                public ValueTask<T> InvokeAsync<T>(IInvokable request) => default;
                public ValueTask InvokeAsync(IInvokable request) => default;
            }
            [GenerateMethodSerializers(typeof(PlainProxy))]
            public interface IPlain { }
            """;

        var (result, output) = Generate(await TestCompilationHelper.CreateCompilation(source));

        AssertCompiles(result, output);
        var generated = GetSource(result);
        Assert.Contains("AddInterfaceProxy(typeof(OrleansCodeGen.TestProject.Proxy_IPlain))", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("GrainReferenceFactoryOptions", generated, StringComparison.Ordinal);
    }

    private static (GeneratorRunResult Result, Compilation Output) Generate(CSharpCompilation compilation)
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new OrleansSerializationSourceGenerator().AsSourceGenerator()],
            parseOptions: (CSharpParseOptions)compilation.SyntaxTrees.First().Options,
            optionsProvider: TestCompilationHelper.CreateOptionsProvider());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _, TestContext.Current.CancellationToken);
        return (Assert.Single(driver.GetRunResult().Results), output);
    }

    private static string GetSource(GeneratorRunResult result) =>
        string.Join(Environment.NewLine, result.GeneratedSources.Select(static source => source.SourceText.ToString()));

    private static void AssertCompiles(GeneratorRunResult result, Compilation output)
    {
        Assert.Empty(result.Diagnostics);
        Assert.Empty(output.GetDiagnostics(TestContext.Current.CancellationToken).Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        using var stream = new MemoryStream();
        var emit = output.Emit(stream, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
    }
}
