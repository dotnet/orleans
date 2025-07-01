using Microsoft.CodeAnalysis;
using Orleans.Analyzers;
using Xunit;

namespace Analyzers.Tests
{
    /// <summary>
    /// Tests for the analyzer that prevents ref, out, and in parameters in grain interface methods.
    /// Orleans grain calls are distributed RPC calls that serialize parameters, so ref/out/in
    /// parameters cannot work as they would in local method calls. This analyzer helps developers
    /// avoid this common mistake and ensures grain interfaces follow the Orleans programming model.
    /// </summary>
    [TestCategory("BVT"), TestCategory("Analyzer")]
    public class NoRefParamsDiagnosticAnalyzerTest : DiagnosticAnalyzerTestBase<NoRefParamsDiagnosticAnalyzer>
    {
        /// <summary>
        /// Verifies that the analyzer detects ref parameters in grain interface methods.
        /// Ref parameters cannot work across distributed calls because they require
        /// shared memory access, which is impossible in a distributed system.
        /// </summary>
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

        /// <summary>
        /// Verifies that the analyzer detects out parameters in grain interface methods.
        /// Out parameters cannot work across distributed calls because they require
        /// the ability to write back to the caller's memory, which is impossible in RPC.
        /// </summary>
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

        /// <summary>
        /// Verifies that the analyzer detects multiple ref/out parameter violations
        /// in grain interface methods, ensuring all problematic parameters are reported.
        /// </summary>
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

        /// <summary>
        /// Verifies that the analyzer allows regular parameters in grain interface methods
        /// and doesn't interfere with ref/out/in parameters in non-grain methods,
        /// which are outside the scope of Orleans restrictions.
        /// </summary>
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

        /// <summary>
        /// Verifies that static interface methods can use ref/out parameters.
        /// Static methods don't participate in grain calls and execute locally,
        /// so they're not subject to the distributed calling restrictions.
        /// </summary>
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
