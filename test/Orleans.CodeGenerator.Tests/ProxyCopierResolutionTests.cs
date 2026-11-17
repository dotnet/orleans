using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Orleans.CodeGenerator.Tests;

public sealed class ProxyCopierResolutionTests
{
    [Fact]
    public async Task ProxyCopierFieldsPreserveConcreteTypesAndCallSites()
    {
        const string source = """
            using Orleans;
            using System.Collections.Generic;
            using System.Threading.Tasks;
            namespace TestProject;
            [GenerateSerializer]
            public sealed class Payload
            {
                [Id(0)] public int Value { get; set; }
            }
            public interface IProxyGrain<T> : IGrainWithStringKey
            {
                ValueTask Send(Payload value, List<Payload> list, Payload[] array, T generic);
            }
            """;
        var (types, metadata) = await Generate(source);
        var proxy = types["Proxy_IProxyGrain"];
        var text = proxy.NormalizeWhitespace().ToFullString();
        Assert.Empty(proxy.Members.OfType<FieldDeclarationSyntax>());
        Assert.Contains("public Proxy_IProxyGrain(global::OrleansCodeGen.TestProject.ProxyShared_IProxyGrain<T> arg0, global::Orleans.Runtime.IdSpan arg1) : base(arg0, arg1)", text);
        Assert.Contains("private new global::OrleansCodeGen.TestProject.ProxyShared_IProxyGrain<T> Shared => (global::OrleansCodeGen.TestProject.ProxyShared_IProxyGrain<T>)base.Shared;", text);
        Assert.Contains("request.arg0 = this.Shared._copier", text);
        Assert.DoesNotContain("CodecProvider.GetDeepCopier<", text);

        var activator = types["ProxyActivator_IProxyGrain"].NormalizeWhitespace().ToFullString();
        Assert.Contains("internal sealed class ProxyActivator_IProxyGrain<T> : global::Orleans.GrainReferences.IGrainReferenceActivator", activator);
        Assert.Contains("private readonly global::OrleansCodeGen.TestProject.ProxyShared_IProxyGrain<T> _shared;", activator);
        Assert.Contains("_shared = new global::OrleansCodeGen.TestProject.ProxyShared_IProxyGrain<T>(shared);", activator);
        Assert.Contains("public global::Orleans.Runtime.GrainReference CreateReference(global::Orleans.Runtime.GrainId grainId) => new global::OrleansCodeGen.TestProject.Proxy_IProxyGrain<T>(_shared, grainId.Key);", activator);

        var shared = types["ProxyShared_IProxyGrain"];
        var sharedText = shared.NormalizeWhitespace().ToFullString();
        Assert.Contains("internal sealed class ProxyShared_IProxyGrain<T> : global::Orleans.Runtime.GrainReferenceShared", sharedText);
        Assert.Contains("public ProxyShared_IProxyGrain(global::Orleans.Runtime.GrainReferenceShared shared) : base(shared)", sharedText);
        Assert.Contains("public readonly OrleansCodeGen.TestProject.Copier_Payload", sharedText);
        Assert.Contains("public readonly global::Orleans.Serialization.Codecs.ListCopier<global::TestProject.Payload>", sharedText);
        Assert.Contains("public readonly global::Orleans.Serialization.Codecs.ArrayCopier<global::TestProject.Payload>", sharedText);
        Assert.Contains("public readonly global::Orleans.Serialization.Cloning.IDeepCopier<T>", sharedText);
        Assert.Contains("GetService<OrleansCodeGen.TestProject.Copier_Payload>(this, CodecProvider)", sharedText);
        Assert.Contains("GetService<global::Orleans.Serialization.Cloning.IDeepCopier<T>>(this, CodecProvider)", sharedText);
        Assert.Equal(4, shared.Members.OfType<FieldDeclarationSyntax>().Count());

        Assert.Contains("config.AddInterfaceProxyActivator(typeof(OrleansCodeGen.TestProject.Proxy_IProxyGrain<>), typeof(OrleansCodeGen.TestProject.ProxyActivator_IProxyGrain<>));", metadata);
    }

    [Fact]
    public async Task ProxyTypeParametersCannotShadowActivatorOrSharedTypes()
    {
        // Other reads a copier from Shared_ while a method type parameter with that name is in scope.
        const string source = """
            using Orleans;
            using System.Collections.Generic;
            using System.Threading.Tasks;
            namespace TestProject;
            [GenerateSerializer]
            public sealed class Payload
            {
                [Id(0)] public int Value { get; set; }
            }
            public interface IProxyGrain<ProxyShared, ProxyActivator, Shared> : IGrainWithStringKey
            {
                ValueTask Send(ProxyShared value, ProxyActivator other, List<Payload> list);
                ValueTask Other<Shared_>(Shared_ value, List<Payload> list);
            }
            """;
        var (types, _) = await Generate(source);
        Assert.Contains("ProxyActivator_IProxyGrain", types.Keys);
        Assert.Contains("ProxyShared_IProxyGrain", types.Keys);
        Assert.Contains("this.Shared_._copier", types["Proxy_IProxyGrain"].NormalizeWhitespace().ToFullString());
    }

    [Fact]
    public async Task ProxiesWithoutCopiersHoldTheRuntimeSharedState()
    {
        const string source = """
            using Orleans;
            using System.Threading.Tasks;
            namespace TestProject;
            public interface IProxyGrain : IGrainWithStringKey
            {
                ValueTask Send(int value);
            }
            """;
        var (types, metadata) = await Generate(source);
        Assert.DoesNotContain("ProxyShared_IProxyGrain", types.Keys);
        var activator = types["ProxyActivator_IProxyGrain"].NormalizeWhitespace().ToFullString();
        Assert.Contains("private readonly global::Orleans.Runtime.GrainReferenceShared _shared;", activator);
        Assert.Contains("_shared = shared;", activator);
        Assert.Contains("public Proxy_IProxyGrain(global::Orleans.Runtime.GrainReferenceShared arg0, global::Orleans.Runtime.IdSpan arg1) : base(arg0, arg1)", types["Proxy_IProxyGrain"].NormalizeWhitespace().ToFullString());
        Assert.Contains("config.AddInterfaceProxyActivator(typeof(OrleansCodeGen.TestProject.Proxy_IProxyGrain), typeof(OrleansCodeGen.TestProject.ProxyActivator_IProxyGrain));", metadata);
    }

    private static async Task<(Dictionary<string, ClassDeclarationSyntax> Types, string Metadata)> Generate(string source)
    {
        var compilation = await TestCompilationHelper.CreateCompilation(source);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new OrleansSerializationSourceGenerator().AsSourceGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out var diagnostics, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Empty(updated.GetDiagnostics(TestContext.Current.CancellationToken).Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var trees = driver.GetRunResult().GeneratedTrees;
        var types = trees.SelectMany(tree => tree.GetRoot(TestContext.Current.CancellationToken).DescendantNodes())
            .OfType<ClassDeclarationSyntax>()
            .Where(type => type.Identifier.ValueText.StartsWith("Proxy", StringComparison.Ordinal))
            .ToDictionary(type => type.Identifier.ValueText);
        var metadata = Assert.Single(trees, tree => tree.FilePath.EndsWith(".orleans.metadata.g.cs", StringComparison.Ordinal)).ToString();
        return (types, metadata);
    }
}
