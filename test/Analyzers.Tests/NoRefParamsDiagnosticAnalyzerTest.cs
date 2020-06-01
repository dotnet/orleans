using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Orleans.Analyzers;
using Xunit;

namespace Analyzers.Tests
{
    [TestCategory("BVT"), TestCategory("Analyzer")]
    public class NoRefParamsDiagnosticAnalyzerTest : DiagnosticAnalyzerTestBase<NoRefParamsDiagnosticAnalyzer>
    {
        [Fact]
        public async Task NoRefParamsDiagnosticAnalyzerInInterface()
        {
            var code = @"public interface I : IGrain
                        {
                            Task GetSomeThing(ref int i);
                            Task GetSomeOtherThing(out int i);
                        }";

            var (diagnostics, _) = await this.GetDiagnosticsAsync(code, new string[0]);

            Assert.NotEmpty(diagnostics);
            Assert.Equal(2, diagnostics.Length);

            var diagnostic = diagnostics.First();
            Assert.Equal(NoRefParamsDiagnosticAnalyzer.DiagnosticId, diagnostic.Id);
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
            Assert.Equal(NoRefParamsDiagnosticAnalyzer.MessageFormat, diagnostic.GetMessage());
        }

        [Fact]
        public async Task NoRefParamsDiagnosticAnalyzerInClass()
        {
            var code = @"public interface I : IGrain
                        {
                            Task GetSomething(ref int i);
                            Task GetSomeOtherThing(out int i);
                        }

                        public class Imp : I
                        {
                            public Task GetSomething(ref int i) => Task.CompletedTask;
                            public Task GetSomeOtherThing(out int i)
                            {
                                i = 0;
                                return Task.CompletedTask;
                            }
                        }
                        ";

            var (diagnostics, _) = await this.GetDiagnosticsAsync(code, new string[0]);

            Assert.NotEmpty(diagnostics);
            Assert.Equal(2, diagnostics.Length);

            var diagnostic = diagnostics.First();
            Assert.Equal(NoRefParamsDiagnosticAnalyzer.DiagnosticId, diagnostic.Id);
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
            Assert.Equal(NoRefParamsDiagnosticAnalyzer.MessageFormat, diagnostic.GetMessage());
        }

        [Fact]
        public async Task NoRefParamsDiagnosticAnalyzerNoError()
        {
            var code = @"public interface I : IGrain
                        {
                            Task GetSomething(int i);
                        }

                        public class Imp : I
                        {
                            public Task GetSomething(int i) => Task.CompletedTask;
                        }
                        ";

            var (diagnostics, _) = await this.GetDiagnosticsAsync(code, new string[0]);

            Assert.Empty(diagnostics);
        }
    }
}
