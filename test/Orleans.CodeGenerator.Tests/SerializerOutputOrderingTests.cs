using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Orleans.CodeGenerator.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("CodeGen")]
public sealed class SerializerOutputOrderingTests
{
    private const string SerializableClass = "[Orleans.GenerateSerializer] public sealed class Zeta { [Orleans.Id(0)] public int Value { get; set; } }";
    private const string Enum = "[Orleans.GenerateSerializer] public enum Alpha { One }";
    private const string Fallback = "public class Outer<T> { [Orleans.GenerateSerializer] public enum Nested { One } }";

    [Fact]
    public async Task SerializableClassEnumAndFallbackOutputs_UseCanonicalOwnerOrderAfterDeclarationReordering()
    {
        var compilation = await TestCompilationHelper.CreateCompilation($"{SerializableClass}\n{Enum}\n{Fallback}");
        var (driver, first) = Run(CreateDriver(), compilation);
        Assert.Equal(["Codec_Alpha", "Codec_Nested", "Codec_Zeta"], GetCodecNames(first));

        var reordered = compilation.ReplaceSyntaxTree(compilation.SyntaxTrees.Single(),
            CSharpSyntaxTree.ParseText($"{Fallback}\n{Enum}\n{SerializableClass}", cancellationToken: TestContext.Current.CancellationToken));
        var (_, retained) = Run(driver, reordered);
        var (_, fresh) = Run(CreateDriver(), reordered);
        Assert.Equal(GetCodecNames(first), GetCodecNames(retained));
        Assert.Equal(GetOutputs(fresh), GetOutputs(retained));
    }

    [Fact]
    public async Task CollidingHints_KeepCanonicalOwnerAndSuffixesAcrossEmissionStreamOrder()
    {
        var compilation = await TestCompilationHelper.CreateCompilation($"{SerializableClass}\n{Enum}\n{Fallback}");
        var (_, result) = Run(CreateDriver(), compilation);
        const string hint = "forced.orleans.ser.collision.g.cs";
        var colliding = GetOutputs(result).Select(output => SourceOutputResult.FromSource(
            new GeneratedSourceEntry(hint, output.SourceEntry!.Value.Source), output.SerializableIdentity)).ToImmutableArray();
        Assert.All(colliding, static output => Assert.NotNull(output.SerializableIdentity));
        var first = GeneratedSourceOutput.DeduplicateSerializableSourceOutputs(colliding);
        var reordered = GeneratedSourceOutput.DeduplicateSerializableSourceOutputs([.. colliding.Reverse()]);
        Assert.Equal(3, first.Length);
        Assert.Equal(first, reordered);
        Assert.Equal("Alpha", Assert.Single(first, static output => output.SourceEntry!.Value.HintName == hint)
            .SerializableIdentity!.MetadataIdentity.MetadataName);
        Assert.Equal(3, first.Select(static output => output.SourceEntry!.Value.HintName).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(colliding.Select(static output => output.SourceEntry!.Value.Source).Order(StringComparer.Ordinal),
            first.Select(static output => output.SourceEntry!.Value.Source).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ActualHashCollision_KeepsGeneratedFileNamesAfterDeclarationReordering()
    {
        const string tenParameters = """
            [Orleans.GenerateSerializer]
            public sealed class Collision<A, B, C_D, E_F_G, H_I, J_K, L_M_N_O, P_Q, R_S, T_U_V_W_X>
            { [Orleans.Id(3)] public int Value { get; set; } }
            """;
        const string elevenParameters = """
            [Orleans.GenerateSerializer]
            public sealed class Collision<A, B_C_D_E_F_G_H, I_J_K_L, M, N, O_P_Q, R_S, T, U, V_W, X>
            { [Orleans.Id(7)] public int Value { get; set; } }
            """;
        const string hint = "TestProject.orleans.ser.global_Collision_A_B_C_D_E_F_G_H_I_J_K_L_M_N_O_P_Q_R_S_T_U_V_W_X.647489D1.g.cs";
        var compilation = await TestCompilationHelper.CreateCompilation($"{elevenParameters}\n{tenParameters}");
        var (driver, first) = Run(CreateDriver(), compilation);
        var rawOutputs = first.TrackedSteps[OrleansSerializationSourceGenerator.SerializableTypeOutputsTrackingName]
            .SelectMany(static step => step.Outputs).Select(static output => output.Value).OfType<SourceOutputResult>().ToArray();
        Assert.Equal(2, rawOutputs.Length);
        Assert.All(rawOutputs, output => Assert.Equal(hint, output.SourceEntry!.Value.HintName));
        Assert.Equal("Collision`10", Assert.Single(GetOutputs(first), static output => output.SourceEntry!.Value.HintName == hint)
            .SerializableIdentity!.MetadataIdentity.MetadataName);
        Assert.Equal(2, GetOutputs(first).Select(static output => output.SourceEntry!.Value.HintName).Distinct(StringComparer.Ordinal).Count());

        var reordered = compilation.ReplaceSyntaxTree(compilation.SyntaxTrees.Single(),
            CSharpSyntaxTree.ParseText($"{tenParameters}\n{elevenParameters}", cancellationToken: TestContext.Current.CancellationToken));
        var (_, retained) = Run(driver, reordered);
        var (_, fresh) = Run(CreateDriver(), reordered);
        Assert.Equal(GetOutputs(first), GetOutputs(retained));
        Assert.Equal(GetOutputs(fresh), GetOutputs(retained));
    }

    private static GeneratorDriver CreateDriver()
        => CSharpGeneratorDriver.Create(
            generators: [new OrleansSerializationSourceGenerator().AsSourceGenerator()],
            optionsProvider: TestCompilationHelper.CreateOptionsProvider(),
            driverOptions: new GeneratorDriverOptions(default, trackIncrementalGeneratorSteps: true));

    private static (GeneratorDriver Driver, GeneratorRunResult Result) Run(GeneratorDriver driver, CSharpCompilation compilation)
    {
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics, TestContext.Current.CancellationToken);
        Assert.Empty(diagnostics);
        Assert.Empty(output.GetDiagnostics(TestContext.Current.CancellationToken).Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        return (driver, Assert.Single(driver.GetRunResult().Results));
    }

    private static ImmutableArray<SourceOutputResult> GetOutputs(GeneratorRunResult result)
        => (ImmutableArray<SourceOutputResult>)Assert.Single(Assert.Single(
            result.TrackedSteps[OrleansSerializationSourceGenerator.SerializerOutputsTrackingName]).Outputs).Value;

    private static string[] GetCodecNames(GeneratorRunResult result)
        => GetOutputs(result).Select(static output => CSharpSyntaxTree.ParseText(output.SourceEntry!.Value.Source,
                cancellationToken: TestContext.Current.CancellationToken).GetRoot(TestContext.Current.CancellationToken)
            .DescendantNodes().OfType<ClassDeclarationSyntax>().First(static type => type.Identifier.ValueText.StartsWith("Codec_", StringComparison.Ordinal))
            .Identifier.ValueText).ToArray();
}
