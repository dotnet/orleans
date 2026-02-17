using Microsoft.CodeAnalysis;
using Orleans.Analyzers;
using Xunit;

namespace Analyzers.Tests
{
    /// <summary>
    /// Tests for the AlwaysInterleaveDiagnosticAnalyzer which ensures that the [AlwaysInterleave] attribute
    /// is only used on grain interface methods, not on grain implementation methods. The attribute must be
    /// declared on the interface to properly configure interleaving behavior for all implementations.
    /// </summary>
    [TestCategory("BVT"), TestCategory("Analyzer")]
    public class AlwaysInterleaveDiagnosticAnalyzerTest : DiagnosticAnalyzerTestBase<AlwaysInterleaveDiagnosticAnalyzer>
    {
        protected override Task<(Diagnostic[], string)> GetDiagnosticsAsync(string source, params string[] extraUsings)
            => base.GetDiagnosticsAsync(source, extraUsings.Concat(new[] { "Orleans.Concurrency" }).ToArray());

        /// <summary>
        /// Verifies that no diagnostic is reported when the [AlwaysInterleave] attribute is not used at all.
        /// </summary>
        [Fact]
        public async Task AlwaysInterleave_Analyzer_NoWarningsIfAttributeIsNotUsed() => await this.AssertNoDiagnostics(@"
class C
{
    Task M() => Task.CompletedTask;
}
");

        /// <summary>
        /// Verifies that no diagnostic is reported when the [AlwaysInterleave] attribute is correctly used on a grain interface method.
        /// This is the correct usage pattern for the attribute.
        /// </summary>
        [Fact]
        public async Task AlwaysInterleave_Analyzer_NoWarningsIfAttributeIsUsedOnInterface() => await this.AssertNoDiagnostics(@"
public interface I : IGrain
{
    [AlwaysInterleave]
    Task<string> M();
}
");

        /// <summary>
        /// Verifies that a diagnostic error is reported when the [AlwaysInterleave] attribute is incorrectly used
        /// on a grain implementation method instead of the interface method. This is an error because interleaving
        /// behavior must be specified at the interface level.
        /// </summary>
        [Fact]
        public async Task AlwaysInterleave_Analyzer_WarningIfAttributeisUsedOnGrainClass()
        {
            var (diagnostics, source) = await this.GetDiagnosticsAsync(@"
public interface I : IGrain
{
    Task<int> Method();
}

public class C : I
{
    [AlwaysInterleave]
    public Task<int> Method() => Task.FromResult(0);
}
");

            var diagnostic = diagnostics.Single();

            Assert.Equal(AlwaysInterleaveDiagnosticAnalyzer.DiagnosticId, diagnostic.Id);
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
            Assert.Equal(AlwaysInterleaveDiagnosticAnalyzer.MessageFormat, diagnostic.GetMessage());

            var span = diagnostic.Location.SourceSpan;
            Assert.Equal("AlwaysInterleave", source[span.Start..span.End]);
        }
    }
}