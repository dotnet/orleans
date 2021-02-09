using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Orleans.Analyzers;
using Xunit;

namespace Analyzers.Tests
{
    [TestCategory("BVT"), TestCategory("Analyzer")]
    public class InheritFromGrainBaseAnalyzerTests : DiagnosticAnalyzerTestBase<InheritFromGrainBaseAnalyzer>
    {
        protected override Task<(Diagnostic[], string)> GetDiagnosticsAsync(string source, params string[] extraUsings)
            => base.GetDiagnosticsAsync(source, extraUsings.Concat(new[] { "Orleans.Concurrency" }).ToArray());

        [Fact]
        public async Task InheritFromGrain_Analyzer_NoWarningsIfNotImplementingIGrain() => await this.AssertNoDiagnostics(@"
class A
{
    Task M() => Task.CompletedTask;
}
");

        [Fact]
        public async Task InheritFromGrain_Analyzer_NoWarningsImplementingIGrain_AndInheritingFromGrain() => await this.AssertNoDiagnostics(@"
class A : Grain, IGrain
{
    Task M() => Task.CompletedTask;
}
");

        [Fact]
        public async Task InheritFromGrain_Analyzer_WarningIfImplementingIGrain_AndNotInheritingFromGrain()
        {
            var (diagnostics, source) = await this.GetDiagnosticsAsync(@"
public class A : IGrain
{
    Task M() => Task.CompletedTask;
}
");

            var diagnostic = diagnostics.Single();

            Assert.Equal(InheritFromGrainBaseAnalyzer.DiagnosticId, diagnostic.Id);
            Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
            Assert.Equal(InheritFromGrainBaseAnalyzer.MessageFormat, diagnostic.GetMessage());
        }

        [Fact]
        public async Task InheritFromGrain_Analyzer_WarningIfImplementingIGrainWithGuidCompoundKey_AndNotInheritingFromGrain()
        {
            var (diagnostics, source) = await this.GetDiagnosticsAsync(@"
public class A : IGrainWithGuidCompoundKey
{
    Task M() => Task.CompletedTask;
}
");

            var diagnostic = diagnostics.Single();

            Assert.Equal(InheritFromGrainBaseAnalyzer.DiagnosticId, diagnostic.Id);
            Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
            Assert.Equal(InheritFromGrainBaseAnalyzer.MessageFormat, diagnostic.GetMessage());
        }
    }
}