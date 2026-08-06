using Microsoft.CodeAnalysis;
using Orleans.Analyzers;
using Xunit;

namespace Analyzers.Tests;

/// <summary>
/// Tests for the AliasClashAttributeAnalyzer which ensures that alias attributes don't have conflicting values.
/// Orleans uses aliases for type identification in serialization and RPC, so duplicate aliases would cause 
/// runtime conflicts. This analyzer catches these issues at compile time.
/// </summary>
[TestCategory("BVT"), TestCategory("Analyzer")]
public class AliasClashAttributeAnalyzerTest : DiagnosticAnalyzerTestBase<AliasClashAttributeAnalyzer>
{
    private async Task VerifyHasDiagnostic(string code)
    {
        var (diagnostics, _) = await GetDiagnosticsAsync(code, Array.Empty<string>());

        Assert.NotEmpty(diagnostics);
        var diagnostic = diagnostics.First();

        Assert.Equal(AliasClashAttributeAnalyzer.RuleId, diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    private async Task VerifyHasNoDiagnostic(string code)
    {
        var (diagnostics, _) = await GetDiagnosticsAsync(code, Array.Empty<string>());
        Assert.Empty(diagnostics);
    }

    /// <summary>
    /// Verifies that the analyzer detects when two enums have the same alias attribute value.
    /// </summary>
    [Fact]
    public Task Enum_ShouldTriggerDiagnostic()
    {
        var code = """
            [Alias("Enum")]
            public enum EA { V }

            [Alias("Enum")]
            public enum EB { V }
            """;

        return VerifyHasDiagnostic(code);
    }

    /// <summary>
    /// Verifies that the analyzer detects when two records have the same alias attribute value.
    /// </summary>
    [Fact]
    public Task Record_ShouldTriggerDiagnostic()
    {
        var code = """
            [Alias("Record")]
            public record RA(string P);

            [Alias("Record")]
            public record RB(string P);
            """;

        return VerifyHasDiagnostic(code);
    }

    /// <summary>
    /// Verifies that the analyzer detects when two record structs have the same alias attribute value.
    /// </summary>
    [Fact]
    public Task RecordStruct_ShouldTriggerDiagnostic()
    {
        var code = """
            [Alias("RecordStruct")]
            public record struct RSA(string P);

            [Alias("RecordStruct")]
            public record struct RSB(string P);
            """;

        return VerifyHasDiagnostic(code);
    }

    /// <summary>
    /// Verifies that the analyzer detects when two classes have the same alias attribute value.
    /// </summary>
    [Fact]
    public Task Class_ShouldTriggerDiagnostic()
    {
        var code = """
            [Alias("Class")]
            public class CA
            {
                [Id(0)] public string P { get; set; }
            }

            [Alias("Class")]
            public class CB
            {
                [Id(0)] public string P { get; set; }
            }
            
            """;

        return VerifyHasDiagnostic(code);
    }

    /// <summary>
    /// Verifies that the analyzer detects when two structs have the same alias attribute value.
    /// </summary>
    [Fact]
    public Task Struct_ShouldTriggerDiagnostic()
    {
        var code = """
            [Alias("Struct")]
            public struct SA
            {
                public SA() { }

                [Id(0)] public string P { get; set; }
            }

            [Alias("Struct")]
            public struct SB
            {
                public SB() { }

                [Id(0)] public string P { get; set; }
            }
            """;

        return VerifyHasDiagnostic(code);
    }

    /// <summary>
    /// Verifies that the analyzer detects when two grain interfaces have the same alias attribute value.
    /// Tests various grain interface types (IGrain, IGrainWithIntegerKey, etc.).
    /// </summary>
    [Theory]
    [MemberData(nameof(GrainInterfaces))]
    public Task GrainInterface_ShouldTriggerDiagnostic(string grainInterface)
    {
        var code = $$"""
            [Alias("Interface")]
            public interface IA : {{grainInterface}} {}

            [Alias("Interface")]
            public interface IB : {{grainInterface}} {}
            """;

        return VerifyHasDiagnostic(code);
    }

    /// <summary>
    /// Verifies that the analyzer does NOT trigger for non-grain interfaces with the same alias,
    /// as aliases are only meaningful for grain interfaces in Orleans.
    /// </summary>
    [Fact]
    public Task NonGrainInterface_ShouldNotTriggerDiagnostic()
    {
        var code = $$"""
            [Alias("Interface")]
            public interface IA {}

            [Alias("Interface")]
            public interface IB {}
            """;

        return VerifyHasNoDiagnostic(code);
    }

    /// <summary>
    /// Verifies that the analyzer detects when two methods in the same grain interface have the same alias.
    /// Method aliases must be unique within an interface for proper RPC dispatch.
    /// </summary>
    [Theory]
    [MemberData(nameof(GrainInterfaces))]
    public Task GrainInterfaceMethod_ShouldTriggerDiagnostic(string grainInterface)
    {
        var code = $$"""
            public interface I : {{grainInterface}}
            {
                [Alias("Void")] Task Void(int a);
                [Alias("Void")] Task Void(long a);
            }
            """;

        return VerifyHasDiagnostic(code);
    }

    /// <summary>
    /// Verifies that the analyzer allows different aliases for different methods in the same grain interface.
    /// </summary>
    [Theory]
    [MemberData(nameof(GrainInterfaces))]
    public Task GrainInterfaceMethod_ShouldNotTriggerDiagnostic(string grainInterface)
    {
        var code = $$"""
            public interface I : {{grainInterface}}
            {
                [Alias("Void")] Task Void(int a);
                [Alias("Void1")] Task Void(long a);
            }
            """;

        return VerifyHasNoDiagnostic(code);
    }

    /// <summary>
    /// Verifies that the analyzer allows the same method alias in different grain interfaces,
    /// as method aliases only need to be unique within each interface.
    /// </summary>
    [Theory]
    [MemberData(nameof(GrainInterfaces))]
    public Task DifferentGrainInterfaceMethod_ShouldNotTriggerDiagnostic(string grainInterface)
    {
        var code = $$"""
            public interface I1 : {{grainInterface}}
            {
                [Alias("Void")] Task Void(string a);
            }
                    
            public interface I2
            {
                [Alias("Void")] Task Void(string a);
            }
            """;

        return VerifyHasNoDiagnostic(code);
    }
}
