using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.Model;

namespace Orleans.CodeGenerator.Tests;

public class ManifestMetadataTests
{
    private const string Source = """
        using System;
        using System.Buffers;
        using Orleans;
        using Orleans.Serialization.Activators;
        using Orleans.Serialization.Buffers;
        using Orleans.Serialization.Cloning;
        using Orleans.Serialization.Serializers;

        namespace MetadataTargets;

        public class Outer<T>
        {
            public class Nested<TItem> : IComparable<Tuple<TItem, T>>
            {
                public int CompareTo(Tuple<TItem, T> other) => 0;
            }
        }

        public class Target<T> { }
        public struct Surrogate<T> { }

        [RegisterSerializer]
        public class Codec<T> : IBaseCodec<Outer<T>.Nested<Target<int>>>
        {
            public void Serialize<TBufferWriter>(ref Writer<TBufferWriter> writer, Outer<T>.Nested<Target<int>> value)
                where TBufferWriter : IBufferWriter<byte> { }
            public void Deserialize<TInput>(ref Reader<TInput> reader, Outer<T>.Nested<Target<int>> value) { }
        }

        [RegisterCopier]
        public class Copier<T> : ShallowCopier<Outer<T>.Nested<Target<int>>> { }

        [RegisterActivator]
        public class Activator<T> : IActivator<Outer<T>.Nested<Target<int>>>
        {
            public Outer<T>.Nested<Target<int>> Create() => new();
        }

        [RegisterConverter]
        public class Converter<T> : IConverter<Outer<T>.Nested<Target<int>>, Surrogate<Target<T>[]>>
        {
            public Outer<T>.Nested<Target<int>> ConvertFromSurrogate(in Surrogate<Target<T>[]> value) => new();
            public Surrogate<Target<T>[]> ConvertToSurrogate(in Outer<T>.Nested<Target<int>> value) => default;
        }

        [GenerateSerializer]
        public class Generated<T> : IComparable<Outer<T>.Nested<Target<int>>>
        {
            public int CompareTo(Outer<T>.Nested<Target<int>> other) => 0;
        }
        """;

    [Theory]
    [InlineData("Codec")]
    [InlineData("Copier")]
    [InlineData("Activator")]
    [InlineData("Converter")]
    public async Task RegisteredImplementationsPreserveTargetMetadata(string name)
    {
        var compilation = await TestCompilationHelper.CreateCompilation(Source);
        var symbol = compilation.GetTypeByMetadataName($"MetadataTargets.{name}`1");
        Assert.NotNull(symbol);

        var kind = name switch
        {
            "Codec" => RegisteredCodecKind.Serializer,
            "Copier" => RegisteredCodecKind.Copier,
            "Activator" => RegisteredCodecKind.Activator,
            "Converter" => RegisteredCodecKind.Converter,
            _ => throw new InvalidOperationException($"Unexpected registration: {name}.")
        };
        var model = ModelExtractor.ExtractRegisteredCodec(symbol, kind);
        var names = model.MetadataTypes.Select(static type => type.MetadataName).ToArray();

        Assert.Contains("MetadataTargets.Outer`1", names);
        Assert.Contains("MetadataTargets.Outer`1+Nested`1", names);
        Assert.Contains("MetadataTargets.Target`1", names);
        Assert.Contains("System.Int32", names);
        Assert.Contains("System.Tuple`2", names);
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(names, static name => name.Contains("TItem", StringComparison.Ordinal));

        if (kind == RegisteredCodecKind.Converter)
        {
            Assert.Contains("MetadataTargets.Surrogate`1", names);
        }
    }

