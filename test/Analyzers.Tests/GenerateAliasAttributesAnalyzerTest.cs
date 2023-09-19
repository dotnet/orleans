using Microsoft.CodeAnalysis;
using Orleans.Analyzers;
using Xunit;

namespace Analyzers.Tests;

[TestCategory("BVT"), TestCategory("Analyzer")]
public class GenerateAliasAttributesAnalyzerTest : DiagnosticAnalyzerTestBase<GenerateAliasAttributesAnalyzer>
{
    private async Task VerifyHasDiagnostic(string code, int diagnosticsCount)
    {
        var (diagnostics, _) = await GetDiagnosticsAsync(code, Array.Empty<string>());

        Assert.NotEmpty(diagnostics);
        Assert.Equal(diagnosticsCount, diagnostics.Length);

        var diagnostic = diagnostics.First();

        Assert.Equal(GenerateAliasAttributesAnalyzer.RuleId, diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
    }

    private async Task VerifyHasNoDiagnostic(string code)
    {
        var (diagnostics, _) = await GetDiagnosticsAsync(code, new string[0]);
        Assert.Empty(diagnostics);
    }

    [Theory]
    [MemberData(nameof(GrainInterfaces))]
    public Task GrainInterfaceWithoutAliasAttribute_ShouldTriggerDiagnostic(string grainInterface)
    {
        var code = $$"""
                    public interface I : {{grainInterface}}
                    {
                        Task<int> M1();
                        Task<int> M2();

                        static Task<int> M3() => Task.FromResult(0);
                    }
                    """;

        return VerifyHasDiagnostic(code, 3);  // 3 diagnostics, because 1 for interface, and 2 for the non-static methods
    }

    [Theory]
    [MemberData(nameof(GrainInterfaces))]
    public Task GrainInterfaceWithAliasAttribute_ShouldNotTriggerDiagnostic(string grainInterface)
    {
        var code = $$"""
                    [Alias("I")]
                    public interface I : {{grainInterface}}
                    {
                        [Alias("M1")] Task<int> M1();
                        [Alias("M2")] Task<int> M2();

                        static Task<int> M3() => Task.FromResult(0);
                    }
                    """;

        return VerifyHasNoDiagnostic(code);
    }

    [Fact]
    public Task NonGrainInterfaceWithoutAliasAttribute_ShouldNotTriggerDiagnostic()
    {
        var code = """
                    public interface I
                    {
                        Task<int> M1();
                        Task<int> M2();

                        static Task<int> M3() => Task.FromResult(0);
                    }
                    """;

        return VerifyHasNoDiagnostic(code);
    }

    [Fact]
    public Task Test()
    {
        var code = """
                    public interface I : IGrain
                    {
                        Task<int> M1();
                    }
                    """;

        return VerifyHasDiagnostic(code, 1);
    }

    public static IEnumerable<object[]> GrainInterfaces =>
        new List<object[]>
        {
            new object[] { "Orleans.IGrain" },
            new object[] { "Orleans.IGrainWithStringKey" },
            new object[] { "Orleans.IGrainWithGuidKey" },
            new object[] { "Orleans.IGrainWithGuidCompoundKey" },
            new object[] { "Orleans.IGrainWithIntegerKey" },
            new object[] { "Orleans.IGrainWithIntegerCompoundKey" }
        };
}
