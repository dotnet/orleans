using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace Orleans.CodeGenerator.Tests;

public class HotReloadCodegenTests
{
    private const string HotReloadProperty = "build_property.orleans_hotreload";

    private const string GreetingV1 = """
        using System.Collections.Generic;
        using Orleans;

        namespace TestProject;

        [GenerateSerializer]
        public sealed record Greeting
        {
            [Id(0)] public string Message { get; init; } = "";
            [Id(1)] public int Count { get; init; }
        }
        """;

    private const string GreetingV2 = """
        using System.Collections.Generic;
        using Orleans;

        namespace TestProject;

        [GenerateSerializer]
        public sealed record Greeting
        {
            [Id(0)] public string Message { get; init; } = "";
            [Id(1)] public int Count { get; init; }
            [Id(2)] public int Extra { get; init; }
            [Id(3)] public List<int>? Tags { get; init; }
            [Id(4)] public Detail? Detail { get; init; }
        }

        [GenerateSerializer]
        public sealed class Detail
        {
            [Id(0)] public string Name { get; set; } = "";
        }
        """;

    [Theory]
    [InlineData("Codec_Greeting")]
    [InlineData("Copier_Greeting")]
    public async Task AddingMembersOnlyAddsGeneratedMembers(string className)
    {
        var before = GetClass(await Generate(GreetingV1, OptimizationLevel.Debug), className);
        var after = GetClass(await Generate(GreetingV2, OptimizationLevel.Debug), className);

        var membersAfter = GetMemberSignatures(after).ToHashSet(StringComparer.Ordinal);
        foreach (var member in GetMemberSignatures(before))
        {
            Assert.Contains(member, membersAfter);
        }

        Assert.Equal(
            before.Members.OfType<ConstructorDeclarationSyntax>().Single().ParameterList.NormalizeWhitespace().ToString(),
            after.Members.OfType<ConstructorDeclarationSyntax>().Single().ParameterList.NormalizeWhitespace().ToString());
    }

    [Fact]
    public async Task DebugBuildsInitializeGeneratedFieldsLazily()
    {
        var generated = await Generate(GreetingV2, OptimizationLevel.Debug);
        var codec = GetClass(generated, "Codec_Greeting").NormalizeWhitespace().ToFullString();
        var copier = GetClass(generated, "Copier_Greeting").NormalizeWhitespace().ToFullString();

        Assert.Contains("private readonly global::Orleans.Serialization.Serializers.ICodecProvider _codecProvider;", codec);
        Assert.Contains("private static global::System.Action<global::TestProject.Greeting, string> setField_0;", codec);
        Assert.Contains("(setField_0 ??= ", codec);
        Assert.Contains("private global::Orleans.Serialization.Codecs.ListCodec<int> _codec_List_Int32;", codec);
        Assert.Contains("(_codec_List_Int32 ??= OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Codecs.ListCodec<int>>(this, _codecProvider))", codec);
        Assert.Contains("_codec_Detail", codec);
        Assert.Contains("typeof(global::System.Collections.Generic.List<int>)", codec);
        Assert.DoesNotContain("_type_", codec);
        Assert.DoesNotContain("readonly global::Orleans.Serialization.Codecs.ListCodec<int>", codec);

        Assert.Contains("private global::Orleans.Serialization.Codecs.ListCopier<int> _copier_List_Int32;", copier);
        Assert.Contains("(_copier_List_Int32 ??= ", copier);
        Assert.Contains("private readonly global::Orleans.Serialization.Serializers.ICodecProvider _codecProvider;", copier);
    }

    [Fact]
    public async Task ReleaseBuildsKeepEagerInitialization()
    {
        var generated = await Generate(GreetingV2, OptimizationLevel.Release);
        var codec = GetClass(generated, "Codec_Greeting").NormalizeWhitespace().ToFullString();

        Assert.Contains("private static readonly global::System.Action<global::TestProject.Greeting, string> setField_0 = ", codec);
        Assert.Contains("private readonly global::Orleans.Serialization.Codecs.ListCodec<int> _codec_List_Int32;", codec);
        Assert.Contains("private readonly global::System.Type _type_List_Int32 = typeof(global::System.Collections.Generic.List<int>);", codec);
        Assert.DoesNotContain("_codecProvider", codec);
        Assert.DoesNotContain("??=", codec);
    }

