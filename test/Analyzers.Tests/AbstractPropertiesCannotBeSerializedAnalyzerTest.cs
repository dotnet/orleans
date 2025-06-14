using Microsoft.CodeAnalysis;
using Orleans.Analyzers;
using Xunit;

namespace Analyzers.Tests;

/// <summary>
/// Tests for the AbstractPropertiesCannotBeSerializedAnalyzer which ensures that abstract properties
/// cannot be marked with serialization attributes since they have no concrete implementation to serialize.
/// This analyzer prevents runtime errors by catching invalid serialization configurations at compile time.
/// </summary>
[TestCategory("BVT"), TestCategory("Analyzer")]
public class AbstractPropertiesCannotBeSerializedAnalyzerTest : DiagnosticAnalyzerTestBase<AbstractPropertiesCannotBeSerializedAnalyzer>
{
    private async Task VerifyGeneratedDiagnostic(string code)
    {
        var (diagnostics, _) = await GetDiagnosticsAsync(code, new string[0]);

        Assert.NotEmpty(diagnostics);
        Assert.Single(diagnostics);

        var diagnostic = diagnostics.First();
        Assert.Equal(AbstractPropertiesCannotBeSerializedAnalyzer.RuleId, diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    /// <summary>
    /// Verifies that the analyzer detects abstract properties with serialization attributes when using namespace aliases.
    /// </summary>
    [Fact]
    public Task AliasedAttribute()
        => VerifyGeneratedDiagnostic("""
using alias = Orleans;
[alias::GenerateSerializer]
public abstract class D { [alias::Id(0)] public abstract int F { get; set; } }
""");

    /// <summary>
    /// Verifies that the analyzer detects abstract properties with serialization attributes when using globally qualified namespaces.
    /// </summary>
    [Fact]
    public Task GloballyQualifiedAttribute()
        => VerifyGeneratedDiagnostic("""
[global::Orleans.GenerateSerializer]
public abstract class D { [global::Orleans.Id(0)] public abstract int F { get; set; } }
""");

    /// <summary>
    /// Verifies that the analyzer detects abstract properties with serialization attributes when using simple attribute names.
    /// </summary>
    [Fact]
    public Task SimpleAttribute()
        => VerifyGeneratedDiagnostic("""
[GenerateSerializer] public abstract class D { [Id(0)] public abstract int F { get; set; } }
""");

    /// <summary>
    /// Verifies that the analyzer correctly identifies abstract properties with serialization attributes
    /// even when other unrelated generic attributes are present.
    /// </summary>
    [Fact]
    public Task UnrelatedGenericAttribute()
        => VerifyGeneratedDiagnostic("""
public class GenericAttribute<T> : Attribute { }
[GenerateSerializer]
public abstract class D {
    [GenericAttribute<bool>] [Id(0)] public abstract int F { get; set; }
}
""");
}