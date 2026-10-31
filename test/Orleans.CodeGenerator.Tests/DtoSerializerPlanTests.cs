using System.Collections;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Orleans.CodeGenerator.Model;

namespace Orleans.CodeGenerator.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("CodeGen")]
public sealed class DtoSerializerPlanTests(ITestOutputHelper output)
{
    private const string IndependentTypes = """
        [Orleans.GenerateSerializer]
        public sealed class Independent { [Orleans.Id(2)] public int Number { get; set; } }
        [Orleans.GenerateSerializer]
        public enum Stable { Zero, One }
        """;

    // Deliberately use the hole names as user identifiers, including within generic type arguments.
    // Compiling the result and checking the accesses catches textual substitution in member templates.
    private const string RichSource = """
        using System.Collections.Generic;
        namespace Example.instance.value.context;

        [Orleans.GenerateSerializer]
        public sealed class Item { [Orleans.Id(3)] public int Number { get; set; } }

        [Orleans.GenerateSerializer, Orleans.SerializationCallbacks(typeof(PayloadHooks))]
        public class Payload
        {
            [Orleans.Id(1)] private readonly Dictionary<string, List<Item[]>> _items = new();
            [Orleans.Id(5)] private readonly int _stamp;
            [Orleans.Id(11)] public Item value { get; set; } = new();
            [Orleans.Id(17)] public List<Item> context { get; set; } = new();
            [Orleans.Id(23)] public List<Item> instance { get; set; } = new();
        }

        public sealed class PayloadHooks
        {
            public void OnSerializing(ref Payload value) { }
            public void OnDeserialized(Payload value) { }
            public void OnCopying(Payload original, ref Payload copy) { }
        }

        [Orleans.GenerateSerializer]
        public struct ValuePayload
        {
            [Orleans.Id(4)] private readonly List<Item> _items;
            [Orleans.Id(9)] public int Count { get; set; }
            public ValuePayload(List<Item> items) { _items = items; Count = items.Count; }
        }

        [Orleans.GenerateSerializer]
        public record RecordPayload<T>(
            [property: Orleans.Id(7)] T Value,
            [property: Orleans.Id(19)] List<Item> Items) where T : class, new();

        [Orleans.GenerateSerializer]
        public record struct ValueRecord([property: Orleans.Id(13)] List<Item> Items);

        [Orleans.GenerateSerializer]
        public enum Stable { Zero, One }
        public static class Unrelated { public static int Value() => 1; }
        """;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ImmutableDtoWithRecursiveValueMember_GeneratesWithoutAnalyzingMemberCopyability(bool isSealed)
    {
        var compilation = await TestCompilationHelper.CreateCompilation($$"""
            [Orleans.GenerateSerializer, Orleans.Immutable]
            public {{(isSealed ? "sealed " : "")}}class Payload { [Orleans.Id(0)] public Node Value { get; set; } }
            public struct Node { public System.Collections.Immutable.ImmutableList<Node> Children; }
            """);
        var (_, result) = Run(CreateDriver(), compilation);
        var plan = Assert.Single(GetPlans(result));
        var source = Assert.Single(GetSerializerEntries(result)).Source;
        Assert.Contains("IFieldCodec<global::Node>", source, StringComparison.Ordinal);
        if (isSealed)
        {
            Assert.Null(plan.Copier);
            Assert.DoesNotContain("class Copier_Payload", source, StringComparison.Ordinal);
        }
        else
        {
            Assert.NotNull(plan.Copier);
            Assert.Contains("class Copier_Payload", source, StringComparison.Ordinal);
            Assert.Contains("output.Value = input.Value;", source, StringComparison.Ordinal);
        }
        AssertFreshParity(compilation, result);
    }

    [Theory]
    [InlineData("record")]
    [InlineData("union")]
    public async Task FSharpMappedSource_UsesCompilationAwareEmissionAndRefreshesMembers(string kind)
    {
        var flags = kind == "record" ? "RecordType" : "SumType";
        var member = kind == "record"
            ? "[Orleans.Id(7)] public int Value { get; set; }"
            : "public int Value;";
        var source = $$"""
            [Orleans.GenerateSerializer]
            [Microsoft.FSharp.Core.CompilationMapping(Microsoft.FSharp.Core.SourceConstructFlags.{{flags}})]
            public sealed class Payload { {{member}} }
            {{IndependentTypes}}
            """;
        var compilation = await TestCompilationHelper.CreateCompilation(source, additionalReferences:
            [MetadataReference.CreateFromFile(typeof(Microsoft.FSharp.Core.FSharpOption<>).Assembly.Location)]);
        var services = new GeneratorServices(compilation, new CodeGeneratorOptions());
        var description = SerializableSourceOutputGenerator.CreateSerializableTypeDescription(
            services, compilation.GetTypeByMetadataName("Payload")!);
        if (kind == "record")
        {
            Assert.IsType<FSharpUtilities.FSharpRecordTypeDescription>(description);
        }
        else
        {
            Assert.IsType<FSharpUtilities.FSharpUnionCaseTypeDescription>(description);
        }

        var (driver, original) = Run(CreateDriver(), compilation);
        Assert.Equal("Independent", Assert.Single(GetPlans(original)).MetadataIdentity.MetadataName);
        Assert.Contains("Int32Codec.WriteField", GetOwnerEntry(original, "Payload").Source, StringComparison.Ordinal);

        var updated = ReplaceSource(compilation, source.Replace("public int Value", "public long Value", StringComparison.Ordinal));
        var (_, retained) = Run(driver, updated);
        Assert.Equal("Independent", Assert.Single(GetPlans(retained)).MetadataIdentity.MetadataName);
        Assert.Single(AssertCachedEmitters(retained));
        AssertEnumCached(retained);
        Assert.Contains("Int64Codec.WriteField", GetOwnerEntry(retained, "Payload").Source, StringComparison.Ordinal);
        Assert.NotEqual(GetOwnerEntry(original, "Payload").Source, GetOwnerEntry(retained, "Payload").Source);
        AssertFreshParity(updated, retained);
    }

    [Fact]
    public async Task PrivateActivatorNamedField_UsesInjectedActivatorWithUnsafeAccessors()
    {
        var compilation = await TestCompilationHelper.CreateCompilation("""
            [Orleans.GenerateSerializer]
            public sealed class Payload
            {
                [Orleans.Id(3)] private int _activator;
                public Payload(int value) { _activator = value; }
            }
            """);
        var (_, result) = Run(CreateDriver(), compilation);
        var plan = Assert.Single(GetPlans(result));
        Assert.True(plan.Serializer.Type.UseActivator);
        Assert.Equal(2, plan.Serializer.Fields.Count(static field => field.FieldName == "_activator"));
        Assert.Equal(2, plan.Copier!.Fields.Count(static field => field.FieldName == "_activator"));
        Assert.Contains(plan.Serializer.Fields, static field => field.Kind == EmissionFieldKind.Accessor && field.FieldName == "_activator");
        Assert.Contains(plan.Serializer.Fields, static field => field.Kind == EmissionFieldKind.Service && field.FieldName == "_activator");
        Assert.Contains("_activator.Create()", Assert.Single(GetSerializerEntries(result)).Source, StringComparison.Ordinal);
        AssertFreshParity(compilation, result);
    }

