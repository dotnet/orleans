using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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
            ".Add(typeof(global::TestProject.IDerived), typeof(OrleansCodeGen.TestProject.Proxy_IDerived), OrleansCodeGen.TestProject.Proxy_IDerived.Create)",
            generated, StringComparison.Ordinal);
        Assert.Contains(
            "public static global::Orleans.Runtime.GrainReference Create(global::Orleans.Runtime.GrainReferenceShared shared, global::Orleans.Runtime.IdSpan key) => new Proxy_IDerived(shared, key);",
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
                        OrleansCodeGen.TestProject.Proxy_IGeneric<int>.Create);
                    options.GetOrCreate<Factories>().Add(
                        typeof(Container<string>.INested<long>),
                        typeof(OrleansCodeGen.TestProject.Container.Proxy_INested<string, long>),
                        OrleansCodeGen.TestProject.Container.Proxy_INested<string, long>.Create);
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
                    OrleansCodeGen.Contracts.Proxy_IGeneric<int>.Create);
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
        Assert.DoesNotContain("GrainReference Create", generated, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task GrainReferenceProxyFactoryUsesProtectedConstructor(bool generic, bool baseFactory)
    {
        var compilation = await TestCompilationHelper.CreateCompilation(CreateCustomProxySource(
            "protected CustomReference(GrainReferenceShared shared, IdSpan key) : base(shared, key) { }", generic, baseFactory));

        var (result, output) = Generate(compilation);

        AssertCompiles(result, output);
        Assert.DoesNotContain(output.GetDiagnostics(TestContext.Current.CancellationToken), static diagnostic => diagnostic.Id == "CS0108");
        var generated = GetSource(result);
        Assert.Contains(
            generic ? "=> new Proxy_ICustom<T>(shared, key);" : "=> new Proxy_ICustom(shared, key);",
            generated, StringComparison.Ordinal);
        Assert.Contains(
            generic
                ? ".Add(typeof(global::TestProject.ICustom<>), typeof(OrleansCodeGen.TestProject.Proxy_ICustom<>))"
                : ".Add(typeof(global::TestProject.ICustom), typeof(OrleansCodeGen.TestProject.Proxy_ICustom), OrleansCodeGen.TestProject.Proxy_ICustom.Create)",
            generated, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task GrainReferenceProxyWithoutRequiredConstructorFailsCompilation(bool generic, bool privateConstructor)
    {
        var constructor = "protected CustomReference(GrainReferenceShared shared) : base(shared, default) { }";
        if (privateConstructor)
        {
            constructor += "\nprivate CustomReference(GrainReferenceShared shared, IdSpan key) : base(shared, key) { }";
        }

        var compilation = await TestCompilationHelper.CreateCompilation(CreateCustomProxySource(constructor, generic));
        Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken).Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        var (result, output) = Generate(compilation);

        Assert.Empty(result.Diagnostics);
        var diagnostic = Assert.Single(output.GetDiagnostics(TestContext.Current.CancellationToken),
            static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Equal("CS1729", diagnostic.Id);
        Assert.Contains("Proxy_ICustom", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("proxy", diagnostic.Location.SourceTree!.FilePath, StringComparison.Ordinal);
        var construction = diagnostic.Location.SourceTree.GetRoot(TestContext.Current.CancellationToken)
            .FindNode(diagnostic.Location.SourceSpan).FirstAncestorOrSelf<ObjectCreationExpressionSyntax>();
        var errorText = Assert.IsType<ObjectCreationExpressionSyntax>(construction).ToString();
        Assert.Contains("Proxy_ICustom", errorText, StringComparison.Ordinal);
        Assert.Contains("shared, key", errorText, StringComparison.Ordinal);
    }

    private static string CreateCustomProxySource(string constructor, bool generic, bool baseFactory = false) => $$"""
        using Orleans;
        using Orleans.Runtime;
        using Orleans.Serialization.Invocation;
        using System.Threading.Tasks;
        namespace TestProject;
        public class CustomReference : GrainReference
        {
            {{constructor}}
            public new ValueTask<T> InvokeAsync<T>(IRequest request) => base.InvokeAsync<T>(request);
            public new ValueTask InvokeAsync(IRequest request) => base.InvokeAsync(request);
            {{(baseFactory ? "public static GrainReference Create(GrainReferenceShared shared, IdSpan key) => throw new System.NotSupportedException(\"base-factory\");" : "")}}
        }
        [GenerateMethodSerializers(typeof(CustomReference))]
        public interface ICustom{{(generic ? "<T>" : "")}} : IGrainWithStringKey { }
        """;

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
