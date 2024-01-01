using Microsoft.CodeAnalysis;
using Orleans.Analyzers;
using Xunit;

namespace Analyzers.Tests
{
    [TestCategory("BVT"), TestCategory("Analyzer")]
    public class AlwaysInterleaveDiagnosticAnalyzerTest : DiagnosticAnalyzerTestBase<AlwaysInterleaveDiagnosticAnalyzer>
    {
        protected override Task<(Diagnostic[], string)> GetDiagnosticsAsync(string source, params string[] extraUsings)
            => base.GetDiagnosticsAsync(source, extraUsings.Concat(new[] { "Orleans.Concurrency" }).ToArray());

        [Fact]
        public async Task AlwaysInterleave_Analyzer_NoWarningsIfAttributeIsNotUsed() => await this.AssertNoDiagnostics(@"
class C
{
    Task M() => Task.CompletedTask;
}
");

        [Fact]
        public async Task AlwaysInterleave_Analyzer_NoWarningsIfAttributeIsUsedOnInterface() => await this.AssertNoDiagnostics(@"
public interface I : IGrain
{
    [AlwaysInterleave]
    Task<string> M();
}
");

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