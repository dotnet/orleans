using Microsoft.CodeAnalysis;
using Orleans.Analyzers;
using Xunit;

namespace Analyzers.Tests;

/// <summary>
/// Tests for the analyzer that ensures types marked with [GenerateSerializer] have proper field attributes.
/// In Orleans serialization, each serializable field must be marked with an [Id] attribute to ensure
/// version tolerance and efficient serialization. This analyzer helps developers identify missing
/// field attributes that could cause serialization issues.
/// </summary>
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
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    /// <summary>
    /// Verifies that the analyzer detects when a class with [GenerateSerializer]
    /// has fields without [Id] attributes, which are required for proper Orleans serialization.
    /// </summary>
    [Fact]
    public Task SerializableClass()
        => VerifyGeneratedDiagnostic(@"[GenerateSerializer] public class D { public int f; }");

    /// <summary>
    /// Verifies that the analyzer detects when a struct with [GenerateSerializer]
    /// has fields without [Id] attributes, which are required for proper Orleans serialization.
    /// </summary>
    [Fact]
    public Task SerializableStruct()
        => VerifyGeneratedDiagnostic(@"[GenerateSerializer] public struct D { public int f; }");

    /// <summary>
    /// Verifies that the analyzer detects when a record with [GenerateSerializer]
    /// has fields without [Id] attributes, which are required for proper Orleans serialization.
    /// </summary>
    [Fact]
    public Task SerializableRecord()
        => VerifyGeneratedDiagnostic(@"[GenerateSerializer] public record D { public int f; }");

    /// <summary>
    /// Verifies that the analyzer detects when a record class with [GenerateSerializer]
    /// has fields without [Id] attributes, which are required for proper Orleans serialization.
    /// </summary>
    [Fact]
    public Task SerializableRecordClass()
        => VerifyGeneratedDiagnostic(@"[GenerateSerializer] public record class D { public int f; }");

    /// <summary>
    /// Verifies that the analyzer detects when a record struct with [GenerateSerializer]
    /// has fields without [Id] attributes, which are required for proper Orleans serialization.
    /// </summary>
    [Fact]
    public Task SerializableRecordStruct()
        => VerifyGeneratedDiagnostic(@"[GenerateSerializer] public record struct D { public int f; }");
}
