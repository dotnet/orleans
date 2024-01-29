using Microsoft.CodeAnalysis;
using Orleans.Analyzers;
using Xunit;

namespace Analyzers.Tests;

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

    [Fact]
    public Task ClassWithoutAliasAttribute_AndWithGenerateSerializerAttribute_ShouldTriggerDiagnostic()
        => VerifyHasDiagnostic("[GenerateSerializer] public class C {}");

    [Fact]
    public Task StructWithoutAliasAttribute_AndWithGenerateSerializerAttribute_ShouldTriggerDiagnostic()
       => VerifyHasDiagnostic("[GenerateSerializer] public struct S {}");

    [Fact]
    public Task RecordClassWithoutAliasAttribute_AndWithGenerateSerializerAttribute_ShouldTriggerDiagnostic()
       => VerifyHasDiagnostic("[GenerateSerializer] public record R {}");

    [Fact]
    public Task RecordStructWithoutAliasAttribute_AndWithGenerateSerializerAttribute_ShouldTriggerDiagnostic()
       => VerifyHasDiagnostic("[GenerateSerializer] public record struct RS {}");

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