    [Theory]
    [InlineData(OptimizationLevel.Release, "true", true)]
    [InlineData(OptimizationLevel.Debug, "false", false)]
    public async Task HotReloadPropertyOverridesOptimizationLevel(OptimizationLevel level, string propertyValue, bool expectLazy)
    {
        var generated = await Generate(GreetingV2, level, new Dictionary<string, string> { [HotReloadProperty] = propertyValue });
        var codec = GetClass(generated, "Codec_Greeting").NormalizeWhitespace().ToFullString();

        Assert.Equal(expectLazy, codec.Contains("_codecProvider", StringComparison.Ordinal));
        Assert.Equal(expectLazy, codec.Contains("??=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EmptyCodecsStillAcceptTheCodecProvider()
    {
        const string code = """
            using Orleans;

            namespace TestProject;

            [GenerateSerializer]
            public sealed class Empty
            {
            }
            """;

        var codec = GetClass(await Generate(code, OptimizationLevel.Debug), "Codec_Empty");
        var ctor = Assert.Single(codec.Members.OfType<ConstructorDeclarationSyntax>());
        var parameter = Assert.Single(ctor.ParameterList.Parameters);
        Assert.Equal("global::Orleans.Serialization.Serializers.ICodecProvider", parameter.Type!.ToString());
    }

    [Fact]
    public async Task CollidingTypeNamesGetDistinctFieldNames()
    {
        const string code = """
            using Orleans;

            namespace First { [GenerateSerializer] public sealed class Item { [Id(0)] public int A { get; set; } } }
            namespace Second { [GenerateSerializer] public sealed class Item { [Id(0)] public int B { get; set; } } }

            namespace TestProject
            {
                [GenerateSerializer]
                public sealed class Holder
                {
                    [Id(0)] public First.Item? First { get; set; }
                    [Id(1)] public Second.Item? Second { get; set; }
                }
            }
            """;

        var (generated, outputCompilation) = await GenerateAndCompile(code, OptimizationLevel.Release);
        Assert.Empty(outputCompilation.GetDiagnostics(TestContext.Current.CancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error));

        var codecFields = GetClass(generated, "Codec_Holder").Members
            .OfType<FieldDeclarationSyntax>()
            .Select(f => f.Declaration.Variables.Single().Identifier.Text)
            .Where(name => name.StartsWith("_codec_", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, codecFields.Count);
        Assert.All(codecFields, name => Assert.Matches("^_codec_Item_[0-9A-F]{8}$", name));
        Assert.NotEqual(codecFields[0], codecFields[1]);
    }

    [Fact]
    public async Task AccessorFieldsAreNamedByFieldId()
    {
        var codec = GetClass(await Generate(GreetingV2, OptimizationLevel.Release), "Codec_Greeting").NormalizeWhitespace().ToFullString();

        Assert.Contains("setField_0 = ", codec);
        Assert.Contains("setField_3 = ", codec);
        Assert.Contains("setField_4 = ", codec);
        Assert.DoesNotContain("setField0", codec);
        Assert.DoesNotContain("setField1", codec);
    }

    [Fact]
    public async Task EnumAndInvokableCodecsSkipTheCodecProvider()
    {
        const string code = """
            using System.Threading.Tasks;
            using Orleans;

            namespace TestProject;

            [GenerateSerializer]
            public enum Color
            {
                Red,
                Green,
            }

            public interface IThingGrain : IGrainWithIntegerKey
            {
                Task<int> Get(int value);
            }
            """;

        var generated = await Generate(code, OptimizationLevel.Debug);
        var root = CSharpSyntaxTree.ParseText(generated, cancellationToken: TestContext.Current.CancellationToken).GetRoot(TestContext.Current.CancellationToken);
        var generatedForInvokablesAndEnum = root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .Where(c => c.Identifier.Text == "Codec_Color"
                || c.Identifier.Text.StartsWith("Codec_Invokable_", StringComparison.Ordinal)
                || c.Identifier.Text.StartsWith("Copier_Invokable_", StringComparison.Ordinal))
            .ToList();

        Assert.Contains(generatedForInvokablesAndEnum, c => c.Identifier.Text == "Codec_Color");
        Assert.Contains(generatedForInvokablesAndEnum, c => c.Identifier.Text.StartsWith("Codec_Invokable_", StringComparison.Ordinal));
        Assert.All(generatedForInvokablesAndEnum, c => Assert.DoesNotContain("_codecProvider", c.ToFullString(), StringComparison.Ordinal));
    }

    private static IEnumerable<string> GetMemberSignatures(ClassDeclarationSyntax classDeclaration)
    {
        foreach (var member in classDeclaration.Members)
        {
            switch (member)
            {
                case FieldDeclarationSyntax field:
                    yield return $"{field.Modifiers} {field.Declaration.Type} {field.Declaration.Variables.Single().Identifier}";
                    break;
                case MethodDeclarationSyntax method:
                    yield return $"{method.Modifiers} {method.ReturnType} {method.Identifier}{method.TypeParameterList}{method.ParameterList}".Replace(" ", "");
                    break;
            }
        }
    }

    private static ClassDeclarationSyntax GetClass(string generatedSource, string className)
    {
        var root = CSharpSyntaxTree.ParseText(generatedSource, cancellationToken: TestContext.Current.CancellationToken).GetRoot(TestContext.Current.CancellationToken);
        return Assert.Single(root.DescendantNodes().OfType<ClassDeclarationSyntax>(), c => c.Identifier.Text == className);
    }

    private static async Task<string> Generate(string code, OptimizationLevel level, IReadOnlyDictionary<string, string>? globalOptions = null)
    {
        var (generated, _) = await GenerateAndCompile(code, level, globalOptions);
        return generated;
    }

    private static async Task<(string GeneratedSource, Compilation OutputCompilation)> GenerateAndCompile(
        string code,
        OptimizationLevel level,
        IReadOnlyDictionary<string, string>? globalOptions = null)
    {
        var compilation = await TestCompilationHelper.CreateCompilation(code);
        compilation = compilation.WithOptions(compilation.Options.WithOptimizationLevel(level));
        Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error));

        var generator = new OrleansSerializationSourceGenerator().AsSourceGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [generator],
            optionsProvider: globalOptions is null ? null : new TestAnalyzerConfigOptionsProvider(globalOptions),
            driverOptions: new GeneratorDriverOptions(default));
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics, TestContext.Current.CancellationToken);
        Assert.Empty(diagnostics);

        var result = driver.GetRunResult().Results.Single();
        var generatedSource = string.Join(
            Environment.NewLine,
            result.GeneratedSources.OrderBy(s => s.HintName, StringComparer.Ordinal).Select(s => s.SourceText.ToString()));
        return (generatedSource, outputCompilation);
    }

    private sealed class TestAnalyzerConfigOptionsProvider(IReadOnlyDictionary<string, string> globalOptions) : AnalyzerConfigOptionsProvider
    {
        private static readonly AnalyzerConfigOptions EmptyOptions = new TestAnalyzerConfigOptions(new Dictionary<string, string>());
        private readonly AnalyzerConfigOptions _globalOptions = new TestAnalyzerConfigOptions(globalOptions);

        public override AnalyzerConfigOptions GlobalOptions => _globalOptions;
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => EmptyOptions;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => EmptyOptions;
    }

    private sealed class TestAnalyzerConfigOptions(IReadOnlyDictionary<string, string> options) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value) => options.TryGetValue(key, out value!);
    }
}
