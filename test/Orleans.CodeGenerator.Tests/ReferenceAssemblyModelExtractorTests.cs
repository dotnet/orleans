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
            compilation, new CodeGeneratorOptions(), TestContext.Current.CancellationToken, out var diagnostics, out var implementationDiagnostics);

        Assert.Empty(diagnostics);
        Assert.Empty(implementationDiagnostics);
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
            compilation, new CodeGeneratorOptions(), TestContext.Current.CancellationToken, out var diagnostics, out var implementationDiagnostics);

        Assert.Empty(diagnostics);
        Assert.Empty(implementationDiagnostics);
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
