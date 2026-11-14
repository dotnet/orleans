using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Orleans.CodeGenerator.Tests;

public sealed class CompoundTypeAliasTests
{
    [Fact]
    public void RequiredPrivateArgumentAliasesProduceClearGeneratorErrors()
    {
        var (compilation, result) = Generate("""
            public interface ITag<T> { }
            [Orleans.GenerateSerializer]
            public sealed class Payload : ITag<Payload.Hidden>
            {
                [Orleans.CompoundTypeAlias("required-private-alias")]
                private sealed class Hidden { }
                [Orleans.Id(0)] public int Value { get; set; }
            }
            """);

        Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("ORLEANS0116", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("Hidden", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            result.Results.SelectMany(static result => result.GeneratedSources),
            static source => source.SourceText.ToString().Contains("required-private-alias", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("public sealed class Outer { [Orleans.CompoundTypeAlias(\"hidden-alias\")] private sealed class Hidden { } }", "Outer.Hidden")]
    [InlineData("public class Outer { [Orleans.CompoundTypeAlias(\"hidden-alias\")] protected sealed class Hidden { } }", "Outer.Hidden")]
    [InlineData("public class Outer { [Orleans.CompoundTypeAlias(\"hidden-alias\")] private protected sealed class Hidden { } }", "Outer.Hidden")]
    [InlineData("public sealed class Outer { private sealed class Wrapper { [Orleans.CompoundTypeAlias(\"hidden-alias\")] public sealed class Hidden { } } }", "Outer.Wrapper.Hidden")]
    [InlineData("public sealed class Outer { private sealed class Key { } [Orleans.CompoundTypeAlias(\"hidden-alias\", typeof(Key))] public sealed class Target { } }", "Outer.Key")]
    [InlineData("public sealed class Outer { private sealed class Key<T> { } [Orleans.CompoundTypeAlias(\"hidden-alias\", typeof(Key<>))] public sealed class Target { } }", "Outer.Key<>")]
    [InlineData("[Orleans.CompoundTypeAlias(\"hidden-alias\")] file sealed class Hidden { }", "Hidden")]
    [InlineData("file sealed class Outer { [Orleans.CompoundTypeAlias(\"hidden-alias\")] public sealed class Hidden { } }", "Outer.Hidden")]
    [InlineData("file sealed class Key { } [Orleans.CompoundTypeAlias(\"hidden-alias\", typeof(Key))] public sealed class Target { }", "Key")]
    [InlineData("file sealed class Key { } [Orleans.CompoundTypeAlias(\"hidden-alias\", typeof(Key[]))] public sealed class Target { }", "Key[]")]
    [InlineData("file sealed class Key { } [Orleans.CompoundTypeAlias(\"hidden-alias\", typeof(System.Collections.Generic.List<Key>))] public sealed class Target { }", "Key")]
    public void InaccessibleCompoundAliasesAreDiagnosedAndOmittedFromMetadata(string source, string inaccessibleType)
    {
        var (compilation, result) = Generate(source);
        Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.DoesNotContain(
            result.Results.SelectMany(static result => result.GeneratedSources),
            static source => source.SourceText.ToString().Contains("hidden-alias", StringComparison.Ordinal));
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("ORLEANS0116", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains(inaccessibleType, diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.NotNull(diagnostic.Location.SourceTree);
        Assert.StartsWith("Orleans.CompoundTypeAlias(", diagnostic.Location.SourceTree.GetText(TestContext.Current.CancellationToken)
            .ToString(diagnostic.Location.SourceSpan), StringComparison.Ordinal);
    }

    [Fact]
    public void ReferencedInaccessibleCompoundAliasesProduceConsumerDiagnostics()
    {
        var library = CreateCompilation("""
            namespace Producer;
            public sealed class Marker
            {
                [Orleans.CompoundTypeAlias("hidden-alias")]
                private sealed class Hidden { }
            }
            """).WithAssemblyName("Producer");
        var consumer = CreateCompilation("""
            [assembly: Orleans.GenerateCodeForDeclaringAssembly(typeof(Producer.Marker))]
            """).AddReferences(library.ToMetadataReference());
        var (compilation, result) = Generate(consumer);

        Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("ORLEANS0116", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("Producer.Marker.Hidden", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Equal(LocationKind.ExternalFile, diagnostic.Location.Kind);
        Assert.DoesNotContain(
            result.Results.SelectMany(static result => result.GeneratedSources),
            static source => source.SourceText.ToString().Contains("hidden-alias", StringComparison.Ordinal));
    }

    [Fact]
    public void AccessibleCompoundAliasTargetsRemainRegisteredInMetadata()
    {
        var (compilation, result) = Generate("""
            [Orleans.CompoundTypeAlias("public-alias")]
            public sealed class PublicTarget { }
            [Orleans.CompoundTypeAlias("internal-alias")]
            internal sealed class InternalTarget { }
            [Orleans.CompoundTypeAlias("generic-alias", typeof(InternalTarget[]), typeof(System.Collections.Generic.List<InternalTarget>))]
            public sealed class GenericTarget<T> { }
            public class Outer
            {
                [Orleans.CompoundTypeAlias("nested-internal-alias")]
                internal sealed class Nested { }
                [Orleans.CompoundTypeAlias("protected-internal-alias")]
                protected internal sealed class ProtectedInternal { }
            }
            """);
        Assert.Empty(result.Diagnostics);
        Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var generated = string.Concat(result.Results.SelectMany(static result => result.GeneratedSources)
            .Select(static source => source.SourceText.ToString()));
        foreach (var alias in new[] { "public-alias", "internal-alias", "generic-alias", "nested-internal-alias", "protected-internal-alias" })
        {
            Assert.Contains($"\"{alias}\"", generated, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("System.Collections.Generic.List<>")]
    [InlineData("System.Collections.Generic.Dictionary<, >")]
    [InlineData("Container<>.Key<>")]
    public void OpenGenericAliasKeysRemainRegistered(string keyType)
    {
        var (compilation, result) = Generate($$"""
            [Orleans.CompoundTypeAlias("open-generic-alias", typeof({{keyType}}))]
            public sealed class Target { }
            public sealed class Container<T>
            {
                public sealed class Key<U> { }
            }
            """);

        Assert.Empty(result.Diagnostics);
        Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var metadata = Assert.Single(Assert.Single(result.Results).GeneratedSources,
            static source => source.HintName.EndsWith(".orleans.metadata.g.cs", StringComparison.Ordinal));
        Assert.Contains("\"open-generic-alias\"", metadata.SourceText.ToString(), StringComparison.Ordinal);
        Assert.Contains($"typeof(global::{keyType})", metadata.SourceText.ToString(), StringComparison.Ordinal);
        Assert.Contains("typeof(global::Target)", metadata.SourceText.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReferencedInternalCompoundAliasesHonorInternalsVisibleTo(bool internalsVisible)
    {
        var library = CreateCompilation($$"""
            {{(internalsVisible ? "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"CompoundAliasTests\")]" : "")}}
            namespace Producer;
            public sealed class Marker { }
            internal sealed class Key { }
            [Orleans.CompoundTypeAlias("internal-alias", typeof(Key))]
            internal sealed class Target { }
            """).WithAssemblyName("Producer");
        var consumer = CreateCompilation("""
            [assembly: Orleans.GenerateCodeForDeclaringAssembly(typeof(Producer.Marker))]
            """).AddReferences(library.ToMetadataReference());
        var (compilation, result) = Generate(consumer);

        Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var generated = string.Concat(result.Results.SelectMany(static result => result.GeneratedSources)
            .Select(static source => source.SourceText.ToString()));
        if (internalsVisible)
        {
            Assert.Empty(result.Diagnostics);
            Assert.Contains("\"internal-alias\"", generated, StringComparison.Ordinal);
            Assert.Contains("typeof(global::Producer.Key)", generated, StringComparison.Ordinal);
            Assert.Contains("typeof(global::Producer.Target)", generated, StringComparison.Ordinal);
        }
        else
        {
            var diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal("ORLEANS0116", diagnostic.Id);
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
            Assert.Equal(
                "The compound type alias on global::Producer.Target cannot be registered because the type global::Producer.Target is inaccessible from generated code",
                diagnostic.GetMessage());
            Assert.DoesNotContain("\"internal-alias\"", generated, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(IncrementalGeneratorOutputKind.None)]
    [InlineData(IncrementalGeneratorOutputKind.Implementation)]
    public void CompoundAliasDiagnosticsFollowImplementationOutput(IncrementalGeneratorOutputKind disabledOutputs)
    {
        var compilation = CreateCompilation("""
            public sealed class Outer
            {
                [Orleans.CompoundTypeAlias("hidden-alias")]
                private sealed class Hidden { }
            }
            """);
        var (output, result) = Generate(compilation, disabledOutputs);

        Assert.Empty(output.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.NotEmpty(Assert.Single(result.Results).GeneratedSources);
        Assert.DoesNotContain(
            result.Results.SelectMany(static result => result.GeneratedSources),
            static source => source.SourceText.ToString().Contains("hidden-alias", StringComparison.Ordinal));
        if (disabledOutputs == IncrementalGeneratorOutputKind.Implementation)
        {
            Assert.Empty(result.Diagnostics);
        }
        else
        {
            Assert.Equal("ORLEANS0116", Assert.Single(result.Diagnostics).Id);
        }
    }

    [Fact]
    public void CompoundAliasDiagnosticsAndMetadataTrackAccessibilityChanges()
    {
        const string source = """
            public sealed class Outer
            {
                [Orleans.CompoundTypeAlias("tracked-alias")]
                private sealed class Target { }
            }
            """;
        var compilation = CreateCompilation(source);
        var parseOptions = (CSharpParseOptions)compilation.SyntaxTrees.First().Options;
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new OrleansSerializationSourceGenerator().AsSourceGenerator()],
            parseOptions: parseOptions,
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGenerators(compilation, TestContext.Current.CancellationToken);
        var first = Assert.Single(driver.GetRunResult().Results);
        Assert.Equal("ORLEANS0116", Assert.Single(first.Diagnostics).Id);

        var unrelatedChange = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(
            "public sealed class Unrelated { }", parseOptions, cancellationToken: TestContext.Current.CancellationToken));
        driver = driver.RunGenerators(unrelatedChange, TestContext.Current.CancellationToken);
        var second = Assert.Single(driver.GetRunResult().Results);
        Assert.Equal(first.Diagnostics.Select(static diagnostic => diagnostic.ToString()),
            second.Diagnostics.Select(static diagnostic => diagnostic.ToString()));
        Assert.Equal(first.GeneratedSources.Select(static source => (source.HintName, source.SourceText.ToString())),
            second.GeneratedSources.Select(static source => (source.HintName, source.SourceText.ToString())));
        Assert.All(second.TrackedSteps[OrleansSerializationSourceGenerator.ReferenceAssemblyDataTrackingName]
            .SelectMany(static step => step.Outputs),
            static entry => Assert.Equal(IncrementalStepRunReason.Unchanged, entry.Reason));

        var accessible = CreateCompilation(source.Replace("private sealed", "internal sealed", StringComparison.Ordinal));
        driver = driver.RunGeneratorsAndUpdateCompilation(accessible, out var output, out _, TestContext.Current.CancellationToken);
        var third = Assert.Single(driver.GetRunResult().Results);
        Assert.Empty(third.Diagnostics);
        Assert.Empty(output.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var metadata = Assert.Single(third.GeneratedSources,
            static source => source.HintName.EndsWith(".orleans.metadata.g.cs", StringComparison.Ordinal));
        Assert.Contains("\"tracked-alias\"", metadata.SourceText.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SameFileAliasEditsRefreshDiagnosticSyntaxTrees()
    {
        const string source = """
            public sealed class Outer
            {
                [Orleans.CompoundTypeAlias("old-alias")]
                private sealed class Target { }
            }
            """;
        var compilation = CreateCompilation(source);
        var parseOptions = (CSharpParseOptions)compilation.SyntaxTrees.First().Options;
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new OrleansSerializationSourceGenerator().AsSourceGenerator()], parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out _, TestContext.Current.CancellationToken);
        Assert.Equal("ORLEANS0116", Assert.Single(driver.GetRunResult().Diagnostics).Id);

        var edited = CreateCompilation(source.Replace("old-alias", "new-longer-alias", StringComparison.Ordinal));
        driver = driver.RunGeneratorsAndUpdateCompilation(edited, out var output, out _, TestContext.Current.CancellationToken);
        var diagnostic = Assert.Single(driver.GetRunResult().Diagnostics);
        Assert.Equal("ORLEANS0116", diagnostic.Id);
        var editedTree = Assert.Single(edited.SyntaxTrees);
        Assert.Same(editedTree, diagnostic.Location.SourceTree);
        Assert.Equal("Orleans.CompoundTypeAlias(\"new-longer-alias\")",
            editedTree.GetText(TestContext.Current.CancellationToken).ToString(diagnostic.Location.SourceSpan));
        Assert.Empty(output.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void CompoundAliasDiagnosticLocationChangesInvalidateReferenceAssemblyData()
    {
        const string source = """
            public sealed class Outer
            {
                [Orleans.CompoundTypeAlias("tracked-alias")]
                private sealed class Target { }
            }
            """;
        var compilation = CreateCompilation(source);
        var options = new CodeGeneratorOptions();
        var first = ReferenceAssemblyModelExtractor.ExtractReferenceAssemblyData(
            compilation, options, TestContext.Current.CancellationToken, out var diagnostics, out var implementationDiagnostics);
        var original = ReferenceAssemblyDataResult.FromModelAndDiagnostics(first, diagnostics, implementationDiagnostics);
        var withoutDiagnostic = ReferenceAssemblyDataResult.FromModelAndDiagnostics(first, diagnostics);
        Assert.NotEqual(original, withoutDiagnostic);

        var changed = ReferenceAssemblyModelExtractor.ExtractReferenceAssemblyData(
            CreateCompilation("\n" + source), options, TestContext.Current.CancellationToken, out diagnostics, out implementationDiagnostics);
        var moved = ReferenceAssemblyDataResult.FromModelAndDiagnostics(changed, diagnostics, implementationDiagnostics);
        Assert.Equal(first, changed);
        Assert.NotEqual(original, moved);
        Assert.Equal(Assert.Single(original.ImplementationDiagnostics).Location.SourceSpan.Start + 1,
            Assert.Single(moved.ImplementationDiagnostics).Location.SourceSpan.Start);
    }

    private static (Compilation Compilation, GeneratorDriverRunResult Result) Generate(string source)
        => Generate(CreateCompilation(source));

    private static CSharpCompilation CreateCompilation(string source)
    {
        var references = TestCompilationHelper.CreateCompilation(source, "CompoundAliasTests").GetAwaiter().GetResult().References;
        return CSharpCompilation.Create("CompoundAliasTests",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview),
                cancellationToken: TestContext.Current.CancellationToken)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static (Compilation Compilation, GeneratorDriverRunResult Result) Generate(
        CSharpCompilation compilation,
        IncrementalGeneratorOutputKind disabledOutputs = IncrementalGeneratorOutputKind.None)
    {
        var parseOptions = (CSharpParseOptions)compilation.SyntaxTrees.First().Options;
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new OrleansSerializationSourceGenerator().AsSourceGenerator()], parseOptions: parseOptions,
            driverOptions: new GeneratorDriverOptions(disabledOutputs));
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _, TestContext.Current.CancellationToken);
        return (output, driver.GetRunResult());
    }
}
