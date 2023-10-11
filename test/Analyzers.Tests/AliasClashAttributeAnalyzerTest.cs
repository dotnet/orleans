using System.Linq;
using Microsoft.CodeAnalysis;
using Orleans.Analyzers;
using Xunit;

namespace Analyzers.Tests;

[TestCategory("BVT"), TestCategory("Analyzer")]
public class AliasClashAttributeAnalyzerTest : DiagnosticAnalyzerTestBase<AliasClashAttributeAnalyzer>
{
    private static readonly List<string> RuleIds = new();

    private async Task VerifyHasDiagnostic(string code, int diagnosticsCount)
    {
        var (diagnostics, _) = await GetDiagnosticsAsync(code, Array.Empty<string>());

        Assert.NotEmpty(diagnostics);
        Assert.Equal(diagnosticsCount, diagnostics.Length);

        Assert.All(diagnostics, diagnostic => RuleIds.Contains(diagnostic.Id));
        Assert.All(diagnostics, diagnostic => diagnostic.Severity.Equals(DiagnosticSeverity.Error));
    }

    [Fact]
    public Task A()
    {
        var code = $$"""
                    [Alias("A")]
                    public interface A : Orleans.IGrainWithStringKey
                    {

                    }

                    [Alias("A")]
                    public interface B : Orleans.IGrainWithStringKey
                    {
                        
                    }
                    """;

        return VerifyHasDiagnostic(code, 2);
    }

    [Fact]
    public Task B()
    {
        var code = $$"""
                    public interface A : Orleans.IGrainWithStringKey
                    {
                        [Alias("Void")] Task Void(string a);
                        [Alias("Void")] Task Void(int a);
                    }
                    """;

        return VerifyHasDiagnostic(code, 2);
    }
}
