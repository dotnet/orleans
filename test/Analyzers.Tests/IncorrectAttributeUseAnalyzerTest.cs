using Microsoft.CodeAnalysis;
using Orleans.Analyzers;
using Xunit;

namespace Analyzers.Tests;

[TestCategory("BVT"), TestCategory("Analyzer")]
public class IncorrectAttributeUseAnalyzerTest : DiagnosticAnalyzerTestBase<IncorrectAttributeUseAnalyzer>
{
    private async Task VerifyHasDiagnostic(string code)
    {
        var (diagnostics, _) = await GetDiagnosticsAsync(code, Array.Empty<string>());

        Assert.NotEmpty(diagnostics);
        var diagnostic = diagnostics.First();

        Assert.Equal(IncorrectAttributeUseAnalyzer.RuleId, diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    private async Task VerifyHasNoDiagnostic(string code)
    {
        var (diagnostics, _) = await GetDiagnosticsAsync(code, Array.Empty<string>());
        Assert.Empty(diagnostics);
    }

    #region Grain

    [Theory]
    [MemberData(nameof(Attributes))]
    public Task ClassInheritingFromGrain_HavingAttributeApplied_ShouldTriggerDiagnostic(string attribute)
    {
        var code = $$"""
                    [{{attribute}}]
                    public class C : Grain
                    {

                    }
                    """;

        return VerifyHasDiagnostic(code);
    }

    [Theory]
    [MemberData(nameof(Attributes))]
    public Task ClassNotInheritingFromGrain_HavingAttributeApplied_ShouldNotTriggerDiagnostic(string attribute)
    {
        var code = $$"""
                    [{{attribute}}]
                    public class C
                    {

                    }
                    """;

        return VerifyHasNoDiagnostic(code);
    }

    [Fact]
    public Task ClassInheritingFromGrain_NotHavingAttributeApplied_ShouldNotTriggerDiagnostic()
    {
        var code = """
                    public class C : Grain
                    {

                    }
                    """;

        return VerifyHasNoDiagnostic(code);
    }

    #endregion

    #region Grain<TGrainState>

    [Theory]
    [MemberData(nameof(Attributes))]
    public Task ClassInheritingFromGenericGrain_HavingAttributeApplied_ShouldTriggerDiagnostic(string attribute)
    {
        var code = $$"""
                    [{{attribute}}]
                    public class C : Grain<S>
                    {

                    }

                    public class S
                    {
                    
                    }
                    """;

        return VerifyHasDiagnostic(code);
    }

    [Theory]
    [MemberData(nameof(Attributes))]
    public Task ClassNotInheritingFromGenericGrain_HavingAttributeApplied_ShouldNotTriggerDiagnostic(string attribute)
    {
        var code = $$"""
                    [{{attribute}}]
                    public class C
                    {

                    }
                    """;

        return VerifyHasNoDiagnostic(code);
    }

    [Fact]
    public Task ClassInheritingFromGenericGrain_NotHavingAttributeApplied_ShouldNotTriggerDiagnostic()
    {
        var code = """
                    public class C : Grain<S>
                    {

                    }

                    public class S
                    {

                    }
                    """;

        return VerifyHasNoDiagnostic(code);
    }

    #endregion

    public static IEnumerable<object[]> Attributes =>
        new List<object[]>
        {
            new object[] { "Alias(\"alias\")" },
            new object[] { "GenerateSerializer" }
        };
}
