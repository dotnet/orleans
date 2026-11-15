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
        var compilation = await TestCompilationHelper.CreateCompilation(source);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new OrleansSerializationSourceGenerator().AsSourceGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out var diagnostics, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Empty(updated.GetDiagnostics(TestContext.Current.CancellationToken).Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var proxy = driver.GetRunResult().GeneratedTrees.SelectMany(tree => tree.GetRoot(TestContext.Current.CancellationToken).DescendantNodes())
            .OfType<ClassDeclarationSyntax>().Single(type => type.Identifier.ValueText == "Proxy_IProxyGrain");
        var text = proxy.NormalizeWhitespace().ToFullString();
        Assert.Empty(proxy.Members.OfType<FieldDeclarationSyntax>());
        Assert.Contains("[global::Orleans.GrainReferences.GrainReferenceActivatorAttribute(typeof(Proxy_IProxyGrain<>.ProxyActivator))]", text);
        Assert.Contains("internal sealed class ProxyActivator : global::Orleans.GrainReferences.IGrainReferenceActivator", text);
        Assert.Contains("private readonly ProxyShared _shared;", text);
        Assert.Contains("_shared = new ProxyShared(shared);", text);
        Assert.Contains("public global::Orleans.Runtime.GrainReference CreateReference(global::Orleans.Runtime.GrainId grainId) => new Proxy_IProxyGrain<T>(_shared, grainId.Key);", text);
        Assert.Contains("public Proxy_IProxyGrain(ProxyShared arg0, global::Orleans.Runtime.IdSpan arg1) : base(arg0, arg1)", text);
        Assert.Contains("internal sealed class ProxyShared : global::Orleans.Runtime.GrainReferenceShared", text);
        Assert.Contains("public ProxyShared(global::Orleans.Runtime.GrainReferenceShared shared) : base(shared)", text);
        Assert.Contains("public readonly OrleansCodeGen.TestProject.Copier_Payload", text);
        Assert.Contains("public readonly global::Orleans.Serialization.Codecs.ListCopier<global::TestProject.Payload>", text);
        Assert.Contains("public readonly global::Orleans.Serialization.Codecs.ArrayCopier<global::TestProject.Payload>", text);
        Assert.Contains("public readonly global::Orleans.Serialization.Cloning.IDeepCopier<T>", text);
        Assert.Contains("GetService<OrleansCodeGen.TestProject.Copier_Payload>(this, CodecProvider)", text);
        Assert.Contains("GetService<global::Orleans.Serialization.Cloning.IDeepCopier<T>>(this, CodecProvider)", text);
        Assert.Contains("request.arg0 = ((ProxyShared)base.Shared)._copier", text);
        Assert.DoesNotContain("CodecProvider.GetDeepCopier<", text);
        Assert.Equal(4, proxy.Members.OfType<ClassDeclarationSyntax>().Single(type => type.Identifier.ValueText == "ProxyShared").Members.OfType<FieldDeclarationSyntax>().Count());
    }

    [Fact]
    public async Task ProxyNestedTypeNamesAvoidTypeParameterNames()
    {
        // The generic method reads the copier which Send declares, so both type parameters are in scope at that access.
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
            public interface IProxyGrain<ProxyShared, ProxyActivator> : IGrainWithStringKey
            {
                ValueTask Send(ProxyShared value, ProxyActivator activator, List<Payload> list);
                ValueTask Other<ProxyShared_1>(ProxyShared_1 value, List<Payload> list);
            }
            """;
        var compilation = await TestCompilationHelper.CreateCompilation(source);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new OrleansSerializationSourceGenerator().AsSourceGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out var diagnostics, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Empty(updated.GetDiagnostics(TestContext.Current.CancellationToken).Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var proxy = driver.GetRunResult().GeneratedTrees.SelectMany(tree => tree.GetRoot(TestContext.Current.CancellationToken).DescendantNodes())
            .OfType<ClassDeclarationSyntax>().Single(type => type.Identifier.ValueText == "Proxy_IProxyGrain");
        Assert.Equal(["ProxyActivator_1", "ProxyShared_2"], proxy.Members.OfType<ClassDeclarationSyntax>().Select(type => type.Identifier.ValueText));
        var text = proxy.NormalizeWhitespace().ToFullString();
        Assert.Contains("typeof(Proxy_IProxyGrain<, >.ProxyActivator_1)", text);
        Assert.Contains("public Proxy_IProxyGrain(ProxyShared_2 arg0, global::Orleans.Runtime.IdSpan arg1) : base(arg0, arg1)", text);
        Assert.Contains("request.arg2 = ((ProxyShared_2)base.Shared)._copier", text);
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
        var compilation = await TestCompilationHelper.CreateCompilation(source);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new OrleansSerializationSourceGenerator().AsSourceGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out var diagnostics, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Empty(updated.GetDiagnostics(TestContext.Current.CancellationToken).Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var proxy = driver.GetRunResult().GeneratedTrees.SelectMany(tree => tree.GetRoot(TestContext.Current.CancellationToken).DescendantNodes())
            .OfType<ClassDeclarationSyntax>().Single(type => type.Identifier.ValueText == "Proxy_IProxyGrain");
        Assert.Equal("ProxyActivator", Assert.Single(proxy.Members.OfType<ClassDeclarationSyntax>()).Identifier.ValueText);
        var text = proxy.NormalizeWhitespace().ToFullString();
        Assert.Contains("private readonly global::Orleans.Runtime.GrainReferenceShared _shared;", text);
        Assert.Contains("_shared = shared;", text);
        Assert.Contains("public Proxy_IProxyGrain(global::Orleans.Runtime.GrainReferenceShared arg0, global::Orleans.Runtime.IdSpan arg1) : base(arg0, arg1)", text);
    }
}
