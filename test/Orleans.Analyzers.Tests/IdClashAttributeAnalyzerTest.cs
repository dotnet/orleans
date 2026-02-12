using Microsoft.CodeAnalysis;
using Orleans.Analyzers;
using Xunit;

namespace Analyzers.Tests;

/// <summary>
/// Tests for the analyzer that detects duplicate [Id] attributes in serializable types.
/// In Orleans serialization, each field must have a unique [Id] attribute value to ensure
/// proper deserialization and version tolerance. Duplicate IDs would cause serialization
/// conflicts and data corruption when messages are exchanged between grains.
/// </summary>
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

    /// <summary>
    /// Verifies that the analyzer detects when multiple fields in a [GenerateSerializer] type
    /// have the same [Id] value. This would cause serialization conflicts as the serializer
    /// wouldn't know which field to map to which ID during deserialization.
    /// </summary>
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

    /// <summary>
    /// Verifies that the analyzer doesn't report issues when all fields in a [GenerateSerializer]
    /// type have unique [Id] values. This is the correct pattern for Orleans serialization.
    /// </summary>
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

    /// <summary>
    /// Verifies that the analyzer ignores types without [GenerateSerializer] attribute,
    /// even if they have duplicate [Id] attributes. The analyzer only validates types
    /// that participate in Orleans serialization.
    /// </summary>
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
