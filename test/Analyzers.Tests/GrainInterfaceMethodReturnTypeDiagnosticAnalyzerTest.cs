using Microsoft.CodeAnalysis;
using Orleans.Analyzers;
using Xunit;

namespace Analyzers.Tests;

[TestCategory("BVT"), TestCategory("Analyzer")]
public class GrainInterfaceMethodReturnTypeDiagnosticAnalyzerTest : DiagnosticAnalyzerTestBase<GrainInterfaceMethodReturnTypeDiagnosticAnalyzer>
{
    private const string DiagnosticId = GrainInterfaceMethodReturnTypeDiagnosticAnalyzer.DiagnosticId;
    private const string MessageFormat = GrainInterfaceMethodReturnTypeDiagnosticAnalyzer.MessageFormat;

    [Fact]
    public async Task GrainInterfaceMethodReturnTypeNoError()
    {
        var code = """
                    public interface IG : Orleans.IGrain
                    {
                        Task TaskMethod(int a);
                        Task<int> TaskOfIntMethod(int a);
                        ValueTask ValueTaskMethod(int a);
                        ValueTask<int> ValueTaskOfIntMethod(int a);
                        void VoidMethod(int a);
                    }

                    public interface IA : Orleans.Runtime.IAddressable
                    {
                        Task TaskMethod(int a);
                        Task<int> TaskOfIntMethod(int a);
                        ValueTask ValueTaskMethod(int a);
                        ValueTask<int> ValueTaskOfIntMethod(int a);
                        void VoidMethod(int a);
                    }

                    public interface IGO : Orleans.IGrainObserver
                    {
                        Task TaskMethod(int a);
                        Task<int> TaskOfIntMethod(int a);
                        ValueTask ValueTaskMethod(int a);
                        ValueTask<int> ValueTaskOfIntMethod(int a);
                        void VoidMethod(int a);
                    }
                    """;

        var (diagnostics, _) = await this.GetDiagnosticsAsync(code, new string[0]);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task IncompatibleGrainInterfaceMethodReturnType()
    {
        var code = """
                    public interface I : Orleans.IGrain
                    {
                        int MyMethod(int a);
                    }
                    """;

        var (diagnostics, _) = await this.GetDiagnosticsAsync(code, new string[0]);

        Assert.NotEmpty(diagnostics);
        Assert.Single(diagnostics);

        var diagnostic = diagnostics.First();
        Assert.Equal(DiagnosticId, diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(MessageFormat, diagnostic.GetMessage());
    }

    [Fact]
    public async Task StaticInterfaceMethodsWithRegularReturnsAreAllowed()
    {
        var code = """
                    public interface I : Orleans.IGrain
                    {
                        public static int GetSomeOtherThing(int a) => 0;
                    }
                    """;

        var (diagnostics, _) = await this.GetDiagnosticsAsync(code, new string[0]);
        Assert.Empty(diagnostics);
    }
}
