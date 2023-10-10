using Microsoft.CodeAnalysis;
using Orleans.Analyzers;
using Xunit;

namespace Analyzers.Tests;

[TestCategory("BVT"), TestCategory("Analyzer")]
public class AliasClashAttributeAnalyzerTest : DiagnosticAnalyzerTestBase<AliasClashAttributeAnalyzer>
{
    private async Task VerifyHasDiagnostic(string code, int diagnosticsCount)
    {
        var (diagnostics, _) = await GetDiagnosticsAsync(code, Array.Empty<string>());

        Assert.NotEmpty(diagnostics);
        Assert.Equal(diagnosticsCount, diagnostics.Length);

        var diagnostic = diagnostics.First();

        Assert.Equal(AliasClashAttributeAnalyzer.RuleId, diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public Task A()
    {
        var code = $$"""
                    [Alias("A")]
                    public interface A : Orleans.IGrainWithStringKey
                    {
                        [Alias("B")]
                        Task Void();
                    }

                    [Alias("A")]
                    public interface B : Orleans.IGrainWithStringKey
                    {
                        
                    }
                    """;

        return VerifyHasDiagnostic(code, 2);
    }
}
