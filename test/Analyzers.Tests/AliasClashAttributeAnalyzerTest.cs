using Microsoft.CodeAnalysis;
using Orleans.Analyzers;
using Xunit;

namespace Analyzers.Tests;

[TestCategory("BVT"), TestCategory("Analyzer")]
public class AliasClashAttributeAnalyzerTest : DiagnosticAnalyzerTestBase<AliasClashAttributeAnalyzer>
{
    private static readonly List<string> RuleIds = new();

    private async Task VerifyHasDiagnostic(string code, int diagnosticsCount)
    {
        var (diagnostics, _) = await GetDiagnosticsAsync(code, Array.Empty<string>());

        Assert.NotEmpty(diagnostics);
        Assert.Equal(diagnosticsCount, diagnostics.Length);

        Assert.All(diagnostics, diagnostic => RuleIds.Contains(diagnostic.Id));
        Assert.All(diagnostics, diagnostic => diagnostic.Severity.Equals(DiagnosticSeverity.Error));
    }

    private async Task VerifyHasNoDiagnostic(string code)
    {
        var (diagnostics, _) = await GetDiagnosticsAsync(code, Array.Empty<string>());
        Assert.Empty(diagnostics);
    }

    #region Methods

    [Fact]
    public Task SameAlias_SameContainingType_ShouldTriggerMethodsDiagnostic()
    {
        var code = """
                    namespace Orleans.MyNs
                    {
                    public interface I
                    {
                        [Alias("A1")] Task Void(string a);
                        [Alias("A1")] Task Void(int a);
                        [Alias("A1")] Task Void(float a);
                        [Alias("A2")] Task Void(long a);
                    }
                    }
                    """;

        return VerifyHasDiagnostic(code, 3);
    }

    [Fact]
    public Task SameAlias_DifferentContainingType_ShouldNotTriggerMethodsDiagnostic()
    {
        var code = """
                    public interface I1
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

    #endregion

    #region Types

    [Theory]
    [MemberData(nameof(TypesData))]
    public Task SameAlias_SameNamespace_ShouldTriggerTypesDiagnostic(string typeKind, string typeName)
    {
        var code = $$"""
                    namespace N1
                    {
                        [Alias("A")] public {{typeKind}} {{typeName}}1 {}
                        [Alias("A")] public {{typeKind}} {{typeName}}2 {}
                    }
                    """;

        return VerifyHasDiagnostic(code, 2);
    }

    [Theory]
    [MemberData(nameof(TypesData))]
    public Task DifferentAlias_SameNamespace_ShouldNotTriggerTypesDiagnostic(string typeKind, string typeName)
    {
        var code = $$"""
                    namespace N1
                    {
                        [Alias("A1")] public {{typeKind}} {{typeName}}1 {}
                        [Alias("A2")] public {{typeKind}} {{typeName}}2 {}
                    }
                    """;

        return VerifyHasNoDiagnostic(code);
    }

    [Theory]
    [MemberData(nameof(TypesData))]
    public Task SameAlias_DifferentNamespace_ShouldTriggerTypesDiagnostic(string typeKind, string typeName)
    {
        var code = $$"""
                    namespace N1
                    {
                        [Alias("A")] public {{typeKind}} {{typeName}} {}
                    }

                    namespace N2
                    {
                        [Alias("A")] public {{typeKind}} {{typeName}} {}
                    }
                    """;

        return VerifyHasDiagnostic(code, 2);
    }

    [Theory]
    [MemberData(nameof(TypesData))]
    public Task DifferentAlias_DifferentNamespace_ShouldNotTriggerTypesDiagnostic(string typeKind, string typeName)
    {
        var code = $$"""
                    namespace N1
                    {
                        [Alias("A1")] public {{typeKind}} {{typeName}} {}
                    }

                    namespace N2
                    {
                        [Alias("A2")] public {{typeKind}} {{typeName}} {}
                    }
                    """;

        return VerifyHasNoDiagnostic(code);
    }

    public static IEnumerable<object[]> TypesData =>
       new List<object[]>
       {
            new object[] { "interface", "I" },
            new object[] { "class", "C" },
            new object[] { "struct", "S" },
            new object[] { "record", "R" },
            new object[] { "record struct", "RS" }
       };

    #endregion
}
