using Microsoft.CodeAnalysis;
using Orleans.Analyzers;
using Xunit;

namespace Analyzers.Tests;

[TestCategory("BVT"), TestCategory("Analyzer")]
public class IdClashAttributeAnalyzerTest : DiagnosticAnalyzerTestBase<IdClashAttributeAnalyzer>
{
    private async Task VerifyHasDiagnostic(string code, int diagnosticsCount)
    {
        var (diagnostics, _) = await GetDiagnosticsAsync(code, Array.Empty<string>());

        Assert.NotEmpty(diagnostics);
        Assert.Equal(diagnosticsCount, diagnostics.Length);

        var diagnostic = diagnostics.First();

        Assert.Equal(IdClashAttributeAnalyzer.RuleId, diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    private async Task VerifyHasNoDiagnostic(string code)
    {
        var (diagnostics, _) = await GetDiagnosticsAsync(code, Array.Empty<string>());
        Assert.Empty(diagnostics);
    }

    [Fact]
    public Task TypesWithGenerateSerializerAndDuplicatedIds_ShouldTriggerDiagnostic()
    {
        var code = """
                    [GenerateSerializer]
                    public class C
                    {
                        [Id(0)] public string P1 { get; set; }
                        [Id(1)] public string P2 { get; set; }
                        [Id(1)] public string P3 { get; set; }
                    }

                    [GenerateSerializer]
                    public struct S
                    {
                        [Id(0)] public string P1 { get; set; }
                        [Id(1)] public string P2 { get; set; }
                        [Id(1)] public string P3 { get; set; }
                    }

                    [GenerateSerializer]
                    public record R(string P1, string P2, string P3)
                    {
                        [Id(0)] public string P4 { get; set; }
                        [Id(0)] public string P5 { get; set; }
                    }

                    [GenerateSerializer]
                    public record struct RS(string P1, string P2, string P3)
                    {
                        [Id(0)] public string P4 { get; set; }
                        [Id(0)] public string P5 { get; set; }
                    }
                    """;

        return VerifyHasDiagnostic(code, 8);
    }

    [Fact]
    public Task TypesWithGenerateSerializerAndNoDuplicatedIds_ShouldNoTriggerDiagnostic()
    {
        var code = """
                    [GenerateSerializer]
                    public class C
                    {
                        [Id(0)] public string P1 { get; set; }
                        [Id(1)] public string P2 { get; set; }
                        [Id(2)] public string P3 { get; set; }
                    }

                    [GenerateSerializer]
                    public struct S
                    {
                        [Id(0)] public string P1 { get; set; }
                        [Id(1)] public string P2 { get; set; }
                        [Id(2)] public string P3 { get; set; }
                    }

                    [GenerateSerializer]
                    public record R(string P1, string P2, string P3)
                    {
                        [Id(0)] public string P4 { get; set; }
                        [Id(1)] public string P5 { get; set; }
                    }

                    [GenerateSerializer]
                    public record struct RS(string P1, string P2, string P3)
                    {
                        [Id(0)] public string P4 { get; set; }
                        [Id(1)] public string P5 { get; set; }
                    }
                    """;

        return VerifyHasNoDiagnostic(code);
    }

    [Fact]
    public Task TypesWithoutGenerateSerializerAndDuplicatedIds_ShouldNotTriggerDiagnostic()
    {
        var code = """
                    public class C
                    {
                        [Id(0)] public string P1 { get; set; }
                        [Id(1)] public string P2 { get; set; }
                        [Id(1)] public string P3 { get; set; }
                    }

                    public struct S
                    {
                        [Id(0)] public string P1 { get; set; }
                        [Id(1)] public string P2 { get; set; }
                        [Id(1)] public string P3 { get; set; }
                    }

                    public record R(string P1, string P2, string P3)
                    {
                        [Id(0)] public string P4 { get; set; }
                        [Id(0)] public string P5 { get; set; }
                    }

                    public record struct RS(string P1, string P2, string P3)
                    {
                        [Id(0)] public string P4 { get; set; }
                        [Id(0)] public string P5 { get; set; }
                    }
                    """;

        return VerifyHasNoDiagnostic(code);
    }
}
