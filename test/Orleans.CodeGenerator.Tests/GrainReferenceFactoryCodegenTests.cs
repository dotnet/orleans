using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

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
    public async Task ClosedGenericFactoriesUseOrdinaryTypedRegistration()
    {
        const string source = """
            using Orleans;
            using Orleans.Runtime;
            using Orleans.Serialization.Configuration;
            using Factories = Orleans.Serialization.Configuration.InterfaceProxyFactoryOptions<
                System.Func<Orleans.Runtime.GrainReferenceShared, Orleans.Runtime.IdSpan, Orleans.Runtime.GrainReference>>;
            namespace TestProject;
            public interface IGeneric<T> : IGrainWithStringKey where T : struct { }
            public class Container<T> where T : class
            {
                public interface INested<U> : IGrainWithStringKey where U : struct { }
            }
            public static class Registration
            {
                public static void Configure(TypeManifestOptions options)
                {
                    options.GetOrCreate<Factories>().Add(
                        typeof(IGeneric<int>),
                        typeof(OrleansCodeGen.TestProject.Proxy_IGeneric<int>),
                        static (shared, key) => new OrleansCodeGen.TestProject.Proxy_IGeneric<int>(shared, key));
                    options.GetOrCreate<Factories>().Add(
                        typeof(Container<string>.INested<long>),
                        typeof(OrleansCodeGen.TestProject.Container.Proxy_INested<string, long>),
                        static (shared, key) => new OrleansCodeGen.TestProject.Container.Proxy_INested<string, long>(shared, key));
                }
            }
            """;

        var (result, output) = Generate(await TestCompilationHelper.CreateCompilation(source));
        AssertCompiles(result, output);
        var generated = GetSource(result);
        Assert.Contains(".Add(typeof(global::TestProject.IGeneric<>), typeof(OrleansCodeGen.TestProject.Proxy_IGeneric<>))", generated, StringComparison.Ordinal);
        Assert.Contains("internal sealed class Proxy_INested<T, U>", generated, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NestedInterfacesCaptureContainingGenericParameters()
    {
        const string source = """
            using Orleans;
            namespace TestProject;
            public class Container<T> where T : class
            {
                public interface INested : IGrainWithStringKey
                {
                    System.Threading.Tasks.Task<T> Echo(T value);
                }
            }
            """;

        var (result, output) = Generate(await TestCompilationHelper.CreateCompilation(source));

        AssertCompiles(result, output);
        var generated = GetSource(result);
        Assert.Contains(
            ".Add(typeof(global::TestProject.Container<>.INested), typeof(OrleansCodeGen.TestProject.Container.Proxy_INested<>))",
            generated, StringComparison.Ordinal);
        Assert.DoesNotContain("new OrleansCodeGen.TestProject.Container.Proxy_INested<>", generated, StringComparison.Ordinal);
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
            using Orleans.Serialization.Configuration;
            using Factories = Orleans.Serialization.Configuration.InterfaceProxyFactoryOptions<
                System.Func<Orleans.Runtime.GrainReferenceShared, Orleans.Runtime.IdSpan, Orleans.Runtime.GrainReference>>;
            [assembly: GenerateCodeForDeclaringAssembly(typeof(Contracts.IGeneric<>))]
            public static class Registration
            {
                public static void Configure(TypeManifestOptions options) => options.GetOrCreate<Factories>().Add(
                    typeof(Contracts.IGeneric<int>),
                    typeof(OrleansCodeGen.Contracts.Proxy_IGeneric<int>),
                    static (shared, key) => new OrleansCodeGen.Contracts.Proxy_IGeneric<int>(shared, key));
            }
            """, "Consumer", MetadataReference.CreateFromImage(stream.ToArray()));

        var (result, output) = Generate(consumer);

        AssertCompiles(result, output);
        var generated = GetSource(result);
        Assert.Contains("internal sealed class Proxy_IGeneric<T>", generated, StringComparison.Ordinal);
        Assert.Contains(
            ".Add(typeof(global::Contracts.IGeneric<>), typeof(OrleansCodeGen.Contracts.Proxy_IGeneric<>))",
            generated, StringComparison.Ordinal);
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
        Assert.DoesNotContain("InterfaceProxyFactoryOptions", generated, StringComparison.Ordinal);
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
