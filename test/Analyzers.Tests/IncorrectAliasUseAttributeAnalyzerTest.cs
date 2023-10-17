using Microsoft.CodeAnalysis;
using Orleans.Analyzers;
using Xunit;

namespace Analyzers.Tests;

[TestCategory("BVT"), TestCategory("Analyzer")]
public class IncorrectAliasUseAttributeAnalyzerTest : DiagnosticAnalyzerTestBase<IncorrectAliasUseAttributeAnalyzer>
{
    private async Task VerifyHasDiagnostic(string code)
    {
        var (diagnostics, _) = await GetDiagnosticsAsync(code, Array.Empty<string>());

        Assert.NotEmpty(diagnostics);
        var diagnostic = diagnostics.First();

        Assert.Equal(IncorrectAliasUseAttributeAnalyzer.RuleId, diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
    }

    private async Task VerifyHasNoDiagnostic(string code)
    {
        var (diagnostics, _) = await GetDiagnosticsAsync(code, Array.Empty<string>());
        Assert.Empty(diagnostics);
    }

    [Fact]
    public Task A()
    {
        var code = """
                    [Alias("MyGrain")]
                    public class C : Grain
                    {

                    }
                    """;

        return VerifyHasDiagnostic(code);
    }
}
