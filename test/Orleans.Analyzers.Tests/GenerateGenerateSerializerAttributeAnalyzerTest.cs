using Microsoft.CodeAnalysis;
using Orleans.Analyzers;
using Xunit;

namespace Analyzers.Tests;

/// <summary>
/// Tests for the analyzer that suggests using [GenerateSerializer] instead of [Serializable] attribute.
/// Orleans uses its own serialization system for efficient grain communication, and the [GenerateSerializer]
/// attribute enables code generation for optimal serialization performance. Using the legacy [Serializable]
/// attribute can lead to slower serialization and larger message sizes.
/// </summary>
[TestCategory("BVT"), TestCategory("Analyzer")]
public class GenerateGenerateSerializerAttributeAnalyzerTest : DiagnosticAnalyzerTestBase<GenerateGenerateSerializerAttributeAnalyzer>
{
    private async Task VerifyGeneratedDiagnostic(string code)
    {
        var (diagnostics, _) = await GetDiagnosticsAsync(code, new string[0]);

        Assert.NotEmpty(diagnostics);
        Assert.Single(diagnostics);

        var diagnostic = diagnostics.First();
        Assert.Equal(GenerateGenerateSerializerAttributeAnalyzer.RuleId, diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
    }

    /// <summary>
    /// Verifies that the analyzer detects when a class uses [Serializable] attribute
    /// and suggests using [GenerateSerializer] instead for better Orleans serialization performance.
    /// </summary>
    [Fact]
    public Task SerializableClass()
        => VerifyGeneratedDiagnostic(@"[System.Serializable] public class D { }");

    /// <summary>
    /// Verifies that the analyzer detects when a struct uses [Serializable] attribute
    /// and suggests using [GenerateSerializer] instead for better Orleans serialization performance.
    /// </summary>
    [Fact]
    public Task SerializableStruct()
        => VerifyGeneratedDiagnostic(@"[System.Serializable] public struct D { }");

    /// <summary>
    /// Verifies that the analyzer detects when a record uses [Serializable] attribute
    /// and suggests using [GenerateSerializer] instead for better Orleans serialization performance.
    /// </summary>
    [Fact]
    public Task SerializableRecord()
        => VerifyGeneratedDiagnostic(@"[System.Serializable] public record D { }");

    /// <summary>
    /// Verifies that the analyzer detects when a record struct uses [Serializable] attribute
    /// and suggests using [GenerateSerializer] instead for better Orleans serialization performance.
    /// </summary>
    [Fact]
    public Task SerializableRecordStruct()
        => VerifyGeneratedDiagnostic(@"[System.Serializable] public record struct D { }");
}
