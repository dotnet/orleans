using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
            ".Add(typeof(global::TestProject.IDerived), typeof(OrleansCodeGen.TestProject.Proxy_IDerived), OrleansCodeGen.TestProject.Proxy_IDerived.Create)",
            generated, StringComparison.Ordinal);
        Assert.Contains(
            "public static global::Orleans.Runtime.GrainReference Create(global::Orleans.Runtime.GrainReferenceShared arg0, global::Orleans.Runtime.IdSpan arg1) => new Proxy_IDerived(arg0, arg1);",
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
            [GenerateMethodSerializers(typeof(PlainProxy))]
            public interface IPlain<T> { }
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
            generic ? "=> new Proxy_ICustom<T>(arg0, arg1);" : "=> new Proxy_ICustom(arg0, arg1);",
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
        Assert.Contains("arg0, arg1", errorText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonGrainProxyFactoryUsesBaseDeclaredDelegate()
    {
        const string source = """
            using Orleans;
            using Orleans.Serialization.Configuration;
            using Orleans.Serialization.Invocation;
            using System.Threading.Tasks;
            namespace TestProject;
            [GenerateProxyFactory(typeof(System.Func<string, int, bool, PlainProxy>))]
            public class PlainProxy
            {
                public PlainProxy(string label, int number, bool enabled)
                {
                    Label = label; Number = number; Enabled = enabled;
                }
                public string Label { get; }
                public int Number { get; }
                public bool Enabled { get; }
                public ValueTask<T> InvokeAsync<T>(IInvokable request) => default;
                public ValueTask InvokeAsync(IInvokable request) => default;
            }
            [GenerateMethodSerializers(typeof(PlainProxy))]
            public interface IPlain { }
            [GenerateMethodSerializers(typeof(PlainProxy))]
            public interface IPlain<T> { }
            public interface IGrainLike : IGrainWithStringKey { }
            public static class Verification
            {
                public static bool Verify()
                {
                    var options = new TypeManifestOptions();
                    ((ITypeManifestProvider)new OrleansCodeGen.TestProject.Metadata_TestProject()).Configure(options);
                    var factories = options.GetOrCreate<InterfaceProxyFactoryOptions<System.Func<string, int, bool, PlainProxy>>>();
                    factories.Add(typeof(IPlain<int>), typeof(OrleansCodeGen.TestProject.Proxy_IPlain<int>),
                        OrleansCodeGen.TestProject.Proxy_IPlain<int>.Create);
                    var registration = factories.Factories[typeof(IPlain)];
                    var proxy = registration.Factory("plain", 42, true);
                    var generic = factories.Factories[typeof(IPlain<int>)].Factory("generic", 17, false);
                    return proxy is IPlain && proxy.Label == "plain" && proxy.Number == 42 && proxy.Enabled
                        && registration.ProxyType == proxy.GetType()
                        && registration.Factory.Method.DeclaringType == proxy.GetType()
                        && generic is IPlain<int> && generic.Label == "generic" && generic.Number == 17 && !generic.Enabled
                        && factories.Factories[typeof(IPlain<>)].ProxyType == typeof(OrleansCodeGen.TestProject.Proxy_IPlain<>)
                        && factories.Factories[typeof(IPlain<>)].Factory is null
                        && options.GetOrCreate<InterfaceProxyFactoryOptions<System.Func<Orleans.Runtime.GrainReferenceShared, Orleans.Runtime.IdSpan, Orleans.Runtime.GrainReference>>>()
                            .Factories[typeof(IGrainLike)].Factory is not null;
                }
            }
            """;

        var (result, output) = Generate(await CreateConfiguredProxyCompilation(source));

        AssertCompiles(result, output);
        Assert.Contains("public static global::TestProject.PlainProxy Create(string arg0, int arg1, bool arg2) => new Proxy_IPlain(arg0, arg1, arg2);",
            GetSource(result), StringComparison.Ordinal);
        AssertVerificationSucceeds(output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProxyFactoryDeclarationIsInheritedAndNearestBaseCanOverrideIt(bool overrideFactory)
    {
        var declaration = overrideFactory ? "[GenerateProxyFactory(typeof(System.Func<string, ChildProxy>))]" : "";
        var constructor = overrideFactory ? "public ChildProxy(string label) { Label = label; }" : "public ChildProxy() { }";
        var invocation = overrideFactory
            ? "var proxy = options.GetOrCreate<InterfaceProxyFactoryOptions<System.Func<string, ChildProxy>>>().Factories[typeof(IChild)].Factory(\"child\");"
            : "var proxy = options.GetOrCreate<InterfaceProxyFactoryOptions<System.Func<ParentProxy>>>().Factories[typeof(IChild)].Factory();";
        var source = $$"""
            using Orleans;
            using Orleans.Serialization.Configuration;
            using Orleans.Serialization.Invocation;
            using System.Threading.Tasks;
            namespace TestProject;
            [GenerateProxyFactory(typeof(System.Func<ParentProxy>))]
            public class ParentProxy
            {
                public string Label { get; protected set; } = "parent";
                public ValueTask<T> InvokeAsync<T>(IInvokable request) => default;
                public ValueTask InvokeAsync(IInvokable request) => default;
            }
            {{declaration}}
            public class ChildProxy : ParentProxy
            {
                {{constructor}}
                public new ValueTask<T> InvokeAsync<T>(IInvokable request) => default;
                public new ValueTask InvokeAsync(IInvokable request) => default;
            }
            [GenerateMethodSerializers(typeof(ChildProxy))]
            public interface IChild { }
            public static class Verification
            {
                public static bool Verify()
                {
                    var options = new TypeManifestOptions();
                    ((ITypeManifestProvider)new OrleansCodeGen.TestProject.Metadata_TestProject()).Configure(options);
                    {{invocation}}
                    return proxy is IChild && proxy is ChildProxy && proxy.Label == "{{(overrideFactory ? "child" : "parent")}}";
                }
            }
            """;

        var (result, output) = Generate(await CreateConfiguredProxyCompilation(source));

        AssertCompiles(result, output);
        AssertVerificationSucceeds(output);
    }

    [Theory]
    [InlineData("ref")]
    [InlineData("in")]
    [InlineData("out")]
    public async Task ProxyFactoryCustomDelegatePreservesReferenceParameters(string mode)
    {
        var initialization = mode switch
        {
            "out" => "Number = value = 42;",
            "in" => "Number = value + 1;",
            _ => "Number = ++value;",
        };
        var source = $$"""
            using Orleans;
            using Orleans.Serialization.Configuration;
            using Orleans.Serialization.Invocation;
            using System.Threading.Tasks;
            namespace TestProject;
            public delegate PlainProxy ProxyFactory({{mode}} int value);
            [GenerateProxyFactory(typeof(ProxyFactory))]
            public class PlainProxy
            {
                public PlainProxy({{mode}} int value) { {{initialization}} }
                public int Number { get; }
                public ValueTask<T> InvokeAsync<T>(IInvokable request) => default;
                public ValueTask InvokeAsync(IInvokable request) => default;
            }
            [GenerateMethodSerializers(typeof(PlainProxy))]
            public interface IPlain { }
            public static class Verification
            {
                public static bool Verify()
                {
                    var options = new TypeManifestOptions();
                    ((ITypeManifestProvider)new OrleansCodeGen.TestProject.Metadata_TestProject()).Configure(options);
                    var factory = options.GetOrCreate<InterfaceProxyFactoryOptions<ProxyFactory>>().Factories[typeof(IPlain)].Factory;
                    var value = 41;
                    var proxy = factory({{mode}} value);
                    return proxy is IPlain && proxy.Number == 42 && value == {{(mode == "in" ? 41 : 42)}};
                }
            }
            """;

        var (result, output) = Generate(await CreateConfiguredProxyCompilation(source));

        AssertCompiles(result, output);
        Assert.Contains($"Create({mode} int arg0) => new Proxy_IPlain({mode} arg0);", GetSource(result), StringComparison.Ordinal);
        AssertVerificationSucceeds(output);
    }

    [Theory]
    [InlineData("typeof(int)")]
    [InlineData("typeof(System.Action)")]
    [InlineData("typeof(System.Func<,>)")]
    [InlineData("typeof(RefFactory)")]
    [InlineData("typeof(System.Func<string>)")]
    [InlineData("null")]
    public async Task InvalidProxyFactoryDeclarationReportsProxyBaseDiagnostic(string declaration)
    {
        var source = $$"""
            using Orleans;
            using Orleans.Serialization.Invocation;
            using System.Threading.Tasks;
            namespace TestProject;
            public delegate ref PlainProxy RefFactory();
            [GenerateProxyFactory({{declaration}})]
            public class PlainProxy
            {
                public PlainProxy() { }
                public ValueTask<T> InvokeAsync<T>(IInvokable request) => default;
                public ValueTask InvokeAsync(IInvokable request) => default;
            }
            [GenerateMethodSerializers(typeof(PlainProxy))]
            public interface IPlain { }
            """;

        var (result, _) = Generate(await TestCompilationHelper.CreateCompilation(source));

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticRuleId.IncorrectProxyBaseClassSpecification, diagnostic.Id);
        Assert.Contains("GenerateProxyFactory", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.True(diagnostic.Location.IsInSource);
    }

    private static void AssertVerificationSucceeds(Compilation output)
    {
        using var stream = new MemoryStream();
        var emit = output.Emit(stream, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        var assembly = System.Reflection.Assembly.Load(stream.ToArray());
        var method = assembly.GetType("TestProject.Verification")!.GetMethod("Verify")!;
        Assert.True(Assert.IsType<bool>(method.Invoke(null, null)));
    }

    private static Task<CSharpCompilation> CreateConfiguredProxyCompilation(string source) =>
        TestCompilationHelper.CreateCompilation(source, additionalReferences:
        [
            MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.Options.IConfigureOptions<>).Assembly.Location),
        ]);

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
