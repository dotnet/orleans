using Microsoft.CodeAnalysis;
using Orleans.Analyzers;
using Xunit;

namespace Analyzers.Tests;

[TestCategory("BVT"), TestCategory("Analyzer")]
public class GenerateSerializationAttributesAnalyzerTest : DiagnosticAnalyzerTestBase<GenerateSerializationAttributesAnalyzer>
{
    private async Task VerifyGeneratedDiagnostic(string code)
    {
        var (diagnostics, _) = await GetDiagnosticsAsync(code, new string[0]);

        Assert.NotEmpty(diagnostics);
        Assert.Single(diagnostics);

        var diagnostic = diagnostics.First();
        Assert.Equal(GenerateSerializationAttributesAnalyzer.RuleId, diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
    }

    [Fact]
    public Task SerializableClass()
        => VerifyGeneratedDiagnostic(@"[GenerateSerializer] public class D { public int f; }");

    [Fact]
    public Task SerializableStruct()
        => VerifyGeneratedDiagnostic(@"[GenerateSerializer] public struct D { public int f; }");

    [Fact]
    public Task SerializableRecord()
        => VerifyGeneratedDiagnostic(@"[GenerateSerializer] public record D { public int f; }");

    [Fact]
    public Task SerializableRecordStruct()
        => VerifyGeneratedDiagnostic(@"[GenerateSerializer] public record struct D { public int f; }");
}
