using Microsoft.CodeAnalysis;
using Orleans.Analyzers;
using Xunit;

namespace Analyzers.Tests;

[TestCategory("BVT"), TestCategory("Analyzer")]
public class GrainInterfacePropertyDiagnosticAnalyzerTest : DiagnosticAnalyzerTestBase<GrainInterfacePropertyDiagnosticAnalyzer>
{
    private const string DiagnosticId = GrainInterfacePropertyDiagnosticAnalyzer.DiagnosticId;
    private const string MessageFormat = GrainInterfacePropertyDiagnosticAnalyzer.MessageFormat;

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
