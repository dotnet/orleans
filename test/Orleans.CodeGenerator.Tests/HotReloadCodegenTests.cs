using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;
using Xunit;

namespace Orleans.CodeGenerator.Tests;

public class HotReloadCodegenTests
{
    private const string HotReloadProperty = "build_property.orleanshotreload";
    private const string HotReloadTestChild = "ORLEANS_HOT_RELOAD_TEST_CHILD";

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
        var options = EnableHotReload();
        var before = GetClass(await Generate(GreetingV1, OptimizationLevel.Debug, options), className);
        var after = GetClass(await Generate(GreetingV2, OptimizationLevel.Debug, options), className);

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
        var generated = await Generate(GreetingV2, OptimizationLevel.Debug, EnableHotReload());
        var codec = GetClass(generated, "Codec_Greeting").NormalizeWhitespace().ToFullString();
        var copier = GetClass(generated, "Copier_Greeting").NormalizeWhitespace().ToFullString();

        Assert.Contains("private readonly global::Orleans.Serialization.Serializers.ICodecProvider _codecProvider;", codec);
        Assert.Contains("private static global::System.Action<global::TestProject.Greeting, string> setField_0;", codec);
        Assert.Contains("(setField_0 ??= ", codec);
        Assert.Contains("private global::Orleans.Serialization.Codecs.ListCodec<int> _codec_List_Int32_", codec);
        Assert.Contains("(_codec_List_Int32_", codec);
        Assert.Contains("??= OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Codecs.ListCodec<int>>(this, _codecProvider))", codec);
        Assert.Contains("_codec_Detail_", codec);
        Assert.Contains("typeof(global::System.Collections.Generic.List<int>)", codec);
        Assert.DoesNotContain("_type_", codec);
        Assert.DoesNotContain("readonly global::Orleans.Serialization.Codecs.ListCodec<int>", codec);