    [Fact]
    public async Task UnrelatedBodyEdit_CachesEveryDtoEmitterWithoutRendering()
    {
        var compilation = await TestCompilationHelper.CreateCompilation(RichSource);
        var (driver, first) = Run(CreateDriver(), compilation);
        var plans = GetPlans(first);
        Assert.Equal(5, plans.Length);
        Assert.Equal(5, CountEmissionExecutions(first));
        Assert.All(GetEmitterOutputs(first), static emitter => Assert.Equal(IncrementalStepRunReason.New, emitter.Reason));
        AssertRichSourceFacts(first);

        foreach (var plan in plans)
        {
            // Warm up the direct renderer, outside the measurement.
            SerializableSourceOutputGenerator.GenerateDtoSource(plan);
        }

        var rendered = new SourceOutputResult[plans.Length];
        var start = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < plans.Length; index++)
        {
            rendered[index] = SerializableSourceOutputGenerator.GenerateDtoSource(plans[index]);
        }
        var renderingBytes = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.True(renderingBytes > 0);
        Assert.Equal(
            GetEmitterOutputs(first).Select(static emitter => emitter.Entry).OrderBy(static entry => entry.HintName, StringComparer.Ordinal),
            rendered.Select(static result => result.SourceEntry!.Value).OrderBy(static entry => entry.HintName, StringComparer.Ordinal));

        var updated = ReplaceSource(compilation, RichSource.Replace("=> 1", "=> 2", StringComparison.Ordinal));
        start = GC.GetAllocatedBytesForCurrentThread();
        driver = driver.RunGeneratorsAndUpdateCompilation(updated, out var generatedCompilation, out var diagnostics, TestContext.Current.CancellationToken);
        var retainedDriverBytes = GC.GetAllocatedBytesForCurrentThread() - start;
        var second = ValidateRun(driver, generatedCompilation, diagnostics);

