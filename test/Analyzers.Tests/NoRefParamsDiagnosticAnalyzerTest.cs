using Microsoft.CodeAnalysis;
using Orleans.Analyzers;
using Xunit;

namespace Analyzers.Tests
{
    [TestCategory("BVT"), TestCategory("Analyzer")]
    public class NoRefParamsDiagnosticAnalyzerTest : DiagnosticAnalyzerTestBase<NoRefParamsDiagnosticAnalyzer>
    {
        [Fact]
        public async Task NoRefParamsAllowedInGrainInterfaceMethods()
        {
            var code = @"public interface I : IGrain
                        {
                            Task GetSomeOtherThing(int a, ref int i);
                        }";

            var (diagnostics, _) = await this.GetDiagnosticsAsync(code, new string[0]);

            Assert.NotEmpty(diagnostics);
            Assert.Single(diagnostics);

            var diagnostic = diagnostics.First();
            Assert.Equal(NoRefParamsDiagnosticAnalyzer.DiagnosticId, diagnostic.Id);
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
            Assert.Equal(NoRefParamsDiagnosticAnalyzer.MessageFormat, diagnostic.GetMessage());
        }

        [Fact]
        public async Task NoOutParamsAllowedInGrainInterfaceMethods()
        {
            var code = @"public interface I : IGrain
                        {
                            Task GetSomeOtherThing(out int i);
                        }";

            var (diagnostics, _) = await this.GetDiagnosticsAsync(code, new string[0]);

            Assert.NotEmpty(diagnostics);
            Assert.Single(diagnostics);

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
                            public Task SomeNonInterfaceMethod(ref int value, out int value2, in int value3)
                            {
                                value = 0;
                                value2 = 0;
                                return Task.CompletedTask;
                            }
                        }
                        ";

            var (diagnostics, _) = await this.GetDiagnosticsAsync(code, new string[0]);

            Assert.Empty(diagnostics);
        }

        [Fact]
        public async Task OutAndRefParamsAllowedInStaticGrainInterfaceMethods()
        {
            var code = @"public interface I : IGrain
                        {
                            public static bool SomeStaticMethod(out int o, ref int v) { o = 0; return false; }
                            public static virtual bool SomeStaticVirtualMethod(out int o, ref int v) { o = 0; return false; }
                        }";

            var (diagnostics, _) = await this.GetDiagnosticsAsync(code, new string[0]);
            Assert.Empty(diagnostics);
        }
    }
}