        Assert.Contains("private global::Orleans.Serialization.Codecs.ListCopier<int> _copier_List_Int32_", copier);
        Assert.Contains("(_copier_List_Int32_", copier);
        Assert.Contains("??= ", copier);
        Assert.Contains("private readonly global::Orleans.Serialization.Serializers.ICodecProvider _codecProvider;", copier);
    }

    [Fact]
    public async Task ReleaseBuildsKeepEagerInitialization()
    {
        var generated = await Generate(GreetingV2, OptimizationLevel.Release);
        var codec = GetClass(generated, "Codec_Greeting").NormalizeWhitespace().ToFullString();

        Assert.Contains("private static readonly global::System.Action<global::TestProject.Greeting, string> setField_0 = ", codec);
        Assert.Contains("private readonly global::Orleans.Serialization.Codecs.ListCodec<int> _codec_List_Int32_", codec);
        Assert.Contains("private readonly global::System.Type _type_List_Int32_", codec);
        Assert.Contains("= typeof(global::System.Collections.Generic.List<int>);", codec);
        Assert.DoesNotContain("_codecProvider", codec);
        Assert.DoesNotContain("??=", codec);
    }

    [Theory]
    [InlineData(OptimizationLevel.Debug, null, false)]
    [InlineData(OptimizationLevel.Debug, "true", true)]
    [InlineData(OptimizationLevel.Release, "false", false)]
    [InlineData(OptimizationLevel.Release, "true", true)]
    public async Task HotReloadPropertyControlsGenerationShape(OptimizationLevel level, string? propertyValue, bool expectLazy)
    {
        var options = propertyValue is null ? null : new Dictionary<string, string> { [HotReloadProperty] = propertyValue };
        var generated = await Generate(GreetingV2, level, options);
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

        var codec = GetClass(await Generate(code, OptimizationLevel.Debug, EnableHotReload()), "Codec_Empty");
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
        Assert.All(codecFields, name => Assert.Matches("^_codec_Item_[0-9A-F]{16}$", name));
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

        var generated = await Generate(code, OptimizationLevel.Debug, EnableHotReload());
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

    [Fact]
    public async Task ExistingCopierCopiesAddedMemberWhoseTypeIsAddedByMetadataUpdate()
    {
        if (!MetadataUpdater.IsSupported)
        {
            Assert.NotEqual("1", Environment.GetEnvironmentVariable(HotReloadTestChild));
            var result = await RunHotReloadTestInEnabledProcess();
            Assert.True(result.ExitCode == 0, $"{result.Output}{Environment.NewLine}{result.Error}");
            return;
        }

        Assert.True(MetadataUpdater.IsSupported);

        const string sourceV1 = """
            using System.Collections.Generic;
            using Orleans;

            namespace TestProject;

            [GenerateSerializer]
            public sealed class Greeting
            {
                [Id(0)] public string Message { get; set; } = "";
                [Id(1)] public List<int>? Existing { get; set; }
            }
            """;
        const string sourceV2 = """
            using System.Collections.Generic;
            using Orleans;

            namespace TestProject;

            [GenerateSerializer]
            public sealed class Greeting
            {
                [Id(0)] public string Message { get; set; } = "";
                [Id(1)] public List<int>? Existing { get; set; }
                [Id(2)] public Detail? Detail { get; set; }
            }

            [GenerateSerializer]
            public sealed class Detail
            {
                [Id(0)] public string Name { get; set; } = "";
            }
            """;

        var assemblyName = $"HotReloadCodegen_{Guid.NewGuid():N}";
        var compilationV1 = await GenerateCompilation(sourceV1, assemblyName, EnableHotReload());
        var compilationV2 = await GenerateCompilation(sourceV2, assemblyName, EnableHotReload());

        using var peStream = new MemoryStream();
        using var pdbStream = new MemoryStream();
        var emitResult = compilationV1.Emit(
            peStream,
            pdbStream,
            options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(emitResult.Success, FormatDiagnostics(emitResult.Diagnostics));

        var peImage = peStream.ToArray();
        var assembly = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(peImage), new MemoryStream(pdbStream.ToArray()));
        var greetingType = assembly.GetType("TestProject.Greeting", throwOnError: true)!;
        var copierType = assembly.GetType("OrleansCodeGen.TestProject.Copier_Greeting", throwOnError: true)!;

        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var codecProvider = serviceProvider.GetRequiredService<ICodecProvider>();
        var copier = Activator.CreateInstance(copierType, codecProvider)!;
        var copyContextPool = serviceProvider.GetRequiredService<CopyContextPool>();
        var deepCopyMethod = GetDeepCopyMethod(copierType, greetingType);

        var original = Activator.CreateInstance(greetingType)!;
        greetingType.GetProperty("Message")!.SetValue(original, "before");
        using (var context = copyContextPool.GetContext())
        {
            var initialCopy = deepCopyMethod.Invoke(copier, [original, context])!;
            Assert.Equal("before", greetingType.GetProperty("Message")!.GetValue(initialCopy));
        }

        using var module = ModuleMetadata.CreateFromImage(peImage);
#if NET10_0_OR_GREATER
        var baseline = EmitBaseline.CreateInitialBaseline(
            compilationV1,
            module,
            static _ => default,
            static _ => default,
            true);
#else
        var baseline = EmitBaseline.CreateInitialBaseline(
            module,
            static _ => default,
            static _ => default,
            true);
#endif

        using var metadataDelta = new MemoryStream();
        using var ilDelta = new MemoryStream();
        using var pdbDelta = new MemoryStream();
        var deltaResult = compilationV2.EmitDifference(
            baseline,
            GetSemanticEdits(compilationV1, compilationV2),
            static _ => false,
            metadataDelta,
            ilDelta,
            pdbDelta,
            TestContext.Current.CancellationToken);
        Assert.True(deltaResult.Success, FormatDiagnostics(deltaResult.Diagnostics));

        MetadataUpdater.ApplyUpdate(assembly, metadataDelta.ToArray(), ilDelta.ToArray(), pdbDelta.ToArray());

        var detailProperty = greetingType.GetProperty("Detail");
        Assert.NotNull(detailProperty);
        var detailType = assembly.GetType("TestProject.Detail", throwOnError: true)!;
        var detail = Activator.CreateInstance(detailType)!;
        detailType.GetProperty("Name")!.SetValue(detail, "after");
        detailProperty.SetValue(original, detail);
        using (var context = copyContextPool.GetContext())
        {
            var updatedCopy = deepCopyMethod.Invoke(copier, [original, context])!;
            var copiedDetail = detailProperty.GetValue(updatedCopy);
            Assert.NotNull(copiedDetail);
            Assert.Equal("after", detailType.GetProperty("Name")!.GetValue(copiedDetail));
            Assert.NotSame(detail, copiedDetail);
        }
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

    private static async Task<CSharpCompilation> GenerateCompilation(
        string code,
        string assemblyName,
        IReadOnlyDictionary<string, string> globalOptions)
    {
        var compilation = await TestCompilationHelper.CreateCompilation(code, assemblyName);
        compilation = compilation.WithOptions(compilation.Options.WithOptimizationLevel(OptimizationLevel.Debug));
        var generator = new OrleansSerializationSourceGenerator().AsSourceGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [generator],
            optionsProvider: new TestAnalyzerConfigOptionsProvider(globalOptions),
            driverOptions: new GeneratorDriverOptions(default));
        driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var outputCompilation,
            out var diagnostics,
            TestContext.Current.CancellationToken);
        Assert.Empty(diagnostics);
        Assert.Empty(outputCompilation.GetDiagnostics(TestContext.Current.CancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error));
        return (CSharpCompilation)outputCompilation;
    }

    private static IReadOnlyList<SemanticEdit> GetSemanticEdits(CSharpCompilation oldCompilation, CSharpCompilation newCompilation)
    {
        var oldGreeting = oldCompilation.GetTypeByMetadataName("TestProject.Greeting")!;
        var newGreeting = newCompilation.GetTypeByMetadataName("TestProject.Greeting")!;
        var newDetail = newCompilation.GetTypeByMetadataName("TestProject.Detail")!;
        var oldCopier = oldCompilation.GetTypeByMetadataName("OrleansCodeGen.TestProject.Copier_Greeting")!;
        var newCopier = newCompilation.GetTypeByMetadataName("OrleansCodeGen.TestProject.Copier_Greeting")!;
        var newDetailCopier = newCompilation.GetTypeByMetadataName("OrleansCodeGen.TestProject.Copier_Detail")!;

        return
        [
            CreateSemanticEdit(SemanticEditKind.Insert, oldSymbol: null, newGreeting.GetMembers("Detail").Single()),
            CreateSemanticEdit(SemanticEditKind.Insert, oldSymbol: null, newDetail),
            CreateSemanticEdit(SemanticEditKind.Insert, oldSymbol: null, newDetailCopier),
            CreateSemanticEdit(SemanticEditKind.Insert, oldSymbol: null, newCopier.GetMembers().OfType<IFieldSymbol>().Single(field => field.Name.StartsWith("_copier_Detail_", StringComparison.Ordinal))),
            CreateSemanticEdit(SemanticEditKind.Update, GetCopierConstructor(oldCopier), GetCopierConstructor(newCopier)),
            CreateSemanticEdit(SemanticEditKind.Update, GetCopyMembersMethod(oldCopier), GetCopyMembersMethod(newCopier)),
        ];
    }

    private static SemanticEdit CreateSemanticEdit(SemanticEditKind kind, ISymbol? oldSymbol, ISymbol? newSymbol)
