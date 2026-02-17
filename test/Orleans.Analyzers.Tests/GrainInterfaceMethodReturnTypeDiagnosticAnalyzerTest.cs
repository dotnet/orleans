using Microsoft.CodeAnalysis;
using Orleans.Analyzers;
using Xunit;

namespace Analyzers.Tests;

/// <summary>
/// Tests for the analyzer that enforces proper return types for grain interface methods.
/// Orleans grain methods must return Task, Task&lt;T&gt;, ValueTask, ValueTask&lt;T&gt;, or void
/// because grain calls are inherently asynchronous across distributed systems.
/// This analyzer prevents developers from using synchronous return types that would
/// break the Orleans programming model.
/// </summary>
[TestCategory("BVT"), TestCategory("Analyzer")]
public class GrainInterfaceMethodReturnTypeDiagnosticAnalyzerTest : DiagnosticAnalyzerTestBase<GrainInterfaceMethodReturnTypeDiagnosticAnalyzer>
{
    private const string DiagnosticId = GrainInterfaceMethodReturnTypeDiagnosticAnalyzer.DiagnosticId;
    private const string MessageFormat = GrainInterfaceMethodReturnTypeDiagnosticAnalyzer.MessageFormat;

    /// <summary>
    /// Verifies that the analyzer accepts valid return types for grain interface methods:
    /// Task, Task&lt;T&gt;, ValueTask, ValueTask&lt;T&gt;, and void are all allowed because they
    /// support the asynchronous nature of grain calls in Orleans.
    /// </summary>
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

    /// <summary>
    /// Verifies that the analyzer detects when a grain interface method returns a synchronous type (int).
    /// This is invalid because grain calls must be asynchronous to work across the distributed cluster.
    /// </summary>
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

    /// <summary>
    /// Verifies that static interface methods can have any return type since they are not
    /// subject to the grain call restrictions. Static methods don't participate in the
    /// distributed grain invocation mechanism.
    /// </summary>
    [Fact]
    public async Task StaticInterfaceMethodsWithRegularReturnsAreAllowed()
    {
        var code = """
                    public interface I : Orleans.IGrain
                    {
                        public static int GetSomeOtherThing(int a) => 0;
                        public static virtual int GetSomeOtherThingVirtual(int a) => 0;
                    }
                    """;

        var (diagnostics, _) = await this.GetDiagnosticsAsync(code, new string[0]);
        Assert.Empty(diagnostics);
    }
}
