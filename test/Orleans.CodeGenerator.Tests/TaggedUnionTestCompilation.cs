using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Orleans.CodeGenerator.Tests;

/// <summary>
/// Runs the Orleans serialization generator over tagged-union test sources using the Roslyn version referenced by the test target.
/// </summary>
internal static class TaggedUnionTestCompilation
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

    public static ClassDeclarationSyntax GetCodec(GeneratorDriverRunResult result, string className)
        => Assert.Single(result.Results.SelectMany(static generator => generator.GeneratedSources)
            .SelectMany(source => source.SyntaxTree.GetRoot(TestContext.Current.CancellationToken).DescendantNodes().OfType<ClassDeclarationSyntax>()),
            declaration => declaration.Identifier.Text == className);

    public static void AssertNoErrors(Compilation compilation)
        => Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken).Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

    public static (Compilation Output, GeneratorDriverRunResult Result) Generate(string source, bool hotReload = false, params MetadataReference[] additionalReferences)
    {
        source = $"#nullable enable\nusing Orleans;\n{source}";
        var references = TestCompilationHelper.CreateCompilation(source, "TaggedUnionDiagnosticTests",
            [MetadataReference.CreateFromFile(typeof(ServiceProvider).Assembly.Location), .. additionalReferences]).GetAwaiter().GetResult().References;
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(
            "TaggedUnionDiagnosticTests",
            [CSharpSyntaxTree.ParseText(source, parseOptions), CSharpSyntaxTree.ParseText(Polyfills, parseOptions)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new OrleansSerializationSourceGenerator().AsSourceGenerator()],
            parseOptions: parseOptions,
            optionsProvider: TestCompilationHelper.CreateOptionsProvider(new Dictionary<string, string> { ["build_property.orleanshotreload"] = hotReload.ToString() }));
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _, TestContext.Current.CancellationToken);
        return (output, driver.GetRunResult());
    }
}