    [Fact]
    public async Task GeneratedManifestEmitsDeterministicAnnotatedMetadataRoots()
    {
        var compilation = await TestCompilationHelper.CreateCompilation(Source);
        var result = RunGenerator(compilation, out var updatedCompilation);
        Assert.Empty(updatedCompilation.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        var metadata = GetMetadata(result);
        var roots = metadata.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(static invocation => invocation.Expression.ToString() == "PreserveTypeMetadata")
            .Select(static invocation => Assert.IsType<LiteralExpressionSyntax>(Assert.Single(invocation.ArgumentList.Arguments).Expression).Token.ValueText)
            .ToArray();

        Assert.Contains("MetadataTargets.Outer`1+Nested`1, TestProject", roots);
        Assert.Contains("MetadataTargets.Target`1, TestProject", roots);
        Assert.Contains("MetadataTargets.Surrogate`1, TestProject", roots);
        Assert.Contains("MetadataTargets.Generated`1, TestProject", roots);
        Assert.Equal(roots.Length, roots.Distinct(StringComparer.Ordinal).Count());

        var helper = Assert.Single(metadata.DescendantNodes().OfType<MethodDeclarationSyntax>(),
            static method => method.Identifier.ValueText == "PreserveTypeMetadata");
        var attribute = Assert.Single(Assert.Single(helper.ParameterList.Parameters).AttributeLists).Attributes.Single();
        Assert.NotNull(attribute.ArgumentList);
        Assert.Equal("global::System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembersAttribute", attribute.Name.ToString());
        Assert.Equal("global::System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.Interfaces",
            Assert.Single(attribute.ArgumentList.Arguments).Expression.ToString());

        var reordered = compilation.ReplaceSyntaxTree(compilation.SyntaxTrees.Single(),
            CSharpSyntaxTree.ParseText(Source.Replace(
                "[RegisterSerializer]", "[RegisterSerializer]\n", StringComparison.Ordinal),
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(metadata.ToString(), GetMetadata(RunGenerator(reordered, out _)).ToString());
    }

    [Fact]
    public async Task ChangingImplementedInterfaceInvalidatesMetadataModel()
    {
        var compilation = await TestCompilationHelper.CreateCompilation(Source);
        var symbol = compilation.GetTypeByMetadataName("MetadataTargets.Codec`1");
        Assert.NotNull(symbol);
        var original = ModelExtractor.ExtractRegisteredCodec(symbol, RegisteredCodecKind.Serializer);

        var changedCompilation = await TestCompilationHelper.CreateCompilation(
            Source.Replace("Target<int>", "Target<string>", StringComparison.Ordinal));
        var changedSymbol = changedCompilation.GetTypeByMetadataName("MetadataTargets.Codec`1");
        Assert.NotNull(changedSymbol);
        var changed = ModelExtractor.ExtractRegisteredCodec(changedSymbol, RegisteredCodecKind.Serializer);

        Assert.NotEqual(original, changed);
        Assert.Contains(changed.MetadataTypes, static type => type.MetadataName == "System.String");
    }

    [Fact]
    public async Task MetadataRootEmissionUsesAssemblyQualifiedNamesForInaccessibleTypes()
    {
        const string source = """
            using Orleans;
            using Orleans.Serialization.Cloning;
            public class Container
            {
                private class Hidden<T> { }
                [RegisterCopier]
                internal class Copier : IDeepCopier<Hidden<int>>
                {
                    Hidden<int> IDeepCopier<Hidden<int>>.DeepCopy(Hidden<int> input, CopyContext context) => input;
                }
            }
            """;
        var compilation = await TestCompilationHelper.CreateCompilation(source);
        var result = RunGenerator(compilation, out var updated);

        Assert.Empty(updated.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Contains("PreserveTypeMetadata(\"Container+Hidden`1, TestProject\")", GetMetadata(result).ToString());
    }

    private static GeneratorRunResult RunGenerator(CSharpCompilation compilation, out Compilation updated)
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new OrleansSerializationSourceGenerator().AsSourceGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out updated, out var diagnostics,
            TestContext.Current.CancellationToken);
        Assert.Empty(diagnostics);
        return Assert.Single(driver.GetRunResult().Results);
    }

    private static CompilationUnitSyntax GetMetadata(GeneratorRunResult result)
        => Assert.Single(result.GeneratedSources.Select(static source => CSharpSyntaxTree.ParseText(source.SourceText,
                cancellationToken: TestContext.Current.CancellationToken).GetCompilationUnitRoot(TestContext.Current.CancellationToken)),
            static root => root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Any(static type => type.Identifier.ValueText.StartsWith("Metadata_", StringComparison.Ordinal)));
}
