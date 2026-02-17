using Microsoft.CodeAnalysis;
using Orleans.Analyzers;
using Xunit;

namespace Analyzers.Tests;

/// <summary>
/// Tests for the analyzer that prevents properties in grain interfaces.
/// Orleans grain interfaces cannot have properties because grain calls are RPC-based
/// and properties would create ambiguity between local state and remote calls.
/// All grain interaction must be through explicit method calls to maintain clarity
/// about distributed communication boundaries.
/// </summary>
[TestCategory("BVT"), TestCategory("Analyzer")]
public class GrainInterfacePropertyDiagnosticAnalyzerTest : DiagnosticAnalyzerTestBase<GrainInterfacePropertyDiagnosticAnalyzer>
{
    private const string DiagnosticId = GrainInterfacePropertyDiagnosticAnalyzer.DiagnosticId;
    private const string MessageFormat = GrainInterfacePropertyDiagnosticAnalyzer.MessageFormat;

    /// <summary>
    /// Verifies that grain interfaces with only methods (no properties) pass validation.
    /// This is the correct pattern for Orleans grain interfaces.
    /// </summary>
    [Fact]
    public async Task GrainInterfacePropertyNoError()
    {
        var code = """
                    public interface I : Orleans.IGrain
                    {
                        Task GetSomeOtherThing(int a);
                    }
                    """;

        var (diagnostics, _) = await this.GetDiagnosticsAsync(code, new string[0]);
        Assert.Empty(diagnostics);
    }

    /// <summary>
    /// Verifies that the analyzer detects properties in IGrain interfaces.
    /// Properties are not allowed because they would obscure the distributed nature
    /// of grain calls and could lead to confusion about local vs. remote state.
    /// </summary>
    [Fact]
    public async Task NoPropertiesAllowedInGrainInterface()
    {
        var code = """
                    public interface I : Orleans.IGrain
                    {
                        int MyProperty { get; set; }
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
    /// Verifies that the analyzer detects properties in IAddressable interfaces.
    /// IAddressable is the base interface for grains and has the same restrictions
    /// regarding properties to maintain consistency in the programming model.
    /// </summary>
    [Fact]
    public async Task NoPropertiesAllowedInIAddressableInterface()
    {
        var code = """
                    public interface I : Orleans.Runtime.IAddressable
                    {
                        int MyProperty { get; set; }
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
    /// Verifies that the analyzer detects properties in IGrainObserver interfaces.
    /// Grain observers are used for callbacks and notifications, and like grains,
    /// they must use explicit methods rather than properties.
    /// </summary>
    [Fact]
    public async Task NoPropertiesAllowedInIGrainObserverInterface()
    {
        var code = """
                    public interface I : Orleans.IGrainObserver
                    {
                        int MyProperty { get; set; }
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
    /// Verifies that static properties are allowed in grain interfaces.
    /// Static members don't participate in grain invocation and can be used
    /// for interface-level constants or utility functions.
    /// </summary>
    [Fact]
    public async Task StaticInterfacePropertiesAllowedInGrainInterface()
    {
        var code = """
                    public interface I : Orleans.IGrain
                    {
                        public static int MyProperty => 0;
                        public static virtual int MyVirtualProperty => 0;
                    }
                    """;

        var (diagnostics, _) = await this.GetDiagnosticsAsync(code, new string[0]);
        Assert.Empty(diagnostics);
    }

    /// <summary>
    /// Verifies that static properties are allowed in IAddressable interfaces.
    /// Static members don't participate in grain invocation and can be used
    /// for interface-level constants or utility functions.
    /// </summary>
    [Fact]
    public async Task StaticInterfacePropertiesAllowedInAddressableInterface()
    {
        var code = """
                    public interface I : Orleans.Runtime.IAddressable
                    {
                        public static int MyProperty => 0;
                        public static virtual int MyVirtualProperty => 0;
                    }
                    """;

        var (diagnostics, _) = await this.GetDiagnosticsAsync(code, new string[0]);
        Assert.Empty(diagnostics);
    }
}