        Assert.Equal(5, AssertCachedEmitters(second).Length);
        Assert.Equal(0, CountEmissionExecutions(second));
        AssertEnumCached(second);
        Assert.Equal(GetSources(first), GetSources(second));
        AssertFreshParity(updated, second);
        Assert.True(retainedDriverBytes > 0);
        output.WriteLine($"DTO emission executions: initial={CountEmissionExecutions(first)}, retained-body-edit={CountEmissionExecutions(second)} " +
            $"({GetEmitterOutputs(second).Length} Cached, 0 Unchanged). Direct GenerateDtoSource allocated {renderingBytes} current-thread bytes for {plans.Length} plans. " +
            $"Whole retained driver, including updating the output compilation but excluding diagnostic validation, allocated {retainedDriverBytes} current-thread bytes. " +
            "Cached DTO emitters do not write source; semantic planning still resolves text plans. " +
            "This is not a claim of zero total source work or zero whole-driver allocation.");
    }

    [Fact]
    public async Task EmissionCache_ReusesSourcePayloadAcrossRegistrationAndCompilerOutputs()
    {
        var compilation = await TestCompilationHelper.CreateCompilation(RichSource);
        var (driver, original) = Run(CreateDriver(), compilation);
        CheckPayloads(original);
        var updated = ReplaceSource(compilation, RichSource.Replace("=> 1", "=> 2", StringComparison.Ordinal));
        var (_, retained) = Run(driver, updated);
        Assert.Equal(5, AssertCachedEmitters(retained).Length);
        CheckPayloads(retained);
        foreach (var emitter in GetEmitterOutputs(retained))
        {
            var previous = Assert.Single(GetEmitterOutputs(original), item => item.Entry.HintName == emitter.Entry.HintName);
            Assert.Same(previous.Entry.Source, emitter.Entry.Source);
        }

        static void CheckPayloads(GeneratorRunResult result)
        {
            var registered = (ImmutableArray<SourceOutputResult>)Assert.Single(Assert.Single(
                result.TrackedSteps[OrleansSerializationSourceGenerator.SerializerOutputsTrackingName]).Outputs).Value;
            foreach (var emitter in GetEmitterOutputs(result))
            {
                var entry = Assert.Single(registered, item => item.SourceEntry?.HintName == emitter.Entry.HintName).SourceEntry!.Value;
                var compilerOutput = Assert.Single(result.GeneratedSources, item => item.HintName == emitter.Entry.HintName);
                Assert.Same(emitter.Entry.Source, entry.Source);
                Assert.Same(entry.Source, compilerOutput.SourceText.ToString());
            }
        }
    }

    [Theory]
    [InlineData("transitive-immutable")]
    [InlineData("transitive-field")]
    [InlineData("effective-base")]
    [InlineData("transparent-base")]
    [InlineData("constructor")]
    [InlineData("required")]
    [InlineData("init")]
    [InlineData("readonly")]
    [InlineData("accessor")]
    [InlineData("constraints")]
    [InlineData("hook-arity")]
    [InlineData("hook-ref")]
    [InlineData("field-id")]
    [InlineData("accessibility")]
    public async Task SemanticDependencyEdit_InvalidatesOwnerAndCachesIndependentDto(string change)
    {
        var (source, edited) = GetDependencySources(change);
        var compilation = await TestCompilationHelper.CreateCompilation(source + "\n" + IndependentTypes);
        var (driver, first) = Run(CreateDriver(), compilation);
        var updated = ReplaceSource(compilation, edited + "\n" + IndependentTypes);
        var (_, second) = Run(driver, updated);
        var before = GetOwnerEntry(first, "Payload").Source;
        var after = GetOwnerEntry(second, "Payload").Source;

        Assert.NotEqual(before, after);
        Assert.Equal(IncrementalStepRunReason.Modified, GetOwnerEmitter(second, "Payload").Reason);
        Assert.Equal(IncrementalStepRunReason.Cached, GetOwnerEmitter(second, "Independent").Reason);
        Assert.Equal(GetOwnerEntry(first, "Independent"), GetOwnerEntry(second, "Independent"));
        AssertEnumCached(second);
        AssertDependencySourceFacts(change, before, after);
        AssertFreshParity(updated, second);
    }

    [Theory]
    [InlineData("unsafe", "v7.0")]
    [InlineData("generic-unsafe", "v8.0")]
    [InlineData("volatile-unsafe", "v9.0")]
    public async Task UnsafeAccessorOptionEdit_InvalidatesOnlyOwnerNeedingThatCapability(string change, string targetVersion)
    {
        var declaration = change switch
        {
            "unsafe" => "public sealed class Payload { [Orleans.Id(5)] private readonly int _value; }",
            "generic-unsafe" => "public sealed class Payload<T> where T : class { [Orleans.Id(5)] private readonly T _value; }",
            "volatile-unsafe" => "public sealed class Payload { [Orleans.Id(5)] private volatile int _value; }",
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        var compilation = await TestCompilationHelper.CreateCompilation("[Orleans.GenerateSerializer] " + declaration + "\n" + IndependentTypes);
        var initialOptions = Options(("build_property.TargetFrameworkVersion", "v10.0"));
        var (driver, first) = Run(CreateDriver(initialOptions), compilation);
        var options = Options(("build_property.TargetFrameworkVersion", targetVersion));
        var (_, second) = Run(driver.WithUpdatedAnalyzerConfigOptions(options), compilation);
        var before = GetOwnerEntry(first, "Payload").Source;
        var after = GetOwnerEntry(second, "Payload").Source;

        Assert.Contains("UnsafeAccessorAttribute", before, StringComparison.Ordinal);
        Assert.DoesNotContain("FieldAccessor.Get", before, StringComparison.Ordinal);
        Assert.DoesNotContain("UnsafeAccessorAttribute", after, StringComparison.Ordinal);
        Assert.Contains("FieldAccessor.Get", after, StringComparison.Ordinal);
        Assert.NotEqual(before, after);
        Assert.Equal(IncrementalStepRunReason.Modified, GetOwnerEmitter(second, "Payload").Reason);
        Assert.Equal(IncrementalStepRunReason.Cached, GetOwnerEmitter(second, "Independent").Reason);
        Assert.Equal(GetOwnerEntry(first, "Independent"), GetOwnerEntry(second, "Independent"));
        AssertEnumCached(second);
        AssertFreshParity(compilation, second, options);
    }

    [Fact]
    public async Task HotReloadOption_ChangesDtoShapeThenCachesEveryEmitterOnUnrelatedEdit()
    {
        const string source = """
            [Orleans.GenerateSerializer]
            public sealed class Payload
            {
                [Orleans.Id(5)] private readonly System.Collections.Generic.List<int> _items = new();
            }
            public static class Unrelated { public static int Value() => 1; }
            """;
        var compilation = await TestCompilationHelper.CreateCompilation(source + "\n" + IndependentTypes);
        var (driver, first) = Run(CreateDriver(), compilation);
        var options = Options(("build_property.orleanshotreload", "true"));
        var (hotReloadDriver, second) = Run(driver.WithUpdatedAnalyzerConfigOptions(options), compilation);
        var before = GetOwnerEntry(first, "Payload").Source;
        var after = GetOwnerEntry(second, "Payload").Source;

        Assert.Contains("UnsafeAccessorAttribute", before, StringComparison.Ordinal);
        Assert.DoesNotContain("_codecProvider", before, StringComparison.Ordinal);
        Assert.DoesNotContain("??=", before, StringComparison.Ordinal);
        Assert.DoesNotContain("UnsafeAccessorAttribute", after, StringComparison.Ordinal);
        Assert.Contains("_codecProvider", after, StringComparison.Ordinal);
        Assert.Contains("FieldAccessor.Get", after, StringComparison.Ordinal);
        Assert.Contains("??=", after, StringComparison.Ordinal);
        Assert.NotEqual(before, after);
        // Hot reload deliberately adds a provider field to ALL ordinary DTOs, even the independent one.
        Assert.Equal(2, CountEmissionExecutions(second));
        Assert.All(GetEmitterOutputs(second), static emitter => Assert.Equal(IncrementalStepRunReason.Modified, emitter.Reason));
        Assert.NotEqual(GetOwnerEntry(first, "Independent"), GetOwnerEntry(second, "Independent"));
        AssertEnumCached(second);
        AssertFreshParity(compilation, second, options);

        var updated = ReplaceSource(compilation, (source + "\n" + IndependentTypes).Replace("=> 1", "=> 2", StringComparison.Ordinal));
        var (_, third) = Run(hotReloadDriver, updated);
        Assert.Equal(2, AssertCachedEmitters(third).Length);
        Assert.Equal(0, CountEmissionExecutions(third));
        Assert.Equal(GetSources(second), GetSources(third));
        AssertEnumCached(third);
        AssertFreshParity(updated, third, options);
    }

    [Fact]
    public async Task AddRemoveAndUndo_MatchFreshOutputAndCurrentOwnerSet()
    {
        const string source = """
            [Orleans.GenerateSerializer]
            public sealed class Payload { [Orleans.Id(5)] public System.Collections.Generic.List<int> Values { get; set; } }
            """;
        const string addition = """

            [Orleans.GenerateSerializer]
            public record struct Added([property: Orleans.Id(19)] System.Collections.Generic.List<string> Values);
            """;
        var compilation = await TestCompilationHelper.CreateCompilation(source + "\n" + IndependentTypes);
        var (driver, original) = Run(CreateDriver(), compilation);
        AssertOwners(original, "Independent", "Payload");

        var added = ReplaceSource(compilation, source + "\n" + IndependentTypes + addition);
        var (addedDriver, addedResult) = Run(driver, added);
        AssertOwners(addedResult, "Added", "Independent", "Payload");
        Assert.Equal(IncrementalStepRunReason.New, GetOwnerEmitter(addedResult, "Added").Reason);
        Assert.Equal(IncrementalStepRunReason.Cached, GetOwnerEmitter(addedResult, "Payload").Reason);
        Assert.Equal(IncrementalStepRunReason.Cached, GetOwnerEmitter(addedResult, "Independent").Reason);
        AssertFreshParity(added, addedResult);

        var (removedDriver, removedResult) = Run(addedDriver, compilation);
        AssertOwners(removedResult, "Independent", "Payload");
        Assert.Equal(GetSources(original), GetSources(removedResult));
        AssertFreshParity(compilation, removedResult);

        var (_, undoResult) = Run(removedDriver, added);
        AssertOwners(undoResult, "Added", "Independent", "Payload");
        Assert.Equal(IncrementalStepRunReason.New, GetOwnerEmitter(undoResult, "Added").Reason);
        Assert.Equal(GetSources(addedResult), GetSources(undoResult));
        AssertFreshParity(added, undoResult);
    }

    [Fact]
    public async Task PartialDeclarations_AttributeOnSecondTreeHasOneCurrentEmissionOwner()
    {
        const string firstPart = """
            public partial class Payload { [Orleans.Id(3)] public int First { get; set; } }
            """;
        const string attributedPart = """
            [Orleans.GenerateSerializer]
            public partial class Payload { [Orleans.Id(7)] public System.Collections.Generic.List<string> Second { get; set; } }
            """;
        const string memberPart = """
            public partial class Payload { [Orleans.Id(29)] public System.Collections.Generic.List<int> Third { get; set; } }
            """;
        var compilation = await TestCompilationHelper.CreateCompilation(firstPart);
        var attributeTree = Parse(attributedPart + "\n" + IndependentTypes, "Attribute.cs");
        compilation = compilation.AddSyntaxTrees(attributeTree);
        var (driver, original) = Run(CreateDriver(), compilation);
        AssertOwners(original, "Independent", "Payload");
        Assert.Contains("instance.First", GetOwnerEntry(original, "Payload").Source, StringComparison.Ordinal);
        Assert.Contains("instance.Second", GetOwnerEntry(original, "Payload").Source, StringComparison.Ordinal);
        Assert.DoesNotContain("instance.Third", GetOwnerEntry(original, "Payload").Source, StringComparison.Ordinal);
        AssertFreshParity(compilation, original);

        var memberTree = Parse(memberPart, "Member.cs");
        var added = compilation.AddSyntaxTrees(memberTree);
        var (addedDriver, addedResult) = Run(driver, added);
        AssertOwners(addedResult, "Independent", "Payload");
        Assert.Contains("instance.Third", GetOwnerEntry(addedResult, "Payload").Source, StringComparison.Ordinal);
        Assert.Equal(IncrementalStepRunReason.Modified, GetOwnerEmitter(addedResult, "Payload").Reason);
        Assert.Equal(IncrementalStepRunReason.Cached, GetOwnerEmitter(addedResult, "Independent").Reason);
        AssertFreshParity(added, addedResult);

        var reordered = added.RemoveAllSyntaxTrees().AddSyntaxTrees(memberTree, attributeTree, compilation.SyntaxTrees.First());
        var (reorderedDriver, reorderedResult) = Run(addedDriver, reordered);
        AssertOwners(reorderedResult, "Independent", "Payload");
        // Metadata registration order follows the symbol's first source location, which can change
        // when partial declarations are reordered. Owner emission stays exact; metadata must match
        // a fresh driver for the CURRENT tree order, not the previous compilation's registration order.
        Assert.Equal(GetSerializerEntries(addedResult), GetSerializerEntries(reorderedResult));
        AssertFreshParity(reordered, reorderedResult);

        var removed = reordered.RemoveSyntaxTrees(memberTree);
        var (_, removedResult) = Run(reorderedDriver, removed);
        AssertOwners(removedResult, "Independent", "Payload");
        Assert.DoesNotContain("instance.Third", GetOwnerEntry(removedResult, "Payload").Source, StringComparison.Ordinal);
        Assert.Equal(GetSerializerEntries(original), GetSerializerEntries(removedResult));
        AssertFreshParity(removed, removedResult);
    }

    [Fact]
    public async Task PreCancelledFreshAndRetainedRuns_ThrowAndRecover()
    {
        var compilation = await TestCompilationHelper.CreateCompilation(RichSource);
        var freshDriver = CreateDriver();
        var (retainedDriver, original) = Run(CreateDriver(), compilation);
        var updated = ReplaceSource(compilation, RichSource.Replace("Orleans.Id(11)", "Orleans.Id(12)", StringComparison.Ordinal));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => freshDriver.RunGenerators(updated, cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => retainedDriver.RunGenerators(updated, cancellation.Token));

        var (_, freshRecovery) = Run(freshDriver, updated);
        var (recoveredDriver, retainedRecovery) = Run(retainedDriver, updated);
        Assert.NotEqual(GetOwnerEntry(original, "Payload"), GetOwnerEntry(retainedRecovery, "Payload"));
        Assert.Equal(GetSources(freshRecovery), GetSources(retainedRecovery));
        Assert.Equal(GetDiagnosticFacts(freshRecovery), GetDiagnosticFacts(retainedRecovery));
        Assert.Equal(5, GetPlans(retainedRecovery).Length);
        Assert.Equal(IncrementalStepRunReason.Cached, GetOwnerEmitter(retainedRecovery, "ValuePayload").Reason);
        AssertEnumCached(retainedRecovery);
        AssertFreshParity(updated, retainedRecovery);

        var (_, undo) = Run(recoveredDriver, compilation);
        Assert.Equal(GetSources(original), GetSources(undo));
        AssertFreshParity(compilation, undo);
    }

    [Fact]
    public async Task SameIdentityPeReplacement_RefreshesTransitiveCopierAndCachesIndependentOwners()
    {
        const string referencedSource = """
            [Orleans.Immutable]
            public sealed class MemberType { public System.Collections.Generic.List<int> Values { get; set; } }
            public struct Wrapper { public MemberType Item; }
            """;
        var producer = await TestCompilationHelper.CreateCompilation(referencedSource, "Producer");
        var changedProducer = ReplaceSource(producer, referencedSource.Replace("[Orleans.Immutable]", "", StringComparison.Ordinal));
        Assert.Equal(producer.Assembly.Identity, changedProducer.Assembly.Identity);
        var firstReference = EmitReference(producer);
        var secondReference = EmitReference(changedProducer);
        var compilation = await TestCompilationHelper.CreateCompilation("""
            [Orleans.GenerateSerializer]
            public sealed class Payload { [Orleans.Id(37)] public Wrapper Item { get; set; } }
            """ + "\n" + IndependentTypes, "Consumer", firstReference);
        var (driver, first) = Run(CreateDriver(), compilation);
        var updated = compilation.ReplaceReference(firstReference, secondReference);
        var (updatedDriver, second) = Run(driver, updated);
        var before = GetOwnerEntry(first, "Payload").Source;
        var after = GetOwnerEntry(second, "Payload").Source;

        Assert.DoesNotContain("_copier", before, StringComparison.Ordinal);
        Assert.Contains("result.Item = original.Item;", before, StringComparison.Ordinal);
        Assert.Contains("_copier_Wrapper", after, StringComparison.Ordinal);
        Assert.Contains(".DeepCopy(original.Item, context)", after, StringComparison.Ordinal);
        Assert.NotEqual(before, after);
        Assert.Equal(IncrementalStepRunReason.Modified, GetOwnerEmitter(second, "Payload").Reason);
        Assert.Equal(IncrementalStepRunReason.Cached, GetOwnerEmitter(second, "Independent").Reason);
        Assert.Equal(GetOwnerEntry(first, "Independent"), GetOwnerEntry(second, "Independent"));
        AssertEnumCached(second);
        AssertFreshParity(updated, second);

        var (_, restored) = Run(updatedDriver, compilation);
        Assert.Equal(GetSources(first), GetSources(restored));
        AssertFreshParity(compilation, restored);
    }

    [Fact]
    public async Task RecreatedInvalidSetterTree_UsesCurrentDiagnosticLocationAndRecovers()
    {
        const string validSource = """
            [Orleans.GenerateSerializer]
            public sealed class Payload
            {
                [Orleans.Id(7)] public int Value { get => 0; set { } }
            }
            """;
        var source = validSource + "\n" + IndependentTypes;
        var compilation = await TestCompilationHelper.CreateCompilation(source);
        var (driver, valid) = Run(CreateDriver(), compilation);
        var invalidSource = source.Replace("set { }", "private set { }", StringComparison.Ordinal);
        var invalidCompilation = ReplaceSource(compilation, invalidSource);
        AssertNoCompilationErrors(invalidCompilation);
        driver = driver.RunGenerators(invalidCompilation, TestContext.Current.CancellationToken);
        var invalid = Assert.Single(driver.GetRunResult().Results);
        var firstDiagnostic = Assert.Single(invalid.Diagnostics);
        Assert.Equal("ORLEANS0101", firstDiagnostic.Id);
        Assert.Same(invalidCompilation.SyntaxTrees.Single(), firstDiagnostic.Location.SourceTree);
        ReportOwnerMismatch("valid -> invalid setter", invalidCompilation, valid, invalid, "Independent");
        AssertOwners(invalid, "Independent");
        AssertDiagnosticFreshParity(invalidCompilation, invalid);

        var recreated = ReplaceSource(invalidCompilation, invalidSource);
        driver = driver.RunGenerators(recreated, TestContext.Current.CancellationToken);
        var repeated = Assert.Single(driver.GetRunResult().Results);
        var diagnostic = Assert.Single(repeated.Diagnostics);
        Assert.Equal(GetDiagnosticFacts(invalid), GetDiagnosticFacts(repeated));
        Assert.NotSame(firstDiagnostic.Location.SourceTree, diagnostic.Location.SourceTree);
        Assert.Same(recreated.SyntaxTrees.Single(), diagnostic.Location.SourceTree);
        ReportOwnerMismatch("invalid -> identical invalid text in recreated tree", recreated, invalid, repeated, "Independent");
        AssertOwners(repeated, "Independent");
        Assert.Equal(IncrementalStepRunReason.Cached, GetOwnerEmitter(repeated, "Independent").Reason);
        AssertDiagnosticFreshParity(recreated, repeated);

        var recoveredCompilation = ReplaceSource(recreated, source);
        var (_, recovered) = Run(driver, recoveredCompilation);
        AssertOwners(recovered, "Independent", "Payload");
        Assert.Equal(GetSources(valid), GetSources(recovered));
        AssertFreshParity(recoveredCompilation, recovered);
    }

    [Fact]
    public async Task ReachablePlanGraph_ContainsTextValuesNotCompilationStateOrSyntax()
    {
        var compilation = await TestCompilationHelper.CreateCompilation(RichSource);
        var (_, result) = Run(CreateDriver(), compilation);
        var plans = GetPlans(result);
        Assert.Equal(5, plans.Length);
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var textCount = 0;
        foreach (var plan in plans)
        {
            Inspect(plan, plan.MetadataIdentity.MetadataName);
        }

        Assert.True(textCount > 0);
        output.WriteLine($"Inspected {plans.Length} DTO plans, {visited.Count} reachable objects and {textCount} text values. " +
            "Traversal covers every plan instance field and sequence element; plans retain no syntax or compilation state.");

        void Inspect(object? value, string path)
        {
            if (value is null || !visited.Add(value))
            {
                return;
            }

            Assert.False(value is Compilation, $"{path} retains a Compilation");
            Assert.False(value is ISymbol, $"{path} retains an ISymbol");
            Assert.False(value is SemanticModel, $"{path} retains a SemanticModel");
            Assert.False(value is SourceText, $"{path} retains SourceText");
            Assert.False(value is SyntaxTree, $"{path} retains a SyntaxTree");
            Assert.False(value is SyntaxNode, $"{path} retains a SyntaxNode");
            if (value is string text)
            {
                textCount++;
                Assert.DoesNotContain("#pragma warning disable", text, StringComparison.Ordinal);
                return;
            }

            var type = value.GetType();
            if (type.IsPrimitive || type.IsEnum)
            {
                return;
            }

            if (value is IEnumerable sequence)
            {
                var index = 0;
                foreach (var item in sequence)
                {
                    Inspect(item, $"{path}[{index++}]");
                }

                return;
            }

            // Only descend into owned plan data, never runtime Type metadata or framework global caches.
            Assert.Equal(typeof(DtoSerializerOutputPlan).Assembly, type.Assembly);
            for (var current = type; current is not null && current != typeof(object) && current != typeof(ValueType); current = current.BaseType)
            {
                foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    Inspect(field.GetValue(value), path + "." + field.Name);
                }
            }
        }
    }

    private static (string Source, string Edited) GetDependencySources(string change)
    {
        const string property = """
            [Orleans.GenerateSerializer]
            public sealed class Payload { [Orleans.Id(7)] public string Value { get; set; } }
            """;
        var source = change switch
        {
            "transitive-immutable" => """
                [Orleans.Immutable]
                public struct Leaf { public System.Collections.Generic.List<int> Values; }
                public struct Bridge { public Leaf Value; }
                [Orleans.GenerateSerializer]
                public sealed class Payload { [Orleans.Id(7)] public Bridge Item { get; set; } }
                """,
            "transitive-field" => """
                public struct Leaf { public int Values; }
                public struct Bridge { public Leaf Value; }
                [Orleans.GenerateSerializer]
                public sealed class Payload { [Orleans.Id(7)] public Bridge Item { get; set; } }
                """,
            "effective-base" => """
                [Orleans.GenerateSerializer]
                public class FirstBase { [Orleans.Id(1)] public int First { get; set; } }
                [Orleans.GenerateSerializer]
                public class SecondBase { [Orleans.Id(3)] public string Second { get; set; } }
                [Orleans.GenerateSerializer]
                public sealed class Payload : FirstBase { [Orleans.Id(7)] public int Value { get; set; } }
                """,
            "transparent-base" => """
                [Orleans.GenerateSerializer]
                public class FirstBase { [Orleans.Id(1)] public int First { get; set; } }
                [Orleans.SerializerTransparent]
                public abstract class Middle : FirstBase { }
                [Orleans.GenerateSerializer]
                public sealed class Payload : Middle { [Orleans.Id(7)] public int Value { get; set; } }
                """,
            "constructor" => """
                [Orleans.GenerateSerializer]
                public sealed class Payload
                {
                    public Payload() { }
                    [Orleans.Id(7)] public int Value { get; set; }
                }
                """,
            "required" or "init" or "accessor" => property,
            "readonly" => """
                [Orleans.GenerateSerializer]
                public sealed class Payload { [Orleans.Id(7)] public int Value; }
                """,
            "constraints" => """
                [Orleans.GenerateSerializer]
                public sealed class Payload<T> where T : class
                {
                    [Orleans.Id(7)] public System.Collections.Generic.List<T> Values { get; set; }
                }
                """,
            "hook-arity" or "hook-ref" => """
                [Orleans.GenerateSerializer, Orleans.SerializationCallbacks(typeof(Hooks))]
                public sealed class Payload { [Orleans.Id(7)] public int Value { get; set; } }
                public sealed class Hooks
                {
                    public void OnSerializing(Payload value) { }
                    public void OnCopying(Payload original, Payload copy) { }
                }
                """,
            "field-id" or "accessibility" => """
                [Orleans.GenerateSerializer]
                public sealed class Payload { [Orleans.Id(7)] public int Value { get; set; } }
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        var edited = change switch
        {
            "transitive-immutable" => source.Replace("[Orleans.Immutable]", "", StringComparison.Ordinal),
            "transitive-field" => source.Replace("int Values", "System.Collections.Generic.List<int> Values", StringComparison.Ordinal),
            "effective-base" => source.Replace("Payload : FirstBase", "Payload : SecondBase", StringComparison.Ordinal),
            "transparent-base" => source.Replace("[Orleans.SerializerTransparent]", "", StringComparison.Ordinal),
            "constructor" => source.Replace("public Payload()", "private Payload()", StringComparison.Ordinal),
            "required" => source.Replace("public string Value", "public required string Value", StringComparison.Ordinal),
            "init" => source.Replace("get; set;", "get; init;", StringComparison.Ordinal),
            "readonly" => source.Replace("public int Value", "public readonly int Value", StringComparison.Ordinal),
            "accessor" => source.Replace("get; set;", "get; private set;", StringComparison.Ordinal),
            "constraints" => source.Replace("where T : class", "where T : class, new()", StringComparison.Ordinal),
            "hook-arity" => source.Replace("OnSerializing(Payload value)", "OnSerializing()", StringComparison.Ordinal)
                .Replace("OnCopying(Payload original, Payload copy)", "OnCopying(Payload original)", StringComparison.Ordinal),
            "hook-ref" => source.Replace("OnSerializing(Payload value)", "OnSerializing(ref Payload value)", StringComparison.Ordinal)
                .Replace("OnCopying(Payload original, Payload copy)", "OnCopying(ref Payload original, ref Payload copy)", StringComparison.Ordinal),
            "field-id" => source.Replace("Orleans.Id(7)", "Orleans.Id(19)", StringComparison.Ordinal),
            "accessibility" => source.Replace("public sealed class Payload", "internal sealed class Payload", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        return (source, edited);
    }

    private static void AssertDependencySourceFacts(string change, string before, string after)
    {
        switch (change)
        {
            case "transitive-immutable":
            case "transitive-field":
                Assert.DoesNotContain("_copier", before, StringComparison.Ordinal);
                Assert.Contains("result.Item = original.Item;", before, StringComparison.Ordinal);
                Assert.Contains("_copier_Bridge", after, StringComparison.Ordinal);
                Assert.Contains(".DeepCopy(original.Item, context)", after, StringComparison.Ordinal);
                break;
            case "effective-base":
                Assert.Contains("Codec_FirstBase _baseTypeSerializer", before, StringComparison.Ordinal);
                Assert.Contains("Copier_FirstBase _baseTypeCopier", before, StringComparison.Ordinal);
                Assert.Contains("Codec_SecondBase _baseTypeSerializer", after, StringComparison.Ordinal);
                Assert.Contains("Copier_SecondBase _baseTypeCopier", after, StringComparison.Ordinal);
                Assert.DoesNotContain("Codec_FirstBase _baseTypeSerializer", after, StringComparison.Ordinal);
                break;
            case "transparent-base":
                Assert.Contains("Codec_FirstBase _baseTypeSerializer", before, StringComparison.Ordinal);
                Assert.Contains("IBaseCodec<global::Middle> _baseTypeSerializer", after, StringComparison.Ordinal);
                Assert.Contains("IBaseCopier<global::Middle> _baseTypeCopier", after, StringComparison.Ordinal);
                break;
            case "constructor":
            case "required":
                Assert.Contains("class Activator_Payload", before, StringComparison.Ordinal);
                Assert.DoesNotContain("_activator", before, StringComparison.Ordinal);
                Assert.DoesNotContain("class Activator_Payload", after, StringComparison.Ordinal);
                Assert.Contains("_activator.Create()", after, StringComparison.Ordinal);
                break;
            case "init":
            case "accessor":
                Assert.Contains("instance.Value =", before, StringComparison.Ordinal);
                Assert.DoesNotContain("k__BackingField", before, StringComparison.Ordinal);
                Assert.DoesNotContain("instance.Value =", after, StringComparison.Ordinal);
                Assert.Contains("Name = \"<Value>k__BackingField\"", after, StringComparison.Ordinal);
                Assert.Contains("accessField_", after, StringComparison.Ordinal);
                break;
            case "readonly":
                Assert.Contains("instance.Value =", before, StringComparison.Ordinal);
                Assert.DoesNotContain("Name = \"Value\"", before, StringComparison.Ordinal);
                Assert.DoesNotContain("instance.Value =", after, StringComparison.Ordinal);
                Assert.Contains("Name = \"Value\"", after, StringComparison.Ordinal);
                Assert.Contains("accessField_", after, StringComparison.Ordinal);
                break;
            case "constraints":
                Assert.Contains("where T : class", before, StringComparison.Ordinal);
                Assert.DoesNotContain("where T : class, new()", before, StringComparison.Ordinal);
                Assert.Contains("where T : class, new()", after, StringComparison.Ordinal);
                // All emitted generic classes, including the activator, must carry the changed constraint.
                var root = Parse(after).GetRoot(TestContext.Current.CancellationToken);
                var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>().ToArray();
                Assert.Equal(3, classes.Length);
                Assert.All(classes, static declaration => Assert.Contains("where T : class, new()", declaration.ToString(), StringComparison.Ordinal));
                break;
            case "hook-arity":
                Assert.Contains("_hook0.OnSerializing(instance);", before, StringComparison.Ordinal);
                Assert.Contains("_hook0.OnCopying(original, result);", before, StringComparison.Ordinal);
                Assert.DoesNotContain(".OnSerializing(", after, StringComparison.Ordinal);
                Assert.DoesNotContain(".OnCopying(", after, StringComparison.Ordinal);
                break;
            case "hook-ref":
                Assert.Contains("_hook0.OnSerializing(instance);", before, StringComparison.Ordinal);
                Assert.Contains("_hook0.OnCopying(original, result);", before, StringComparison.Ordinal);
                Assert.Contains("_hook0.OnSerializing(ref instance);", after, StringComparison.Ordinal);
                Assert.Contains("_hook0.OnCopying(ref original, ref result);", after, StringComparison.Ordinal);
                break;
            case "field-id":
                Assert.Contains("Int32Codec.WriteField(ref writer, 7U, instance.Value);", before, StringComparison.Ordinal);
                Assert.Contains("Int32Codec.WriteField(ref writer, 19U, instance.Value);", after, StringComparison.Ordinal);
                Assert.Contains("if (id == 19U)", after, StringComparison.Ordinal);
                Assert.DoesNotContain("if (id == 7U)", after, StringComparison.Ordinal);
                break;
            case "accessibility":
                Assert.Contains("public sealed class Codec_Payload", before, StringComparison.Ordinal);
                Assert.Contains("public sealed class Copier_Payload", before, StringComparison.Ordinal);
                Assert.Contains("internal sealed class Codec_Payload", after, StringComparison.Ordinal);
                Assert.Contains("internal sealed class Copier_Payload", after, StringComparison.Ordinal);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(change));
        }
    }

    private static void AssertRichSourceFacts(GeneratorRunResult result)
    {
        var payload = GetOwnerEntry(result, "Payload").Source;
        var payloadRoot = Parse(payload).GetRoot(TestContext.Current.CancellationToken);
        Assert.Contains("Name = \"_items\"", payload, StringComparison.Ordinal);
        Assert.Contains("Name = \"_stamp\"", payload, StringComparison.Ordinal);
        AssertTypePresent(payloadRoot,
            "global::System.Collections.Generic.Dictionary<string, global::System.Collections.Generic.List<global::Example.instance.value.context.Item[]>>");
        Assert.Contains("_hook0.OnSerializing(ref instance);", payload, StringComparison.Ordinal);
        Assert.Contains("_hook0.OnCopying(input, ref output);", payload, StringComparison.Ordinal);
        foreach (var name in new[] { "instance", "value", "context" })
        {
            // ValueText accepts legal verbatim spelling (@value) without losing the exact user identifier.
            Assert.Contains(payloadRoot.DescendantNodes().OfType<MemberAccessExpressionSyntax>(),
                access => IsMemberAccess(access, "instance", name));
            var assignment = Assert.Single(payloadRoot.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
                assignment => assignment.Left is MemberAccessExpressionSyntax access && IsMemberAccess(access, "output", name));
            var copy = Assert.IsType<InvocationExpressionSyntax>(assignment.Right);
            Assert.Equal("DeepCopy", Assert.IsType<MemberAccessExpressionSyntax>(copy.Expression).Name.Identifier.ValueText);
            Assert.Equal(2, copy.ArgumentList.Arguments.Count);
            Assert.True(IsMemberAccess(Assert.IsType<MemberAccessExpressionSyntax>(copy.ArgumentList.Arguments[0].Expression), "input", name));
            Assert.Equal("context", Assert.IsType<IdentifierNameSyntax>(copy.ArgumentList.Arguments[1].Expression).Identifier.ValueText);
        }

        var valuePayload = GetOwnerEntry(result, "ValuePayload").Source;
        var valueRoot = Parse(valuePayload).GetRoot(TestContext.Current.CancellationToken);
        AssertTypePresent(valueRoot,
            "global::Orleans.Serialization.Serializers.IValueSerializer<global::Example.instance.value.context.ValuePayload>");
        var valueTypeTokens = TypeTokens(SyntaxFactory.ParseTypeName("global::Example.instance.value.context.ValuePayload"));
        Assert.Contains(valueRoot.DescendantNodes().OfType<ParameterSyntax>(),
            parameter => parameter.Modifiers.Any(SyntaxKind.RefKeyword)
                && parameter.Type is { } type && TypeTokens(type).SequenceEqual(valueTypeTokens));
        Assert.Contains("Name = \"_items\"", valuePayload, StringComparison.Ordinal);
        Assert.Contains("_copier_List_Item", valuePayload, StringComparison.Ordinal);
        Assert.Contains(".DeepCopy(", valuePayload, StringComparison.Ordinal);
        var record = GetOwnerEntry(result, "RecordPayload").Source;
        Assert.Contains("Codec_RecordPayload<T>", record, StringComparison.Ordinal);
        Assert.Contains("Copier_RecordPayload<T>", record, StringComparison.Ordinal);
        Assert.Contains("where T : class, new()", record, StringComparison.Ordinal);
        Assert.Contains("k__BackingField", record, StringComparison.Ordinal);
        Assert.Contains("Copier_ValueRecord", GetOwnerEntry(result, "ValueRecord").Source, StringComparison.Ordinal);
    }

    private static bool IsMemberAccess(MemberAccessExpressionSyntax access, string receiver, string member)
        => access.Expression is IdentifierNameSyntax identifier && identifier.Identifier.ValueText == receiver
            && access.Name.Identifier.ValueText == member;

    private static void AssertTypePresent(SyntaxNode root, string expected)
    {
        var tokens = TypeTokens(SyntaxFactory.ParseTypeName(expected));
        Assert.Contains(root.DescendantNodes().OfType<TypeSyntax>(), type => TypeTokens(type).SequenceEqual(tokens));
    }

    private static (int Kind, string Text)[] TypeTokens(TypeSyntax type)
        => type.DescendantTokens().Select(static token => (token.RawKind, token.ValueText)).ToArray();

    private static GeneratorDriver CreateDriver(AnalyzerConfigOptionsProvider? options = null)
        => CSharpGeneratorDriver.Create(
            generators: [new OrleansSerializationSourceGenerator().AsSourceGenerator()],
            optionsProvider: options ?? TestCompilationHelper.CreateOptionsProvider(),
            driverOptions: new GeneratorDriverOptions(default, trackIncrementalGeneratorSteps: true));

    private static AnalyzerConfigOptionsProvider Options(params (string Name, string Value)[] values)
        => TestCompilationHelper.CreateOptionsProvider(values.ToDictionary(static value => value.Name, static value => value.Value));

    private static (GeneratorDriver Driver, GeneratorRunResult Result) Run(GeneratorDriver driver, CSharpCompilation compilation)
    {
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out var diagnostics, TestContext.Current.CancellationToken);
        return (driver, ValidateRun(driver, updated, diagnostics));
    }

    private static GeneratorRunResult ValidateRun(GeneratorDriver driver, Compilation updated, IEnumerable<Diagnostic> diagnostics)
    {
        Assert.Empty(diagnostics);
        var result = Assert.Single(driver.GetRunResult().Results);
        Assert.Null(result.Exception);
        Assert.Empty(result.Diagnostics);
        AssertNoCompilationErrors(updated);
        return result;
    }

    private static void AssertNoCompilationErrors(Compilation compilation)
    {
        var errors = compilation.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(static diagnostic => diagnostic.ToString())));
    }

    private static void AssertFreshParity(CSharpCompilation compilation, GeneratorRunResult retained, AnalyzerConfigOptionsProvider? options = null)
    {
        var (_, fresh) = Run(CreateDriver(options), compilation);
        Assert.Equal(GetSources(fresh), GetSources(retained));
        Assert.Equal(GetDiagnosticFacts(fresh), GetDiagnosticFacts(retained));
    }

    private static void AssertDiagnosticFreshParity(CSharpCompilation compilation, GeneratorRunResult retained)
    {
        var driver = CreateDriver().RunGenerators(compilation, TestContext.Current.CancellationToken);
        var fresh = Assert.Single(driver.GetRunResult().Results);
        Assert.Null(retained.Exception);
        Assert.Null(fresh.Exception);
        Assert.Equal(GetSources(fresh), GetSources(retained));
        Assert.Equal(GetDiagnosticFacts(fresh), GetDiagnosticFacts(retained));
        Assert.All(retained.Diagnostics, diagnostic => Assert.Contains(compilation.SyntaxTrees,
            tree => ReferenceEquals(tree, diagnostic.Location.SourceTree)));
        Assert.All(fresh.Diagnostics, diagnostic => Assert.Contains(compilation.SyntaxTrees,
            tree => ReferenceEquals(tree, diagnostic.Location.SourceTree)));
    }

    private void ReportOwnerMismatch(
        string transition,
        CSharpCompilation compilation,
        GeneratorRunResult previous,
        GeneratorRunResult retained,
        params string[] expectedOwners)
    {
        var owners = GetPlans(retained).Select(static plan => plan.MetadataIdentity.MetadataName).Order(StringComparer.Ordinal).ToArray();
        if (owners.SequenceEqual(expectedOwners.Order(StringComparer.Ordinal))
            && GetEmitterOutputs(retained).Length == expectedOwners.Length
            && GetSerializerEntries(retained).Length == expectedOwners.Length + 1
            && expectedOwners.All(owner => GetSerializerEntries(retained).Any(entry => IsOwnerEntry(entry.Source, owner))))
        {
            return;
        }

        // Failure evidence only: do not infer registered output from tracking reasons. In particular,
        // keep Removed entries visible and run a fresh driver before the unchanged owner assertion fails.
        var freshDriver = CreateDriver().RunGenerators(compilation, TestContext.Current.CancellationToken);
        var fresh = Assert.Single(freshDriver.GetRunResult().Results);
        output.WriteLine($"Owner mismatch at transition: {transition}");
        output.WriteLine($"Roslyn: {typeof(Compilation).Assembly.FullName}");
        output.WriteLine($"Expected DTO owners: [{string.Join(", ", expectedOwners)}]");
        foreach (var tree in compilation.SyntaxTrees)
        {
            output.WriteLine($"Current fixture, path='{tree.FilePath}':{Environment.NewLine}{tree.GetText(TestContext.Current.CancellationToken)}");
        }

        Dump("previous", previous);
        Dump("retained current", retained);
        Dump("fresh current", fresh);
        output.WriteLine($"Retained/fresh complete generated sources equal: {GetSources(retained).SequenceEqual(GetSources(fresh))}");
        output.WriteLine($"Retained/fresh diagnostic facts equal: {GetDiagnosticFacts(retained).SequenceEqual(GetDiagnosticFacts(fresh))}");

        void Dump(string label, GeneratorRunResult result)
        {
            output.WriteLine($"{label}: filtered DTO plan owners=[{string.Join(", ", GetPlans(result).Select(static plan => plan.MetadataIdentity.MetadataName))}]; " +
                $"filtered DTO emitters={GetEmitterOutputs(result).Length}; actual registered serializers={GetSerializerEntries(result).Length}; " +
                $"actual registered Independent={GetSerializerEntries(result).Any(static entry => IsOwnerEntry(entry.Source, "Independent"))}; " +
                $"exception={result.Exception}");
            foreach (var entry in GetSerializerEntries(result))
            {
                output.WriteLine($"  registered: {DescribeTrackingValue(SourceOutputResult.FromSource(entry))}");
            }

            foreach (var diagnostic in result.Diagnostics)
            {
                output.WriteLine($"  diagnostic: {diagnostic.Id}; {diagnostic.GetMessage(CultureInfo.InvariantCulture)}; span={diagnostic.Location.SourceSpan}");
            }

            foreach (var trackingName in new[]
            {
                OrleansSerializationSourceGenerator.SerializableTypeResultsTrackingName,
                OrleansSerializationSourceGenerator.DtoSerializerPlansTrackingName,
                OrleansSerializationSourceGenerator.DtoSerializerOutputsTrackingName,
                OrleansSerializationSourceGenerator.EnumSerializerPlansTrackingName,
                OrleansSerializationSourceGenerator.EnumSerializerOutputsTrackingName,
                OrleansSerializationSourceGenerator.SerializerOutputsTrackingName,
            })
            {
                if (!result.TrackedSteps.TryGetValue(trackingName, out var steps))
                {
                    output.WriteLine($"  {trackingName}: <absent>");
                    continue;
                }

                for (var stepIndex = 0; stepIndex < steps.Length; stepIndex++)
                {
                    var step = steps[stepIndex];
                    output.WriteLine($"  {trackingName} step[{stepIndex}] inputs=[{string.Join(", ", step.Inputs.Select(static input => $"{input.Source.Name ?? "<unnamed>"}[{input.OutputIndex}]"))}]");
                    for (var outputIndex = 0; outputIndex < step.Outputs.Length; outputIndex++)
                    {
                        var tracked = step.Outputs[outputIndex];
                        output.WriteLine($"    output[{outputIndex}] {tracked.Reason}: {DescribeTrackingValue(tracked.Value)}");
                    }
                }
            }
        }
    }

    private static string DescribeTrackingValue(object? value)
    {
        return value switch
        {
            null => "<empty declaration slot>",
            SerializableTypeResult type => $"type={type.MetadataIdentity.MetadataName}; dtoPlan={type.DtoSerializerPlan is not null}; " +
                $"enumPlan={type.EnumSerializerPlan is not null}; diagnostic={type.Diagnostic?.Id ?? "<none>"}",
            DtoSerializerOutputPlan plan => $"DTO plan={plan.MetadataIdentity.MetadataName}",
            EnumSerializerOutputPlan => "enum plan",
            SourceOutputResult result => result.SourceEntry is { } entry
                ? $"hint={entry.HintName}; codecs=[{string.Join(", ", Parse(entry.Source).GetRoot(TestContext.Current.CancellationToken).DescendantNodes().OfType<ClassDeclarationSyntax>().Select(static declaration => declaration.Identifier.ValueText).Where(static name => name.StartsWith("Codec_", StringComparison.Ordinal)))}]"
                : $"no source; diagnostic={result.Diagnostic?.Id ?? "<none>"}",
            IEnumerable<SourceOutputResult> results => $"source batch=[{string.Join("; ", results.Select(static result => DescribeTrackingValue(result)))}]",
            _ => value.GetType().FullName ?? value.GetType().Name,
        };
    }

    private static (string Id, DiagnosticSeverity Severity, string Message, TextSpan Span, string Path)[] GetDiagnosticFacts(GeneratorRunResult result)
        => result.Diagnostics.Select(static diagnostic => (
                Id: diagnostic.Id, Severity: diagnostic.Severity, Message: diagnostic.GetMessage(CultureInfo.InvariantCulture),
                Span: diagnostic.Location.SourceSpan, Path: diagnostic.Location.GetLineSpan().Path))
            .OrderBy(static diagnostic => diagnostic.Id, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Span.Start).ToArray();

    private static CSharpSyntaxTree Parse(string source, string path = "")
        => (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(source, path: path, cancellationToken: TestContext.Current.CancellationToken);

    private static CSharpCompilation ReplaceSource(CSharpCompilation compilation, string source)
    {
        var original = compilation.SyntaxTrees.Single();
        return compilation.ReplaceSyntaxTree(original,
            CSharpSyntaxTree.ParseText(source, (CSharpParseOptions)original.Options, original.FilePath, cancellationToken: TestContext.Current.CancellationToken));
    }

    private static string[] GetSources(GeneratorRunResult result)
        => result.GeneratedSources.OrderBy(static source => source.HintName, StringComparer.Ordinal)
            .Select(static source => $"{source.HintName}\n{source.SourceText}").ToArray();

    private static GeneratedSourceEntry[] GetSerializerEntries(GeneratorRunResult result)
        => result.GeneratedSources.Where(static source => source.HintName.Contains(".orleans.ser.", StringComparison.Ordinal))
            .OrderBy(static source => source.HintName, StringComparer.Ordinal)
            .Select(static source => new GeneratedSourceEntry(source.HintName, source.SourceText.ToString())).ToArray();

    private static DtoSerializerOutputPlan[] GetPlans(GeneratorRunResult result)
        => result.TrackedSteps.TryGetValue(OrleansSerializationSourceGenerator.DtoSerializerPlansTrackingName, out var steps)
            ? steps.SelectMany(static step => step.Outputs)
                .Where(static output => output.Reason != IncrementalStepRunReason.Removed)
                .Select(static output => output.Value).OfType<DtoSerializerOutputPlan>().ToArray()
            : [];

    private static (GeneratedSourceEntry Entry, IncrementalStepRunReason Reason)[] GetEmitterOutputs(GeneratorRunResult result)
        => GetEmitterOutputs(result, OrleansSerializationSourceGenerator.DtoSerializerOutputsTrackingName);

    private static (GeneratedSourceEntry Entry, IncrementalStepRunReason Reason)[] GetEmitterOutputs(GeneratorRunResult result, string trackingName)
        => result.TrackedSteps.TryGetValue(trackingName, out var steps)
            ? steps.SelectMany(static step => step.Outputs)
                .Where(static output => output.Reason != IncrementalStepRunReason.Removed
                    && output.Value is SourceOutputResult { SourceEntry: not null })
                .Select(static output => (((SourceOutputResult)output.Value).SourceEntry!.Value, output.Reason)).ToArray()
            : [];

    private static int CountEmissionExecutions(GeneratorRunResult result)
        // Unchanged is an executed selector whose output compared equal; it is NOT a cache hit.
        => GetEmitterOutputs(result).Count(static emitter => emitter.Reason != IncrementalStepRunReason.Cached);

    private static GeneratedSourceEntry[] AssertCachedEmitters(GeneratorRunResult result)
    {
        var emitters = GetEmitterOutputs(result);
        Assert.NotEmpty(emitters);
        Assert.All(emitters, static emitter => Assert.Equal(IncrementalStepRunReason.Cached, emitter.Reason));
        return emitters.Select(static emitter => emitter.Entry).ToArray();
    }

    private static void AssertEnumCached(GeneratorRunResult result)
    {
        var emitter = Assert.Single(GetEmitterOutputs(result, OrleansSerializationSourceGenerator.EnumSerializerOutputsTrackingName));
        Assert.Equal(IncrementalStepRunReason.Cached, emitter.Reason);
    }

    private static (GeneratedSourceEntry Entry, IncrementalStepRunReason Reason) GetOwnerEmitter(GeneratorRunResult result, string name)
        => Assert.Single(GetEmitterOutputs(result), emitter => IsOwnerEntry(emitter.Entry.Source, name));

    private static GeneratedSourceEntry GetOwnerEntry(GeneratorRunResult result, string name)
        => Assert.Single(GetSerializerEntries(result), entry => IsOwnerEntry(entry.Source, name));

    private static bool IsOwnerEntry(string source, string name)
        => source.Contains($"sealed class Codec_{name} ", StringComparison.Ordinal)
            || source.Contains($"sealed class Codec_{name}<", StringComparison.Ordinal);

    private static void AssertOwners(GeneratorRunResult result, params string[] names)
    {
        Assert.Equal(names.Order(StringComparer.Ordinal),
            GetPlans(result).Select(static plan => plan.MetadataIdentity.MetadataName).Order(StringComparer.Ordinal));
        Assert.Equal(names.Length, GetEmitterOutputs(result).Length);
        Assert.Equal(names.Length + 1,
            result.GeneratedSources.Count(static source => source.HintName.Contains(".orleans.ser.", StringComparison.Ordinal)));
        foreach (var name in names)
        {
            Assert.Equal(GetOwnerEntry(result, name), GetOwnerEmitter(result, name).Entry);
        }

        GetOwnerEntry(result, "Stable");
    }

    private static MetadataReference EmitReference(CSharpCompilation compilation)
    {
        using var image = new MemoryStream();
        var result = compilation.Emit(image, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return MetadataReference.CreateFromImage(image.ToArray());
    }
}
