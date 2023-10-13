using Microsoft.CodeAnalysis;
using Orleans.Analyzers;
using Xunit;

namespace Analyzers.Tests;

[TestCategory("BVT"), TestCategory("Analyzer")]
public class AliasClashAttributeAnalyzerTest : DiagnosticAnalyzerTestBase<AliasClashAttributeAnalyzer>
{
    private async Task VerifyHasDiagnostic(string code)
    {
        var (diagnostics, _) = await GetDiagnosticsAsync(code, Array.Empty<string>());

        Assert.NotEmpty(diagnostics);
        var diagnostic = diagnostics.First();

        Assert.Equal(AliasClashAttributeAnalyzer.RuleId, diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    private async Task VerifyHasNoDiagnostic(string code)
    {
        var (diagnostics, _) = await GetDiagnosticsAsync(code, Array.Empty<string>());
        Assert.Empty(diagnostics);
    }

    [Theory]
    [MemberData(nameof(GrainInterfaces))]
    public Task SameAliasWithinDeclaringType_ShouldTriggerDiagnostic(string grainInterface)
    {
        var code = $$"""
                    public interface I : {{grainInterface}}
                    {
                        [Alias("Void")] Task Void(int a);
                        [Alias("Void")] Task Void(long a);
                    }
                    """;

        return VerifyHasDiagnostic(code);
    }

    [Theory]
    [MemberData(nameof(GrainInterfaces))]
    public Task DifferentAliasWithinDeclaringType_ShouldNotTriggerDiagnostic(string grainInterface)
    {
        var code = $$"""
                    public interface I : {{grainInterface}}
                    {
                        [Alias("Void")] Task Void(int a);
                        [Alias("Void1")] Task Void(long a);
                    }
                    """;

        return VerifyHasNoDiagnostic(code);
    }

    [Theory]
    [MemberData(nameof(GrainInterfaces))]
    public Task SameAliasOutsideDeclaringType_ShouldNotTriggerDiagnostic(string grainInterface)
    {
        var code = $$"""
                    public interface I1 : {{grainInterface}}
                    {
                        [Alias("Void")] Task Void(string a);
                    }
                    
                    public interface I2
                    {
                        [Alias("Void")] Task Void(string a);
                    }
                    """;

        return VerifyHasNoDiagnostic(code);
    }
}