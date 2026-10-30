using System.Collections.Immutable;
using System.IO;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Orleans.CodeGenerator.Model;
using Orleans.CodeGenerator.SyntaxGeneration;

namespace Orleans.CodeGenerator.Tests;

public class ReferenceAssemblyModelExtractorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Generator_SourceDiscoveriesAreReusedWithFullScanParity(bool useMetadataReference)
    {
        var library = await TestCompilationHelper.CreateCompilation(
            """
            using Orleans;
            namespace External;
            [GenerateSerializer, Alias("external")]
            public class Dto { [Id(0)] public int Value { get; set; } }
            public interface IExternalGrain : IGrainWithIntegerKey
            {
                System.Threading.Tasks.Task<Dto> Get();
            }
            """, "External");
        MetadataReference reference = library.ToMetadataReference();
        if (useMetadataReference)
        {
            using var stream = new MemoryStream();
            var emit = library.Emit(stream, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
            reference = MetadataReference.CreateFromImage(stream.ToArray());
        }

        var compilation = await TestCompilationHelper.CreateCompilation(
            """
            using Orleans;
            using System.Threading.Tasks;
            [assembly: GenerateCodeForDeclaringAssembly(typeof(External.Dto))]
            [assembly: RegisterProvider("test", "Clustering", "Client", typeof(Targets.Provider))]
            namespace Targets;
            public class Provider { }
            public partial class Outer<T>
            {
                [GenerateSerializer, Id(42), Alias("dto"), CompoundTypeAlias("dto", typeof(string))]
                public partial class Dto<TItem> { [Id(0)] public TItem Value { get; set; } }
            }
            [GenerateSerializer]
            public enum State : byte { Ready }
            public interface ITargetGrain : IGrainWithIntegerKey
            {
                Task<Outer<int>.Dto<string>> Get();
            }
            public interface IDerivedGrain : ITargetGrain { }
            public class TargetGrain : Grain, IDerivedGrain
            {
                public Task<Outer<int>.Dto<string>> Get() => Task.FromResult(new Outer<int>.Dto<string>());
            }
            [RegisterActivator]
            public class Activator : Orleans.Serialization.Activators.IActivator<Provider>
            {
                public Provider Create() => new();
            }
            """, "Targets", reference,
            MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.Options.IConfigureOptions<>).Assembly.Location));
        compilation = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(
            """
            namespace Targets;
            public partial class Outer<T>
            {
                public partial class Dto<TItem> { [Orleans.Id(1)] public T Other { get; set; } }
            }
            """, cancellationToken: TestContext.Current.CancellationToken));
        var result = RunTrackedGenerator(compilation);
        Assert.Empty(result.Diagnostics);
        var serializers = GetTrackedOutput<ImmutableArray<SerializableTypeModel>>(
            result, OrleansSerializationSourceGenerator.CollectedSerializableTypesTrackingName);
        var proxies = GetTrackedOutput<ImmutableArray<ProxyInterfaceModel>>(
            result, OrleansSerializationSourceGenerator.CollectedProxyInterfacesTrackingName);
        var referenceData = GetTrackedOutput<ReferenceAssemblyDataResult>(
            result, OrleansSerializationSourceGenerator.ReferenceAssemblyDataTrackingName);
        var fullScan = ReferenceAssemblyModelExtractor.ExtractReferenceAssemblyData(
            compilation, new CodeGeneratorOptions(), TestContext.Current.CancellationToken, out var diagnostics);

        Assert.Empty(diagnostics);
        Assert.Equal(fullScan, referenceData.Model);
        Assert.Equal(2, serializers.Length);
        Assert.Equal(2, proxies.Length);
        foreach (var model in serializers)
        {
            Assert.Same(model, Assert.Single(referenceData.Model.ReferencedSerializableTypes,
                candidate => candidate.MetadataIdentity == model.MetadataIdentity));
        }

        foreach (var model in proxies)
        {
            Assert.Same(model, Assert.Single(referenceData.Model.ReferencedProxyInterfaces,
                candidate => candidate.MetadataIdentity == model.MetadataIdentity));
        }

        Assert.Contains(referenceData.Model.ReferencedSerializableTypes, static model => model.MetadataIdentity.AssemblyName == "External");
        Assert.Contains(referenceData.Model.ReferencedProxyInterfaces, static model => model.MetadataIdentity.AssemblyName == "External");
        Assert.Equal(new WellKnownTypeIdModel(new TypeRef("global::Targets.Outer<>.Dto<>"), 42),
            Assert.Single(referenceData.Model.WellKnownTypeIds));
        Assert.Single(referenceData.Model.RegisteredProviders);
        Assert.Single(referenceData.Model.RegisteredCodecs);
        Assert.Single(referenceData.Model.InterfaceImplementations);

        var prepared = GetTrackedOutput<ProxyOutputPreparationResult>(
            result, OrleansSerializationSourceGenerator.PreparedProxyOutputsTrackingName);
        var aggregate = ModelExtractor.CreateMetadataAggregate("Targets", serializers, prepared.ProxyOutputModels, fullScan);
        var expectedMetadata = MetadataSourceOutputGenerator.CreateMetadataSourceOutput(
            aggregate, SourceGeneratorOptionsParser.ParseOptions(TestCompilationHelper.CreateOptionsProvider().GlobalOptions),
            supportsModuleInitializers: true);
        Assert.Null(expectedMetadata.Diagnostic);
        var metadata = Assert.Single(result.GeneratedSources,
            source => source.HintName == expectedMetadata.SourceEntry!.Value.HintName);
        Assert.Equal(expectedMetadata.SourceEntry!.Value.Source, metadata.SourceText.ToString());
    }

    [Theory]
    [InlineData(32)]
    [InlineData(64)]
    public async Task ExtractReferenceAssemblyData_ReusesAvailableDiscoveriesAndReducesAllocations(int discoveredCount)
    {
        var source = new StringBuilder("using Orleans;\nnamespace Targets;\n");
        for (var i = 0; i < 64; i++)
        {
            source.Append("[GenerateSerializer] public class Dto").Append(i)
                .Append(" { [Id(0)] public string Name { get; set; } [Id(1)] public int Value { get; set; } }\n")
                .Append("public interface IGrain").Append(i)
                .Append(" : IGrainWithIntegerKey { System.Threading.Tasks.Task<int> Get(); }\n");
        }

        var compilation = await TestCompilationHelper.CreateCompilation(source.ToString());
        var options = new CodeGeneratorOptions();
        var fullScan = ReferenceAssemblyModelExtractor.ExtractReferenceAssemblyData(
            compilation, options, TestContext.Current.CancellationToken);
        Assert.Equal(64, fullScan.ReferencedSerializableTypes.Length);
        Assert.Equal(64, fullScan.ReferencedProxyInterfaces.Length);
        var serializers = fullScan.ReferencedSerializableTypes.Take(discoveredCount).ToImmutableArray();
        var proxies = fullScan.ReferencedProxyInterfaces.Take(discoveredCount).ToImmutableArray();

        _ = Extract();
        var start = GC.GetAllocatedBytesForCurrentThread();
        var reused = Extract();
        var reusedBytes = GC.GetAllocatedBytesForCurrentThread() - start;
        start = GC.GetAllocatedBytesForCurrentThread();
        var scanned = ReferenceAssemblyModelExtractor.ExtractReferenceAssemblyData(
            compilation, options, TestContext.Current.CancellationToken);
        var scannedBytes = GC.GetAllocatedBytesForCurrentThread() - start;

        Assert.Equal(fullScan, reused);
        Assert.Equal(fullScan, scanned);
        foreach (var model in serializers)
        {
            Assert.Same(model, Assert.Single(reused.ReferencedSerializableTypes,
                candidate => candidate.MetadataIdentity == model.MetadataIdentity));
        }

        foreach (var model in proxies)
        {
            Assert.Same(model, Assert.Single(reused.ReferencedProxyInterfaces,
                candidate => candidate.MetadataIdentity == model.MetadataIdentity));
        }

        TestContext.Current.SendDiagnosticMessage(
            $"{discoveredCount * 2} reused source models: {reusedBytes:N0} bytes allocated versus {scannedBytes:N0} bytes for full discovery.");
        Assert.True(reusedBytes < scannedBytes,
            $"Reuse allocated {reusedBytes:N0} bytes versus {scannedBytes:N0} bytes for full discovery.");

        ReferenceAssemblyModel Extract() => ReferenceAssemblyModelExtractor.ExtractReferenceAssemblyData(
            compilation, options, TestContext.Current.CancellationToken, out _, serializers, proxies);
    }

    [Fact]
    public async Task Generator_ReferenceReplacementWithSameIdentityRefreshesExternalDiscoveries()
    {
        const string librarySource = """
            using Orleans;
            namespace External;
            [GenerateSerializer, Id(42), Alias("original")]
            public class Dto { [Id(0)] public int Value { get; set; } }
            public interface IExternalGrain : IGrainWithIntegerKey
            {
                System.Threading.Tasks.Task<int> Get();
            }
            """;
        var library = await TestCompilationHelper.CreateCompilation(librarySource, "External");
        var changedLibrary = await TestCompilationHelper.CreateCompilation(
            librarySource.Replace("42", "43", StringComparison.Ordinal)
                .Replace("original", "changed", StringComparison.Ordinal)
                .Replace("public int Value", "public long Value", StringComparison.Ordinal)
                .Replace("Task<int>", "Task<long>", StringComparison.Ordinal), "External");
        var reference = library.ToMetadataReference();
        var changedReference = changedLibrary.ToMetadataReference();
        var compilation = await TestCompilationHelper.CreateCompilation(
            """
            using Orleans;
            [assembly: GenerateCodeForDeclaringAssembly(typeof(External.Dto))]
            [GenerateSerializer]
            public class SourceDto { [Id(0)] public int Value { get; set; } }
            """, additionalReferences: [reference]);
        var changed = compilation.ReplaceReference(reference, changedReference);
        GeneratorDriver driver = CreateTrackedDriver();
        driver = driver.RunGenerators(compilation, TestContext.Current.CancellationToken);
        var first = Assert.Single(driver.GetRunResult().Results);
        driver = driver.RunGenerators(changed, TestContext.Current.CancellationToken);
        var second = Assert.Single(driver.GetRunResult().Results);
        Assert.Empty(first.Diagnostics);
        Assert.Empty(second.Diagnostics);
        var before = GetTrackedOutput<ReferenceAssemblyDataResult>(first, OrleansSerializationSourceGenerator.ReferenceAssemblyDataTrackingName).Model;
        var after = GetTrackedOutput<ReferenceAssemblyDataResult>(second, OrleansSerializationSourceGenerator.ReferenceAssemblyDataTrackingName).Model;
        Assert.NotEqual(before, after);
        Assert.Equal(42u, Assert.Single(before.WellKnownTypeIds).Id);
        Assert.Equal(43u, Assert.Single(after.WellKnownTypeIds).Id);
        Assert.Equal("changed", Assert.Single(after.TypeAliases).Alias);
        Assert.Equal("long", Assert.Single(Assert.Single(after.ReferencedSerializableTypes,
            static model => model.Name == "Dto").Members).Type.SyntaxString);
        Assert.Equal(
            ReferenceAssemblyModelExtractor.ExtractReferenceAssemblyData(changed, new CodeGeneratorOptions(), TestContext.Current.CancellationToken),
            after);
        Assert.NotEqual(
            Assert.Single(first.GeneratedSources, static source => source.HintName.Contains(".orleans.proxy.", StringComparison.Ordinal)).SourceText.ToString(),
            Assert.Single(second.GeneratedSources, static source => source.HintName.Contains(".orleans.proxy.", StringComparison.Ordinal)).SourceText.ToString());
    }

    [Fact]
    public async Task Generator_PartialRemovalRefreshesReusedSourceModel()
    {
        var compilation = await TestCompilationHelper.CreateCompilation(
            """
            [Orleans.GenerateSerializer]
            public partial class Dto { [Orleans.Id(0)] public int First { get; set; } }
            """);
        var partial = CSharpSyntaxTree.ParseText(
            "public partial class Dto { [Orleans.Id(1)] public int Second { get; set; } }",
            cancellationToken: TestContext.Current.CancellationToken);
        compilation = compilation.AddSyntaxTrees(partial);
        var changed = compilation.RemoveSyntaxTrees(partial);
        GeneratorDriver driver = CreateTrackedDriver();
        driver = driver.RunGenerators(compilation, TestContext.Current.CancellationToken);
        var first = Assert.Single(driver.GetRunResult().Results);
        driver = driver.RunGenerators(changed, TestContext.Current.CancellationToken);
        var second = Assert.Single(driver.GetRunResult().Results);
        driver = driver.RunGenerators(compilation, TestContext.Current.CancellationToken);
        var restored = Assert.Single(driver.GetRunResult().Results);
        Assert.Empty(first.Diagnostics);
        Assert.Empty(second.Diagnostics);
        Assert.Empty(restored.Diagnostics);
        var before = GetTrackedOutput<ReferenceAssemblyDataResult>(first, OrleansSerializationSourceGenerator.ReferenceAssemblyDataTrackingName).Model;
        var after = GetTrackedOutput<ReferenceAssemblyDataResult>(second, OrleansSerializationSourceGenerator.ReferenceAssemblyDataTrackingName).Model;
        Assert.Equal(["First", "Second"], Assert.Single(before.ReferencedSerializableTypes).Members.Select(static member => member.BackingPropertyName));
        Assert.Equal("First", Assert.Single(Assert.Single(after.ReferencedSerializableTypes).Members).BackingPropertyName);
        Assert.Equal(
            ReferenceAssemblyModelExtractor.ExtractReferenceAssemblyData(changed, new CodeGeneratorOptions(), TestContext.Current.CancellationToken),
            after);
        Assert.NotEqual(
            Assert.Single(first.GeneratedSources, static source => source.HintName.Contains(".orleans.ser.", StringComparison.Ordinal)).SourceText.ToString(),
            Assert.Single(second.GeneratedSources, static source => source.HintName.Contains(".orleans.ser.", StringComparison.Ordinal)).SourceText.ToString());
        Assert.Equal(
            first.GeneratedSources.Select(static source => (source.HintName, source.SourceText.ToString())),
            restored.GeneratedSources.Select(static source => (source.HintName, source.SourceText.ToString())));
    }

    [Fact]
    public async Task Generator_UnsuccessfulSourceDiscoveryRetainsCurrentDiagnosticLocation()
    {
        const string source = """
            public class Container
            {
                [Orleans.GenerateSerializer]
                private class Hidden { [Orleans.Id(0)] public int Value { get; set; } }
            }
            """;
        var compilation = await TestCompilationHelper.CreateCompilation(source);
        var originalTree = compilation.SyntaxTrees.Single();
        var newTree = CSharpSyntaxTree.ParseText("\n" + source, cancellationToken: TestContext.Current.CancellationToken);
        var changed = compilation.ReplaceSyntaxTree(originalTree, newTree);
        GeneratorDriver driver = CreateTrackedDriver();
        driver = driver.RunGenerators(compilation, TestContext.Current.CancellationToken);
        var first = Assert.Single(driver.GetRunResult().Results);
        driver = driver.RunGenerators(changed, TestContext.Current.CancellationToken);
        var second = Assert.Single(driver.GetRunResult().Results);
        var originalDiagnostic = Assert.Single(first.Diagnostics);
        var currentDiagnostic = Assert.Single(second.Diagnostics);
        Assert.Equal(originalDiagnostic.Id, currentDiagnostic.Id);
        Assert.Equal(originalDiagnostic.GetMessage(), currentDiagnostic.GetMessage());
        Assert.Same(originalTree, originalDiagnostic.Location.SourceTree);
        Assert.Same(newTree, currentDiagnostic.Location.SourceTree);
        var referenceData = GetTrackedOutput<ReferenceAssemblyDataResult>(
            second, OrleansSerializationSourceGenerator.ReferenceAssemblyDataTrackingName);
        Assert.Empty(referenceData.Model.ReferencedSerializableTypes);
        Assert.Empty(referenceData.Diagnostics);
    }

    private static GeneratorDriver CreateTrackedDriver() => CSharpGeneratorDriver.Create(
        [new OrleansSerializationSourceGenerator().AsSourceGenerator()],
        optionsProvider: TestCompilationHelper.CreateOptionsProvider(),
        driverOptions: new GeneratorDriverOptions(disabledOutputs: default, trackIncrementalGeneratorSteps: true));

    private static GeneratorRunResult RunTrackedGenerator(CSharpCompilation compilation)
    {
        var driver = CreateTrackedDriver().RunGeneratorsAndUpdateCompilation(
            compilation, out var updated, out var diagnostics, TestContext.Current.CancellationToken);
        Assert.Empty(diagnostics);
        Assert.Empty(updated.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        return Assert.Single(driver.GetRunResult().Results);
    }

    private static T GetTrackedOutput<T>(GeneratorRunResult result, string name)
        => Assert.IsType<T>(Assert.Single(Assert.Single(result.TrackedSteps[name]).Outputs).Value);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExtractReferenceAssemblyData_PreservesSharedOpenGenericTypeMetadata(bool useMetadataReference)
    {
        const string source = """
            using Orleans;

            namespace MetadataTargets;

            public interface ITargetGrain : IGrainWithIntegerKey { }

            public class Outer<T>
            {
                [Id(37)]
                [Alias("nested")]
                [CompoundTypeAlias("first", typeof(Outer<>))]
                [CompoundTypeAlias("second", typeof(Outer<>))]
                public class @class<TItem> : Grain, ITargetGrain { }
            }

            [Id(38)]
            public class IdOnly<T> { }

            [Alias("alias-only")]
            public class AliasOnly<T> { }

            [CompoundTypeAlias("compound-only")]
            public class CompoundOnly<T> { }

            public class ImplementationOnly : Grain, ITargetGrain { }
            """;
        var compilation = await TestCompilationHelper.CreateCompilation(source, "MetadataTargets");
        Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        if (useMetadataReference)
        {
            using var stream = new MemoryStream();
            var emitResult = compilation.Emit(stream, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(emitResult.Success, string.Join(Environment.NewLine, emitResult.Diagnostics));
            compilation = await TestCompilationHelper.CreateCompilation(
                """
                using Orleans;
                [assembly: GenerateCodeForDeclaringAssembly(typeof(MetadataTargets.Outer<>))]
                """,
                "Consumer",
                MetadataReference.CreateFromImage(stream.ToArray()));
        }

        var model = ReferenceAssemblyModelExtractor.ExtractReferenceAssemblyData(
            compilation, new CodeGeneratorOptions(), TestContext.Current.CancellationToken, out var diagnostics);

        Assert.Empty(diagnostics);
        const string nestedType = "global::MetadataTargets.Outer<>.@class<>";
        Assert.Equal(
            [
                new WellKnownTypeIdModel(new TypeRef("global::MetadataTargets.IdOnly<>"), 38),
                new WellKnownTypeIdModel(new TypeRef(nestedType), 37),
            ],
            model.WellKnownTypeIds);
        Assert.Equal(
            [
                new TypeAliasModel(new TypeRef("global::MetadataTargets.AliasOnly<>"), "alias-only"),
                new TypeAliasModel(new TypeRef(nestedType), "nested"),
            ],
            model.TypeAliases);
        Assert.Equal(3, model.CompoundTypeAliases.Length);
        Assert.Equal(
            ["compound-only", "first", "second"],
            model.CompoundTypeAliases.Select(static alias => alias.Components[0].StringValue));
        Assert.Equal(
            ["global::MetadataTargets.CompoundOnly<>", nestedType, nestedType],
            model.CompoundTypeAliases.Select(static alias => alias.TargetType.SyntaxString));
        foreach (var alias in model.CompoundTypeAliases.Skip(1))
        {
            Assert.Equal(
                new CompoundAliasComponentModel(new TypeRef("global::MetadataTargets.Outer<>")),
                alias.Components[1]);
        }

        Assert.Equal(
            ["global::MetadataTargets.ImplementationOnly", nestedType],
            model.InterfaceImplementations.Select(static implementation => implementation.ImplementationType.SyntaxString));

        var nestedSyntax = model.WellKnownTypeIds[1].Type.SyntaxString;
        Assert.Same(nestedSyntax, model.TypeAliases[1].Type.SyntaxString);
        Assert.Same(nestedSyntax, model.CompoundTypeAliases[1].TargetType.SyntaxString);
        Assert.Same(nestedSyntax, model.CompoundTypeAliases[2].TargetType.SyntaxString);
        Assert.Same(nestedSyntax, model.InterfaceImplementations[1].ImplementationType.SyntaxString);
    }

    [Fact]
    public async Task ExtractReferenceAssemblyData_IrrelevantTypesAvoidEagerFormattingAllocations()
    {
        var source = new StringBuilder("namespace Irrelevant.Deeply.Nested.Namespace;\n");
        for (var i = 0; i < 256; i++)
        {
            source.Append("public class Outer").Append(i).Append("<T> { public class Inner<U, V> { } }\n");
        }

        var compilation = await TestCompilationHelper.CreateCompilation(source.ToString());
        var options = new CodeGeneratorOptions();
        var cancellationToken = TestContext.Current.CancellationToken;
        var types = compilation.Assembly.GetDeclaredTypes().ToArray();
        Assert.Equal(512, types.Length);

        // Warm Roslyn's symbol and syntax caches before comparing synchronous, thread-local allocations.
        _ = Extract();
        _ = FormatTypes();
        var start = GC.GetAllocatedBytesForCurrentThread();
        var model = Extract();
        var extractionBytes = GC.GetAllocatedBytesForCurrentThread() - start;
        start = GC.GetAllocatedBytesForCurrentThread();
        var formattedCharacters = FormatTypes();
        var formattingBytes = GC.GetAllocatedBytesForCurrentThread() - start;

        Assert.Empty(model.WellKnownTypeIds);
        Assert.Empty(model.TypeAliases);
        Assert.Empty(model.CompoundTypeAliases);
        Assert.Empty(model.InterfaceImplementations);
        Assert.Empty(model.RegisteredCodecs);
        Assert.Empty(model.ReferencedSerializableTypes);
        Assert.Empty(model.ReferencedProxyInterfaces);
        Assert.True(formattedCharacters > 0);
        TestContext.Current.SendDiagnosticMessage(
            $"512 irrelevant types: extraction allocated {extractionBytes:N0} bytes; eager formatting alone allocated {formattingBytes:N0} bytes.");
        Assert.True(extractionBytes < formattingBytes,
            $"Extraction allocated {extractionBytes:N0} bytes, exceeding eager formatting alone ({formattingBytes:N0} bytes).");

        ReferenceAssemblyModel Extract() => ReferenceAssemblyModelExtractor.ExtractReferenceAssemblyData(
            compilation, options, cancellationToken);

        int FormatTypes()
        {
            var characters = 0;
            foreach (var type in types)
            {
                characters += type.ToOpenTypeSyntax().ToString().Length;
            }

            return characters;
        }
    }

    [Fact]
    public async Task ExtractReferenceAssemblyData_PreservesFSharpRecordsAndUnionCases()
    {
        var compilation = await TestCompilationHelper.CreateCompilation(
            """
            using Orleans;
            [assembly: GenerateCodeForDeclaringAssembly(typeof(UnitTests.FSharpTypes.GenericDU<>))]
            """,
            additionalReferences:
            [
                MetadataReference.CreateFromFile(typeof(UnitTests.FSharpTypes.GenericDU<>).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Microsoft.FSharp.Core.FSharpOption<>).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(UnitTests.GrainInterfaces.INonGenericBase).Assembly.Location),
            ]);
        var model = ReferenceAssemblyModelExtractor.ExtractReferenceAssemblyData(
            compilation, new CodeGeneratorOptions(), TestContext.Current.CancellationToken, out var diagnostics);

        Assert.Empty(diagnostics);
        var record = Assert.Single(model.ReferencedSerializableTypes,
            static type => type.Name == "GenericRecord");
        Assert.Equal("UnitTests.FSharpTypes", record.Namespace);
        Assert.True(record.IsGenericType);
        Assert.Contains(record.Members, static member => member.FieldId == 1 && member.Name == "Value");
        Assert.Contains(model.ReferencedSerializableTypes,
            static type => type.Name == "GenericDU" && type.IsGenericType);
        Assert.Contains(model.ReferencedSerializableTypes,
            static type => type.MetadataIdentity.MetadataName == "UnitTests.FSharpTypes.GenericDU`1+Case1");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Generator_IrrelevantDeclarationsPreserveGeneratedSourcesAndDiagnostics(bool useMetadataReference)
    {
        const string source = """
            using Orleans;
            using System.Threading.Tasks;

            [assembly: RegisterProvider("test", "Clustering", "Client", typeof(MetadataTargets.Provider))]

            namespace MetadataTargets;

            public class Provider { }

            [GenerateSerializer, Id(42), Alias("dto"), CompoundTypeAlias("dto", typeof(string))]
            public class Dto<T>
            {
                [Id(0)] public T Value { get; set; }
            }

            public interface ITargetGrain : IGrainWithIntegerKey
            {
                Task<Dto<int>> Get();
            }

            public class TargetGrain : Grain, ITargetGrain
            {
                public Task<Dto<int>> Get() => Task.FromResult(new Dto<int>());
            }
            """;
        const string irrelevant = """

            public class Unused<T>
            {
                public class Nested<U> { }
            }
            internal abstract class UnusedBase { }
            public interface IUnused { }
            public enum UnusedEnum { Value }
            """;
        var original = await CreateCompilation(source);
        var changed = await CreateCompilation(source + irrelevant);
        var originalResult = RunGenerator(original);
        var changedResult = RunGenerator(changed);

        Assert.Empty(originalResult.Diagnostics);
        Assert.Equal(originalResult.Diagnostics, changedResult.Diagnostics);
        Assert.NotEmpty(originalResult.GeneratedSources);
        Assert.Equal(
            originalResult.GeneratedSources.Select(static generated => (generated.HintName, generated.SourceText.ToString())),
            changedResult.GeneratedSources.Select(static generated => (generated.HintName, generated.SourceText.ToString())));

        async Task<CSharpCompilation> CreateCompilation(string code)
        {
            var compilation = await TestCompilationHelper.CreateCompilation(
                code,
                "MetadataTargets",
                MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.Options.IConfigureOptions<>).Assembly.Location));
            if (!useMetadataReference)
            {
                return compilation;
            }

            using var stream = new MemoryStream();
            var emitResult = compilation.Emit(stream, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(emitResult.Success, string.Join(Environment.NewLine, emitResult.Diagnostics));
            return await TestCompilationHelper.CreateCompilation(
                """
                using Orleans;
                [assembly: GenerateCodeForDeclaringAssembly(typeof(MetadataTargets.Provider))]
                """,
                "Consumer",
                MetadataReference.CreateFromImage(stream.ToArray()));
        }

        static GeneratorRunResult RunGenerator(CSharpCompilation compilation)
        {
            GeneratorDriver driver = CSharpGeneratorDriver.Create(
                [new OrleansSerializationSourceGenerator().AsSourceGenerator()],
                optionsProvider: TestCompilationHelper.CreateOptionsProvider());
            driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out var diagnostics,
                TestContext.Current.CancellationToken);
            Assert.Empty(diagnostics);
            Assert.Empty(updated.GetDiagnostics(TestContext.Current.CancellationToken)
                .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
            return Assert.Single(driver.GetRunResult().Results);
        }
    }
}
