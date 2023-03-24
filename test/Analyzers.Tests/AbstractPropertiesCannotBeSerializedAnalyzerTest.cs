using Microsoft.CodeAnalysis;
using Orleans.Analyzers;
using Xunit;

namespace Analyzers.Tests;

[TestCategory("BVT"), TestCategory("Analyzer")]
public class AbstractPropertiesCannotBeSerializedAnalyzerTest : DiagnosticAnalyzerTestBase<AbstractPropertiesCannotBeSerializedAnalyzer>
{
    async Task VerifyGeneratedDiagnostic(string code)
    {
        var (diagnostics, _) = await GetDiagnosticsAsync(code, new string[0]);

        Assert.NotEmpty(diagnostics);
        Assert.Single(diagnostics);

        var diagnostic = diagnostics.First();
        Assert.Equal(AbstractPropertiesCannotBeSerializedAnalyzer.RuleId, diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public Task AliasedAttribute()
        => VerifyGeneratedDiagnostic("""
using alias = Orleans;
[alias::GenerateSerializer]
public abstract class D { [alias::Id(0)] public abstract int F { get; set; } }
""");

    [Fact]
    public Task GloballyQualifiedAttribute()
        => VerifyGeneratedDiagnostic("""
[global::Orleans.GenerateSerializer]
public abstract class D { [global::Orleans.Id(0)] public abstract int F { get; set; } }
""");

    [Fact]
    public Task SimpleAttribute()
        => VerifyGeneratedDiagnostic("""
[GenerateSerializer] public abstract class D { [Id(0)] public abstract int F { get; set; } }
""");

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