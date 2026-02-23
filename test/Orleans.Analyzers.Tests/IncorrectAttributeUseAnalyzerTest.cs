using Microsoft.CodeAnalysis;
using Orleans.Analyzers;
using Xunit;

namespace Analyzers.Tests;

/// <summary>
/// Tests for the analyzer that prevents incorrect use of serialization attributes on grain classes.
/// Grain classes (inheriting from Grain or Grain&lt;T&gt;) should not be marked with [Alias] or
/// [GenerateSerializer] because grains are not serialized - only their state and method parameters are.
/// This analyzer helps developers avoid confusion between grain implementations and serializable data types.
/// </summary>
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

    /// <summary>
    /// Verifies that the analyzer detects when a class inheriting from Grain has
    /// serialization attributes applied. Grains are not serialized themselves,
    /// so these attributes are meaningless and indicate a misunderstanding of the Orleans model.
    /// </summary>
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

    /// <summary>
    /// Verifies that the analyzer allows serialization attributes on regular classes
    /// that don't inherit from Grain. These classes can be serialized as method parameters
    /// or grain state.
    /// </summary>
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

    /// <summary>
    /// Verifies that grain classes without serialization attributes don't trigger
    /// the analyzer. This is the correct pattern for grain implementations.
    /// </summary>
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

    /// <summary>
    /// Verifies that the analyzer detects when a class inheriting from Grain&lt;T&gt; has
    /// serialization attributes applied. Like non-generic grains, stateful grains
    /// are not serialized themselves - only their state is.
    /// </summary>
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

    /// <summary>
    /// Verifies that the analyzer allows serialization attributes on regular classes
    /// that don't inherit from Grain&lt;T&gt;. These classes can be serialized as method parameters
    /// or grain state.
    /// </summary>
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

    /// <summary>
    /// Verifies that stateful grain classes without serialization attributes don't trigger
    /// the analyzer. This is the correct pattern for stateful grain implementations.
    /// </summary>
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
