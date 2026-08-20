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
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Analyzer")]
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

    [Fact]
    public async Task RegisteredCustomReturnTypeIsAllowed()
    {
        var code = """
                    [Orleans.InvokableBaseType(
                        typeof(Orleans.Runtime.GrainReference),
                        typeof(CustomCall<>),
                        typeof(CustomRequest<>))]
                    public class CustomCall<T> { }

                    [Orleans.Invocation.ReturnValueProxy(nameof(InitializeRequest))]
                    public abstract class CustomRequest<T>
                    {
                        public CustomCall<T> InitializeRequest(Orleans.Runtime.GrainReference proxy) => new();
                    }

                    public interface I : Orleans.IGrain
                    {
                        CustomCall<int> MyMethod();
                    }
                    """;

        var (diagnostics, _) = await this.GetDiagnosticsAsync(code, new string[0]);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task AssemblyRegistrationCannotReplaceProxyDefault()
    {
        var code = """
                    [assembly: Orleans.InvokableBaseType(
                        typeof(Orleans.Runtime.GrainReference),
                        typeof(Task),
                        typeof(CustomRequest))]

                    public abstract class CustomRequest { }

                    public interface I : Orleans.IGrain
                    {
                        Task MyMethod();
                    }
                    """;

        var (diagnostics, _) = await this.GetDiagnosticsAsync(code, []);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(GrainInterfaceMethodReturnTypeDiagnosticAnalyzer.InvalidMappingDiagnosticId, diagnostic.Id);
        Assert.Contains("cannot replace proxy default", diagnostic.GetMessage());
    }

    [Fact]
    public async Task InheritedMethodInitializerIsValidatedForDerivedProxyReceiver()
    {
        var code = """
                    [Orleans.InvokableBaseType(
                        typeof(Orleans.Runtime.GrainReference),
                        typeof(CustomCall),
                        typeof(CustomRequest))]
                    public class CustomCall { }

                    [Orleans.Invocation.ReturnValueProxy(nameof(InitializeRequest))]
                    public abstract class CustomRequest
                    {
                        public CustomCall InitializeRequest(IBase proxy) => new();
                        public object InitializeRequest(IDerived proxy) => new();
                    }

                    public interface IBase : Orleans.IGrain
                    {
                        CustomCall Call();
                    }

                    public interface IDerived : IBase { }
                    """;

        var (diagnostics, _) = await this.GetDiagnosticsAsync(code, []);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(GrainInterfaceMethodReturnTypeDiagnosticAnalyzer.InvalidMappingDiagnosticId, diagnostic.Id);
        Assert.Contains("Return-value proxy initializer", diagnostic.GetMessage());
    }

    [Fact]
    public async Task ConstructedInheritedMethodInitializerIsValidatedForDerivedProxyReceiver()
    {
        var code = """
                    [Orleans.InvokableBaseType(
                        typeof(Orleans.Runtime.GrainReference),
                        typeof(CustomCall<>),
                        typeof(CustomRequest<>))]
                    public class CustomCall<T> { }

                    [Orleans.Invocation.ReturnValueProxy(nameof(InitializeRequest))]
                    public abstract class CustomRequest<T>
                    {
                        public CustomCall<T> InitializeRequest(IBase<T> proxy) => new();
                        public object InitializeRequest(IDerived proxy) => new();
                    }

                    public interface IBase<T> : Orleans.IGrain
                    {
                        CustomCall<T> Call();
                    }

                    public interface IDerived : IBase<int> { }
                    """;

        var (diagnostics, _) = await this.GetDiagnosticsAsync(code, []);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(GrainInterfaceMethodReturnTypeDiagnosticAnalyzer.InvalidMappingDiagnosticId, diagnostic.Id);
        Assert.Contains("Return-value proxy initializer", diagnostic.GetMessage());
    }

    [Theory]
    [InlineData("ProxyA", "ProxyB", false)]
    [InlineData("ProxyB", "ProxyA", true)]
    public async Task DirectSerializerAttributesMatchGeneratorProxySelection(
        string firstProxy,
        string secondProxy,
        bool expectDiagnostic)
    {
        var code = $$"""
                    [assembly: Orleans.InvokableBaseType(typeof(ProxyA), typeof(CustomCall), typeof(CustomRequest))]

                    public abstract class ProxyA { }
                    public abstract class ProxyB { }
                    public class CustomCall { }
                    public abstract class CustomRequest { }

                    [Orleans.GenerateMethodSerializers(typeof({{firstProxy}}))]
                    [Orleans.GenerateMethodSerializers(typeof({{secondProxy}}))]
                    public interface ICustomGrain : Orleans.IGrain
                    {
                        CustomCall Call();
                    }
                    """;

        var (diagnostics, _) = await this.GetDiagnosticsAsync(code, []);

        Assert.Equal(expectDiagnostic, diagnostics.Any(static diagnostic =>
            diagnostic.Id == GrainInterfaceMethodReturnTypeDiagnosticAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("IProxyA", "IProxyB", false)]
    [InlineData("IProxyB", "IProxyA", true)]
    public async Task InheritedSerializerAttributesMatchGeneratorProxySelection(
        string firstInterface,
        string secondInterface,
        bool expectDiagnostic)
    {
        var code = $$"""
                    [assembly: Orleans.InvokableBaseType(typeof(ProxyA), typeof(CustomCall), typeof(CustomRequest))]

                    public abstract class ProxyA { }
                    public abstract class ProxyB { }
                    public class CustomCall { }
                    public abstract class CustomRequest { }

                    [Orleans.GenerateMethodSerializers(typeof(ProxyA))]
                    public interface IProxyA : Orleans.IGrain { }

                    [Orleans.GenerateMethodSerializers(typeof(ProxyB))]
                    public interface IProxyB : Orleans.IGrain { }

                    public interface ICustomGrain : {{firstInterface}}, {{secondInterface}}
                    {
                        CustomCall Call();
                    }
                    """;

        var (diagnostics, _) = await this.GetDiagnosticsAsync(code, []);

        Assert.Equal(expectDiagnostic, diagnostics.Any(static diagnostic =>
            diagnostic.Id == GrainInterfaceMethodReturnTypeDiagnosticAnalyzer.DiagnosticId));
    }

    [Fact]
    public async Task RegisteredCustomReturnTypeMustSupportEveryProxyBase()
    {
        var code = """
                    [Orleans.InvokableBaseType(
                        typeof(Orleans.Runtime.GrainReference),
                        typeof(CustomCall<>),
                        typeof(CustomRequest<>))]
                    public class CustomCall<T> { }

                    public abstract class CustomRequest<T> { }
                    public abstract class CustomProxy { }

                    [Orleans.GenerateMethodSerializers(typeof(CustomProxy))]
                    public interface I : Orleans.IGrain
                    {
                        CustomCall<int> MyMethod();
                    }
                    """;

        var (diagnostics, _) = await this.GetDiagnosticsAsync(code, new string[0]);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticId, diagnostic.Id);
        Assert.Equal(MessageFormat, diagnostic.GetMessage());
    }
}
