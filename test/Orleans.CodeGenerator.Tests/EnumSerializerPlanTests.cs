using System.Collections.Immutable;
using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.Model;

namespace Orleans.CodeGenerator.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("CodeGen")]
public sealed class EnumSerializerPlanTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("byte", "ByteCodec")]
    [InlineData("sbyte", "SByteCodec")]
    [InlineData("short", "Int16Codec")]
    [InlineData("ushort", "UInt16Codec")]
    [InlineData("int", "Int32Codec")]
    [InlineData("uint", "UInt32Codec")]
    [InlineData("long", "Int64Codec")]
    [InlineData("ulong", "UInt64Codec")]
    public async Task IntegralCodecs_MatchCompilationAwareEmission(string underlyingType, string codec)
    {
        var compilation = await TestCompilationHelper.CreateCompilation($$"""
            namespace Example;
            [Orleans.GenerateSerializer]
            public enum @enum : {{underlyingType}} { Zero, One }
            """);
        var (_, result) = Run(CreateDriver(), compilation);
        var entry = Assert.Single(GetSerializerEntries(result));
        var legacy = Assert.Single(GenerateLegacy(compilation, "Example.enum"));
        Assert.Equal(legacy.SourceEntry, entry);
        var root = CSharpSyntaxTree.ParseText(entry.Source, cancellationToken: TestContext.Current.CancellationToken).GetRoot(TestContext.Current.CancellationToken);
        var write = Assert.Single(root.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => invocation.Expression.ToString().EndsWith($"{codec}.WriteField", StringComparison.Ordinal));
        var arguments = write.ArgumentList.Arguments;
        Assert.Equal(5, arguments.Count);
        Assert.Equal("ref writer", arguments[0].ToString());
        Assert.Equal("fieldIdDelta", arguments[1].ToString());
        Assert.Equal("expectedType", arguments[2].ToString());
        var cast = Assert.IsType<CastExpressionSyntax>(arguments[3].Expression);
        Assert.Equal(underlyingType, cast.Type.ToString());
        Assert.Equal("value", Assert.IsType<IdentifierNameSyntax>(cast.Expression).Identifier.ValueText);
        Assert.Equal("_codecFieldType", arguments[4].ToString());
        Assert.Contains($"{codec}.ReadValue(ref reader, field);", entry.Source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnrelatedBodyEdit_CachesEveryEmitterWithoutSourceConstruction()
    {
        const string source = """
            [Orleans.GenerateSerializer] public enum First { Zero, One }
            [Orleans.GenerateSerializer] internal enum Second : long { Minus = -1, One = 1 }
            public static class Unrelated { public static int Value() => 1; }
            """;
        var compilation = await TestCompilationHelper.CreateCompilation(source);
        var (driver, first) = Run(CreateDriver(), compilation);
        var plans = GetPlans(first);
        Assert.Equal(2, plans.Length);

        foreach (var plan in plans)
        {
            EnumSerializerGenerator.GenerateSource(plan);
        }

        var start = GC.GetAllocatedBytesForCurrentThread();
        foreach (var plan in plans)
        {
            EnumSerializerGenerator.GenerateSource(plan);
        }
        var sourceConstructionBytes = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.True(sourceConstructionBytes > 0);

        var updated = ReplaceSource(compilation, source.Replace("=> 1", "=> 2", StringComparison.Ordinal));
        start = GC.GetAllocatedBytesForCurrentThread();
        var (_, second) = Run(driver, updated);
        var retainedRunBytes = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.Equal(2, AssertCachedEmitters(second).Length);
        Assert.Equal(GetSources(first), GetSources(second));
        AssertFreshParity(updated, second);
        output.WriteLine($"Enum emitters: initial=2, retained-body-edit=0 (2 Cached, 0 Unchanged). " +
            $"Direct source construction/formatting for these plans allocated {sourceConstructionBytes} bytes; " +
            $"cached emission allocated 0 source-construction bytes. Retained driver plus output compilation validation allocated {retainedRunBytes} bytes.");
    }

    [Fact]
    public async Task UnderlyingTypeEdit_InvalidatesOnlyOwningEnum()
    {
        const string source = """
            [Orleans.GenerateSerializer] public enum First : int { One = 1 }
            [Orleans.GenerateSerializer] public enum Second { One = 1 }
            [Orleans.GenerateSerializer] public sealed class Holder { [Orleans.Id(17)] public First Value { get; set; } }
            """;
        var compilation = await TestCompilationHelper.CreateCompilation(source);
        var (driver, first) = Run(CreateDriver(), compilation);
        var updated = ReplaceSource(compilation, source.Replace("First : int", "First : long", StringComparison.Ordinal));
        var (_, second) = Run(driver, updated);
        var emitters = GetEmitterOutputs(second);
        Assert.Equal(2, emitters.Length);
        Assert.Equal(IncrementalStepRunReason.Modified, Assert.Single(emitters, static entry => entry.Entry.Source.Contains("Codec_First", StringComparison.Ordinal)).Reason);
        Assert.Equal(IncrementalStepRunReason.Cached, Assert.Single(emitters, static entry => entry.Entry.Source.Contains("Codec_Second", StringComparison.Ordinal)).Reason);
        var changed = Assert.Single(GetSerializerEntries(second), static entry => entry.Source.Contains("sealed class Codec_First", StringComparison.Ordinal));
        Assert.Contains("Int64Codec.WriteField", changed.Source, StringComparison.Ordinal);
        var cast = Assert.Single(CSharpSyntaxTree.ParseText(changed.Source, cancellationToken: TestContext.Current.CancellationToken)
            .GetRoot(TestContext.Current.CancellationToken).DescendantNodes().OfType<CastExpressionSyntax>(),
            static cast => cast.Expression is IdentifierNameSyntax { Identifier.ValueText: "value" });
        Assert.Equal("long", cast.Type.ToString());
        AssertFreshParity(updated, second);
        Assert.NotEqual(GetSources(first), GetSources(second));
    }

    [Fact]
    public async Task ConstantAliasAndOptionsEdits_PreserveCodecPlan()
    {
        const string source = """
            [Orleans.GenerateSerializer, Orleans.Alias("before")]
            public enum First { One = 1 }
            """;
        var compilation = await TestCompilationHelper.CreateCompilation(source);
        var (driver, first) = Run(CreateDriver(), compilation);
        var updated = ReplaceSource(compilation, source
            .Replace("One = 1", "One = 2, Three = 3", StringComparison.Ordinal)
            .Replace("\"before\"", "\"after\"", StringComparison.Ordinal));
        var options = TestCompilationHelper.CreateOptionsProvider(new Dictionary<string, string>
        {
            ["build_property.orleanshotreload"] = "true",
        });
        var (_, second) = Run(driver.WithUpdatedAnalyzerConfigOptions(options), updated);
        Assert.Single(AssertCachedEmitters(second));
        Assert.Equal(GetSerializerEntries(first), GetSerializerEntries(second));
        Assert.Contains("\"after\"", string.Join("\n", GetSources(second)), StringComparison.Ordinal);
        AssertFreshParity(updated, second, options);
    }

    [Fact]
    public async Task ImplicitFieldIdOption_PreservesDiagnosticAndRecovers()
    {
        var compilation = await TestCompilationHelper.CreateCompilation("[Orleans.GenerateSerializer] public enum First { One }");
        var (driver, first) = Run(CreateDriver(), compilation);
        var options = TestCompilationHelper.CreateOptionsProvider(new Dictionary<string, string>
        {
            ["build_property.orleans_generatefieldids"] = "PublicProperties",
        });
        driver = driver.WithUpdatedAnalyzerConfigOptions(options).RunGenerators(compilation, TestContext.Current.CancellationToken);
        var diagnostic = Assert.Single(driver.GetRunResult().Diagnostics);
        Assert.Equal("ORLEANS0106", diagnostic.Id);
        Assert.Same(compilation.SyntaxTrees.Single(), diagnostic.Location.SourceTree);
        Assert.Empty(GetSerializerEntries(Assert.Single(driver.GetRunResult().Results)));
        var (_, recovered) = Run(driver.WithUpdatedAnalyzerConfigOptions(TestCompilationHelper.CreateOptionsProvider()), compilation);
        Assert.Equal(GetSources(first), GetSources(recovered));
        AssertFreshParity(compilation, recovered);
    }

    [Fact]
    public async Task AccessibilityEdit_RebuildsOwnerAndPreservesLegacyOutput()
    {
        const string source = "[Orleans.GenerateSerializer] public enum First { One }";
        var compilation = await TestCompilationHelper.CreateCompilation(source);
        var (driver, _) = Run(CreateDriver(), compilation);
        var updated = ReplaceSource(compilation, source.Replace("public enum", "internal enum", StringComparison.Ordinal));
        var (_, second) = Run(driver, updated);
        Assert.Equal(IncrementalStepRunReason.Modified, Assert.Single(GetEmitterOutputs(second)).Reason);
        Assert.Contains("internal sealed class Codec_First", Assert.Single(GetSerializerEntries(second)).Source, StringComparison.Ordinal);
        Assert.Equal(Assert.Single(GenerateLegacy(updated, "First")).SourceEntry, Assert.Single(GetSerializerEntries(second)));
        AssertFreshParity(updated, second);
    }

    [Fact]
    public async Task AddRemoveAndUndo_KeepCurrentOwners()
    {
        const string source = "[Orleans.GenerateSerializer] public enum First { One }";
        const string addition = "\n[Orleans.GenerateSerializer] public enum Second : byte { Two = 2 }";
        var compilation = await TestCompilationHelper.CreateCompilation(source);
        var (driver, original) = Run(CreateDriver(), compilation);
        var added = ReplaceSource(compilation, source + addition);
        var (addedDriver, addedResult) = Run(driver, added);
        Assert.Equal(2, GetEmitterOutputs(addedResult).Length);
        Assert.Equal(IncrementalStepRunReason.Cached, Assert.Single(GetEmitterOutputs(addedResult),
            static emitter => emitter.Entry.Source.Contains("sealed class Codec_First", StringComparison.Ordinal)).Reason);
        AssertFreshParity(added, addedResult);

        var (removedDriver, removedResult) = Run(addedDriver, compilation);
        Assert.Equal(GetSources(original), GetSources(removedResult));
        Assert.Single(GetSerializerEntries(removedResult));
        AssertFreshParity(compilation, removedResult);
        var (_, undoResult) = Run(removedDriver, added);
        Assert.Equal(IncrementalStepRunReason.New, Assert.Single(GetEmitterOutputs(undoResult),
            static emitter => emitter.Entry.Source.Contains("sealed class Codec_Second", StringComparison.Ordinal)).Reason);
        Assert.Equal(GetSources(addedResult), GetSources(undoResult));
        AssertFreshParity(added, undoResult);
    }

    [Fact]
    public async Task GenericContainingType_UsesCompilationAwareEmission()
    {
        var compilation = await TestCompilationHelper.CreateCompilation("""
            public class Outer<T> where T : class, new()
            {
                [Orleans.GenerateSerializer] public enum Nested : short { One }
            }
            """);
        var (_, result) = Run(CreateDriver(), compilation);
        Assert.False(result.TrackedSteps.ContainsKey(OrleansSerializationSourceGenerator.EnumSerializerPlansTrackingName));
        var entry = Assert.Single(GetSerializerEntries(result));
        Assert.Equal(Assert.Single(GenerateLegacy(compilation, "Outer`1+Nested")).SourceEntry, entry);
        Assert.Contains("where T : class, new()", entry.Source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Mutability")]
    [InlineData("Base")]
    [InlineData("Constructor")]
    public async Task DtoSemanticChanges_RefreshDtoAndKeepIndependentEnumCached(string change)
    {
        const string source = """
            [Orleans.GenerateSerializer] public enum Stable { One }
            [Orleans.Immutable] public sealed class MemberType { public int Value { get; set; } }
            [Orleans.GenerateSerializer] public class Parent { [Orleans.Id(7)] public int Value { get; set; } }
            [Orleans.GenerateSerializer] public class Payload
            {
                [Orleans.Id(19)] public MemberType Item { get; set; }
                public Payload() { }
            }
            """;
        var compilation = await TestCompilationHelper.CreateCompilation(source);
        var (driver, first) = Run(CreateDriver(), compilation);
        var edited = change switch
        {
            "Mutability" => source.Replace("[Orleans.Immutable]", "", StringComparison.Ordinal),
            "Base" => source.Replace("class Payload", "class Payload : Parent", StringComparison.Ordinal),
            "Constructor" => source.Replace("public Payload()", "private Payload()", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        var updated = ReplaceSource(compilation, edited);
        var (_, second) = Run(driver, updated);
        Assert.Single(AssertCachedEmitters(second));
        Assert.NotEqual(
            Assert.Single(GetSerializerEntries(first), static entry => entry.Source.Contains("Codec_Payload", StringComparison.Ordinal)).Source,
            Assert.Single(GetSerializerEntries(second), static entry => entry.Source.Contains("Codec_Payload", StringComparison.Ordinal)).Source);
        AssertFreshParity(updated, second);
    }

    [Fact]
    public async Task SameIdentityReferenceReplacement_RefreshesDependentDto()
    {
        const string referencedSource = "[Orleans.Immutable] public sealed class MemberType { public int Value { get; set; } }";
        var producer = await TestCompilationHelper.CreateCompilation(referencedSource, "Producer");
        var firstReference = EmitReference(producer);
        var secondReference = EmitReference(ReplaceSource(producer, referencedSource.Replace("[Orleans.Immutable]", "", StringComparison.Ordinal)));
        var compilation = await TestCompilationHelper.CreateCompilation("""
            [Orleans.GenerateSerializer] public enum Stable { One }
            [Orleans.GenerateSerializer] public sealed class Payload { [Orleans.Id(37)] public MemberType Item { get; set; } }
            """, "Consumer", firstReference);
        var (driver, first) = Run(CreateDriver(), compilation);
        var updated = compilation.ReplaceReference(firstReference, secondReference);
        var (_, second) = Run(driver, updated);
        Assert.Single(AssertCachedEmitters(second));
        Assert.NotEqual(
            Assert.Single(GetSerializerEntries(first), static entry => entry.Source.Contains("Codec_Payload", StringComparison.Ordinal)).Source,
            Assert.Single(GetSerializerEntries(second), static entry => entry.Source.Contains("Codec_Payload", StringComparison.Ordinal)).Source);
        AssertFreshParity(updated, second);
    }

    [Fact]
    public async Task RecreatedSyntaxTree_ReportsDiagnosticsFromCurrentCompilation()
    {
        const string source = "public class Outer { [Orleans.GenerateSerializer] private enum Hidden { One } }";
        var compilation = await TestCompilationHelper.CreateCompilation(source);
        GeneratorDriver driver = CreateDriver();
        driver = driver.RunGenerators(compilation, TestContext.Current.CancellationToken);
        var firstDiagnostic = Assert.Single(driver.GetRunResult().Diagnostics);
        var updated = ReplaceSource(compilation, source);
        driver = driver.RunGenerators(updated, TestContext.Current.CancellationToken);
        var diagnostic = Assert.Single(driver.GetRunResult().Diagnostics);
        Assert.Equal(firstDiagnostic.Id, diagnostic.Id);
        Assert.Equal(firstDiagnostic.Location.SourceSpan, diagnostic.Location.SourceSpan);
        Assert.Same(updated.SyntaxTrees.Single(), diagnostic.Location.SourceTree);
        Assert.NotSame(firstDiagnostic.Location.SourceTree, diagnostic.Location.SourceTree);
    }

    private static GeneratorDriver CreateDriver(Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptionsProvider? options = null)
        => CSharpGeneratorDriver.Create(
            generators: [new OrleansSerializationSourceGenerator().AsSourceGenerator()],
            optionsProvider: options ?? TestCompilationHelper.CreateOptionsProvider(),
            driverOptions: new GeneratorDriverOptions(default, trackIncrementalGeneratorSteps: true));

    private static (GeneratorDriver Driver, GeneratorRunResult Result) Run(GeneratorDriver driver, CSharpCompilation compilation)
    {
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out var diagnostics, TestContext.Current.CancellationToken);
        Assert.Empty(diagnostics);
        var result = Assert.Single(driver.GetRunResult().Results);
        var errors = updated.GetDiagnostics(TestContext.Current.CancellationToken).Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length > 0)
        {
            var steps = result.TrackedSteps.Where(static pair => pair.Key.Contains("EnumSerializer", StringComparison.Ordinal))
                .SelectMany(static pair => pair.Value.SelectMany(step => step.Outputs.Select(value => $"{pair.Key}: {value.Reason} {value.Value}")));
            Assert.Fail($"{string.Join(Environment.NewLine, errors.Select(static error => error.ToString()))}{Environment.NewLine}{string.Join(Environment.NewLine, steps)}");
        }
        return (driver, result);
    }

    private static void AssertFreshParity(
        CSharpCompilation compilation,
        GeneratorRunResult retained,
        Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptionsProvider? options = null)
    {
        var (_, fresh) = Run(CreateDriver(options), compilation);
        Assert.Equal(GetSources(fresh), GetSources(retained));
    }

    private static CSharpCompilation ReplaceSource(CSharpCompilation compilation, string source)
        => compilation.ReplaceSyntaxTree(compilation.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(source, cancellationToken: TestContext.Current.CancellationToken));

    private static string[] GetSources(GeneratorRunResult result)
        => result.GeneratedSources.OrderBy(static source => source.HintName, StringComparer.Ordinal)
            .Select(static source => $"{source.HintName}\n{source.SourceText}").ToArray();

    private static GeneratedSourceEntry[] GetSerializerEntries(GeneratorRunResult result)
        => result.GeneratedSources.Where(static source => source.HintName.Contains(".orleans.ser.", StringComparison.Ordinal))
            .OrderBy(static source => source.HintName, StringComparer.Ordinal)
            .Select(static source => new GeneratedSourceEntry(source.HintName, source.SourceText.ToString())).ToArray();

    private static EnumSerializerOutputPlan[] GetPlans(GeneratorRunResult result)
        => result.TrackedSteps[OrleansSerializationSourceGenerator.EnumSerializerPlansTrackingName]
            .SelectMany(static step => step.Outputs).Select(static output => (EnumSerializerOutputPlan)output.Value).ToArray();

    private static (GeneratedSourceEntry Entry, IncrementalStepRunReason Reason)[] GetEmitterOutputs(GeneratorRunResult result)
        => result.TrackedSteps[OrleansSerializationSourceGenerator.EnumSerializerOutputsTrackingName]
            .SelectMany(static step => step.Outputs)
            .Where(static output => output.Reason != IncrementalStepRunReason.Removed)
            .Select(static output => (((SourceOutputResult)output.Value).SourceEntry!.Value, output.Reason)).ToArray();

    private static GeneratedSourceEntry[] AssertCachedEmitters(GeneratorRunResult result)
    {
        var emitters = GetEmitterOutputs(result);
        Assert.NotEmpty(emitters);
        Assert.All(emitters, static emitter => Assert.Equal(IncrementalStepRunReason.Cached, emitter.Reason));
        return emitters.Select(static emitter => emitter.Entry).ToArray();
    }

    private static ImmutableArray<SourceOutputResult> GenerateLegacy(CSharpCompilation compilation, string metadataName)
    {
        var symbol = compilation.GetTypeByMetadataName(metadataName)!;
        var options = new CodeGeneratorOptions();
        var description = SerializableSourceOutputGenerator.CreateSerializableTypeDescription(
            compilation, LibraryTypes.FromCompilation(compilation, options), options, symbol)!;
        var model = SerializableTypeModelExtractor.ExtractSerializableTypeModel(description);
        return SerializableSourceOutputGenerator.CreateSerializableSourceOutputs(compilation, [model], default, TestContext.Current.CancellationToken);
    }

    private static MetadataReference EmitReference(CSharpCompilation compilation)
    {
        using var image = new MemoryStream();
        var result = compilation.Emit(image, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return MetadataReference.CreateFromImage(image.ToArray());
    }
}