#if NET10_0_OR_GREATER
        => new(kind, oldSymbol, newSymbol, syntaxMap: null, runtimeRudeEdit: null, instrumentation: default);
#else
        => new(kind, oldSymbol, newSymbol, syntaxMap: null, preserveLocalVariables: false);
#endif

    private static IMethodSymbol GetCopierConstructor(INamedTypeSymbol copier)
        => copier.InstanceConstructors.Single(constructor => constructor.Parameters.Length == 1);

    private static IMethodSymbol GetCopyMembersMethod(INamedTypeSymbol copier)
        => copier.GetMembers("DeepCopy").OfType<IMethodSymbol>().Single(method => method.Parameters.Length == 2);

    private static MethodInfo GetDeepCopyMethod(Type copierType, Type greetingType)
        => copierType.GetMethod("DeepCopy", [greetingType, typeof(CopyContext)])!;

    private static IReadOnlyDictionary<string, string> EnableHotReload()
        => new Dictionary<string, string> { [HotReloadProperty] = "true" };

    private static string FormatDiagnostics(IEnumerable<Diagnostic> diagnostics)
        => string.Join(Environment.NewLine, diagnostics.Select(static diagnostic => diagnostic.ToString()));

    private static async Task<(int ExitCode, string Output, string Error)> RunHotReloadTestInEnabledProcess()
    {
        var assemblyPath = Assembly.GetExecutingAssembly().Location;
        var startInfo = new ProcessStartInfo("dotnet")
        {
            Arguments = $"\"{assemblyPath}\" --filter-method \"*ExistingCopierCopiesAddedMemberWhoseTypeIsAddedByMetadataUpdate*\" --minimum-expected-tests 1",
            WorkingDirectory = Path.GetDirectoryName(assemblyPath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.Environment["DOTNET_MODIFIABLE_ASSEMBLIES"] = "debug";
        startInfo.Environment[HotReloadTestChild] = "1";

        using var process = Process.Start(startInfo)!;
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            return (process.ExitCode, await outputTask, await errorTask);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
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
