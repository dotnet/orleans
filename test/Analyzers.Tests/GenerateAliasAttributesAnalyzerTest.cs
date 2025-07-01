using Microsoft.CodeAnalysis;
using Orleans.Analyzers;
using Xunit;

namespace Analyzers.Tests;

/// <summary>
/// Tests for the GenerateAliasAttributesAnalyzer which suggests adding [Alias] attributes to types and methods
/// that need them. Orleans uses aliases for stable type identification across versions and deployments.
/// This analyzer helps developers remember to add aliases to grain interfaces, serializable types, and RPC methods.
/// </summary>
[TestCategory("BVT"), TestCategory("Analyzer")]
public class GenerateAliasAttributesAnalyzerTest : DiagnosticAnalyzerTestBase<GenerateAliasAttributesAnalyzer>
{
    private async Task VerifyHasDiagnostic(string code, int diagnosticsCount = 1)
    {
        var (diagnostics, _) = await GetDiagnosticsAsync(code, Array.Empty<string>());

        Assert.NotEmpty(diagnostics);
        Assert.Equal(diagnosticsCount, diagnostics.Length);

        var diagnostic = diagnostics.First();

        Assert.Equal(GenerateAliasAttributesAnalyzer.RuleId, diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
    }

    private async Task VerifyHasNoDiagnostic(string code)
    {
        var (diagnostics, _) = await GetDiagnosticsAsync(code, Array.Empty<string>());
        Assert.Empty(diagnostics);
    }

    #region Interfaces & Methods

    /// <summary>
    /// Verifies that the analyzer suggests adding [Alias] attributes to grain interfaces and their methods
    /// when they don't have them. Each interface and non-static method should have an alias for proper RPC routing.
    /// </summary>
    [Theory]
    [MemberData(nameof(GrainInterfaces))]
    public Task GrainInterfaceWithoutAliasAttribute_ShouldTriggerDiagnostic(string grainInterface)
    {
        var code = $$"""
                    public interface I : {{grainInterface}}
                    {
                        Task<int> M1();
                        Task<int> M2();

                        static Task<int> M3() => Task.FromResult(0);
                    }
                    """;

        return VerifyHasDiagnostic(code, 3);  // 3 diagnostics, because 1 for interface, and 2 for the non-static methods
    }

    /// <summary>
    /// Verifies that the analyzer does not trigger when grain interfaces and their methods already have [Alias] attributes.
    /// </summary>
    [Theory]
    [MemberData(nameof(GrainInterfaces))]
    public Task GrainInterfaceWithAliasAttribute_ShouldNotTriggerDiagnostic(string grainInterface)
    {
        var code = $$"""
                    [Alias("I")]
                    public interface I : {{grainInterface}}
                    {
                        [Alias("M1")] Task<int> M1();
                        [Alias("M2")] Task<int> M2();

                        static Task<int> M3() => Task.FromResult(0);
                    }
                    """;

        return VerifyHasNoDiagnostic(code);
    }

    /// <summary>
    /// Verifies that the analyzer does not suggest aliases for non-grain interfaces,
    /// as aliases are only needed for grain interfaces in the Orleans RPC system.
    /// </summary>
    [Fact]
    public Task NonGrainInterfaceWithoutAliasAttribute_ShouldNotTriggerDiagnostic()
    {
        var code = """
                    public interface I
                    {
                        Task<int> M1();
                        Task<int> M2();

                        static Task<int> M3() => Task.FromResult(0);
                    }
                    """;

        return VerifyHasNoDiagnostic(code);
    }

    #endregion

    #region Classes, Structs, Records

    /// <summary>
    /// Verifies that the analyzer suggests adding [Alias] to classes marked with [GenerateSerializer].
    /// Serializable types need aliases for version-tolerant serialization.
    /// </summary>
    [Fact]
    public Task ClassWithoutAliasAttribute_AndWithGenerateSerializerAttribute_ShouldTriggerDiagnostic()
        => VerifyHasDiagnostic("[GenerateSerializer] public class C {}");

    /// <summary>
    /// Verifies that the analyzer suggests adding [Alias] to structs marked with [GenerateSerializer].
    /// </summary>
    [Fact]
    public Task StructWithoutAliasAttribute_AndWithGenerateSerializerAttribute_ShouldTriggerDiagnostic()
       => VerifyHasDiagnostic("[GenerateSerializer] public struct S {}");

    /// <summary>
    /// Verifies that the analyzer suggests adding [Alias] to record classes marked with [GenerateSerializer].
    /// </summary>
    [Fact]
    public Task RecordClassWithoutAliasAttribute_AndWithGenerateSerializerAttribute_ShouldTriggerDiagnostic()
       => VerifyHasDiagnostic("[GenerateSerializer] public record R {}");

    /// <summary>
    /// Verifies that the analyzer suggests adding [Alias] to record structs marked with [GenerateSerializer].
    /// </summary>
    [Fact]
    public Task RecordStructWithoutAliasAttribute_AndWithGenerateSerializerAttribute_ShouldTriggerDiagnostic()
       => VerifyHasDiagnostic("[GenerateSerializer] public record struct RS {}");

    /// <summary>
    /// Verifies that the analyzer does not trigger when a class with [GenerateSerializer] already has an [Alias].
    /// </summary>
    [Fact]
    public Task ClassWithAliasAttribute_AndWithGenerateSerializerAttribute_ShouldNotTriggerDiagnostic()
        => VerifyHasNoDiagnostic("[GenerateSerializer, Alias(\"C\")] public class C {}");

    [Fact]
    public Task StructWithAliasAttribute_AndWithGenerateSerializerAttribute_ShouldNotTriggerDiagnostic()
       => VerifyHasNoDiagnostic("[GenerateSerializer, Alias(\"S\")] public struct S {}");

    [Fact]
    public Task RecordClassWithAliasAttribute_AndWithGenerateSerializerAttribute_ShouldNotTriggerDiagnostic()
       => VerifyHasNoDiagnostic("[GenerateSerializer, Alias(\"R\")] public record R {}");

    [Fact]
    public Task RecordStructWithAliasAttribute_AndWithGenerateSerializerAttribute_ShouldNotTriggerDiagnostic()
       => VerifyHasNoDiagnostic("[GenerateSerializer, Alias(\"RS\")] public record struct RS {}");

    /// <summary>
    /// Verifies that the analyzer does not suggest aliases for classes without [GenerateSerializer],
    /// as only serializable types need aliases.
    /// </summary>
    [Fact]
    public Task ClassWithoutAliasAttribute_AndWithoutGenerateSerializerAttribute_ShouldNotTriggerDiagnostic()
        => VerifyHasNoDiagnostic("public class C {}");

    [Fact]
    public Task StructWithoutAliasAttribute_AndWithoutGenerateSerializerAttribute_ShouldNotTriggerDiagnostic()
       => VerifyHasNoDiagnostic("public struct S {}");

    [Fact]
    public Task RecordClassWithoutAliasAttribute_AndWithoutGenerateSerializerAttribute_ShouldNotTriggerDiagnostic()
       => VerifyHasNoDiagnostic("public record R {}");

    [Fact]
    public Task RecordStructWithoutAliasAttribute_AndWithoutGenerateSerializerAttribute_ShouldNotTriggerDiagnostic()
       => VerifyHasNoDiagnostic("public record struct RS {}");

    #endregion
}
