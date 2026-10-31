using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
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
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Analyzer")]
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

    [Fact]
    public async Task AliasedAttributesOnPartialGrain_ShouldReportExactAttributeLocations()
    {
        const string alias = "StableName(\"grain\")";
        const string serializer = "Serializer";
        var source = $$"""
            using StableName = Orleans.AliasAttribute;
            using Serializer = Orleans.GenerateSerializerAttribute;

            public partial class C : Orleans.Grain { }

            [{{alias}}, {{serializer}}]
            public partial class C { }
            """;

        var diagnostics = await GetDiagnosticsFullSourceAsync(source);

        Assert.Collection(diagnostics,
            diagnostic => AssertAttributeDiagnostic(diagnostic, source.IndexOf(alias, StringComparison.Ordinal), alias.Length),
            diagnostic => AssertAttributeDiagnostic(diagnostic, source.IndexOf(", " + serializer + "]", StringComparison.Ordinal) + 2, serializer.Length));

        static void AssertAttributeDiagnostic(Diagnostic diagnostic, int start, int length)
        {
            Assert.Equal(IncorrectAttributeUseAnalyzer.RuleId, diagnostic.Id);
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
            Assert.Equal(new TextSpan(start, length), diagnostic.Location.SourceSpan);
        }
    }

    public static IEnumerable<object[]> Attributes =>
        new List<object[]>
        {
            new object[] { "Alias(\"alias\")" },
            new object[] { "GenerateSerializer" }
        };
}
