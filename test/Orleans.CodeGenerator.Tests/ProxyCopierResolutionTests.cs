using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Orleans.CodeGenerator.Tests;

public sealed class ProxyCopierResolutionTests
{
    [Fact]
    public async Task ProxyCopierFieldsResolveContractsThroughProviderCaches()
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
        Assert.Contains("IDeepCopier<global::TestProject.Payload>", text);
        Assert.Contains("IDeepCopier<global::System.Collections.Generic.List<global::TestProject.Payload>>", text);
        Assert.Contains("IDeepCopier<global::TestProject.Payload[]>", text);
        Assert.Contains("IDeepCopier<T>", text);
        Assert.Contains("CodecProvider.GetDeepCopier<global::TestProject.Payload>()", text);
        Assert.Contains("CodecProvider.GetDeepCopier<T>()", text);
        Assert.DoesNotContain("GetService<", text);
        Assert.DoesNotContain("Copier_Payload", text);
    }
}
