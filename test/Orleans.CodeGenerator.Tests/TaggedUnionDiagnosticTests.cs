// net10.0 tests reference Roslyn 5.9, which parses closed types and unions; net8.0 tests reference the oldest supported Roslyn.
#if NET10_0_OR_GREATER
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Orleans.CodeGenerator.Tests;

/// <summary>
/// Tests the source generator's ORLEANS0116 diagnostics for closed type hierarchies and union types.
/// </summary>
/// <remarks>
/// These tests compile closed and union types with <see cref="LanguageVersion.Preview"/>. Union diagnostics which depend on union case types
/// require a compiler which exposes them, so the tests cover the union diagnostics which are available with the referenced compiler.
/// </remarks>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("CodeGen")]
public sealed class TaggedUnionDiagnosticTests
{
    // The compiler requires these types to declare closed types and unions; they are part of the base class library starting with .NET 11.
    private const string Polyfills = """
        namespace System.Runtime.CompilerServices
        {
            [AttributeUsage(AttributeTargets.Class, Inherited = false)]
            internal sealed class IsClosedTypeAttribute : Attribute { public Type[]? DerivedTypes { get; set; } }

            [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
            internal sealed class UnionAttribute : Attribute { }

            internal interface IUnion { object? Value { get; } }
        }
        """;

    [Fact]
    public void ValidClosedHierarchyGeneratesCompilableCodec()
    {
        var (output, result) = Generate("""
            [GenerateSerializer] public closed record Shape<T>;
            [GenerateSerializer, Case(0)] public sealed record Circle<T>([property: Id(0)] T Radius) : Shape<T>;
            [GenerateSerializer, Case(1)] public sealed record Square<X>([property: Id(0)] X Side) : Shape<X>;
            """);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Id == "ORLEANS0116");
        AssertGeneratesTaggedUnionCodec(result);
        AssertNoErrors(output);
    }

    [Fact]
    public void NullabilityConstraintsOfCasesDoNotAffectGeneratedCodec()
    {
        // Nullability is not part of a type's runtime identity, and generated code has no nullable context, so these cases are supported.
        var (output, result) = Generate("""
            [GenerateSerializer] public closed record NotNullBase<T>;
            [GenerateSerializer, Case(0)] public sealed record NotNullCase<T>([property: Id(0)] T Value) : NotNullBase<T> where T : notnull;
            [GenerateSerializer] public closed record NullableClassBase<T> where T : class?;
            [GenerateSerializer, Case(0)] public sealed record ClassCase<T>([property: Id(0)] T Value) : NullableClassBase<T> where T : class;
            """);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Id == "ORLEANS0116");
        AssertGeneratesTaggedUnionCodec(result);
        AssertNoErrors(output);
    }

    [Theory]
    [InlineData(
        """
        [GenerateSerializer] public closed record Base;
        [GenerateSerializer, Case(0)] public sealed record First : Base;
        [GenerateSerializer] public sealed record Second : Base;
        """,
        "'Second' derives from closed type 'Base', which uses case identifiers")]
    [InlineData(
        """
        [GenerateSerializer] public closed record Base;
        [GenerateSerializer, Case(1)] public sealed record First : Base;
        [GenerateSerializer, Case(1)] public sealed record Second : Base;
        """,
        "Case identifier 1 is used by both 'First' and 'Second' in 'Base'")]
    [InlineData(
        """
        [GenerateSerializer] public closed record Shape<T>;
        [GenerateSerializer, Case(0)] public sealed record IntSquare([property: Id(0)] int Side) : Shape<int>;
        """,
        "Case 'IntSquare' must pass its own type parameters as the type arguments of 'Shape<T>'")]
    [InlineData(
        """
        [GenerateSerializer] public closed record Shape<T>;
        [GenerateSerializer, Case(0)] public sealed record ListShape<U>([property: Id(0)] U Item) : Shape<System.Collections.Generic.List<U>>;
        """,
        "Case 'ListShape<U>' must pass its own type parameters as the type arguments of 'Shape<T>'")]
    [InlineData(
        """
        [GenerateSerializer] public closed record Shape<T>;
        [GenerateSerializer, Case(0)] public sealed record RefShape<T>([property: Id(0)] T Item) : Shape<T> where T : class;
        """,
        "Case 'RefShape<T>' has type parameter constraints which closed type 'Shape<T>' does not declare")]
    [InlineData(
        """
        public static class Outer
        {
            [GenerateSerializer] public closed record Base;
            [GenerateSerializer, Case(0)] private sealed record Hidden : Base;
        }
        """,
        "Case type 'Outer.Hidden' of 'Outer.Base' is not accessible to generated code")]
    [InlineData(
        """
        public sealed record Cat;
        public sealed record Dog;
        [GenerateSerializer] public union Pet(Cat, Dog);
        """,
        "The compiler does not expose the case types of union type 'Pet' to source generators")]
    public void InvalidCasesProduceGeneratorErrors(string source, string expectedMessage)
    {
        var (_, result) = Generate(source);

        var diagnostic = Assert.Single(result.Diagnostics, static diagnostic => diagnostic.Id == "ORLEANS0116");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains(expectedMessage, diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    private static void AssertGeneratesTaggedUnionCodec(GeneratorDriverRunResult result)
        => Assert.Contains(result.Results.SelectMany(static generator => generator.GeneratedSources), static source => source.SourceText.ToString().Contains("WriteStartUnion", StringComparison.Ordinal));

    private static void AssertNoErrors(Compilation compilation)
        => Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken).Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

    private static (Compilation Output, GeneratorDriverRunResult Result) Generate(string source)
    {
        source = $"#nullable enable\nusing Orleans;\n{source}";
        var references = TestCompilationHelper.CreateCompilation(source, "TaggedUnionDiagnosticTests").GetAwaiter().GetResult().References;
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(
            "TaggedUnionDiagnosticTests",
            [CSharpSyntaxTree.ParseText(source, parseOptions), CSharpSyntaxTree.ParseText(Polyfills, parseOptions)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create([new OrleansSerializationSourceGenerator().AsSourceGenerator()], parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _, TestContext.Current.CancellationToken);
        return (output, driver.GetRunResult());
    }
}
#endif
