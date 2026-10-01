using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace Orleans.CodeGenerator.Tests;

public class FieldAccessCodegenTests
{
    private const string Source = """
        using Orleans;
        namespace TestProject;

        [GenerateSerializer]
        public sealed class Fields
        {
            [Id(0)] private readonly int _value;
            [Id(1)] public string ReadOnly { get; } = "";
            [Id(2)] public string InitOnly { get; init; } = "";
            [Id(3)] private string Private { get; set; } = "";
            public int Value => _value;
        }

        [GenerateSerializer]
        public struct StructFields
        {
            [Id(0)] private readonly int _value;
            [Id(1)] private byte[] _bytes;
            public int Value => _value;
        }

        [GenerateSerializer]
        public sealed class GenericFields<T> where T : class, System.IEquatable<T>, new()
        {
            [Id(0)] private readonly T _value;
            [Id(1)] public T ReadOnly { get; }
            public T Value => _value;
        }
        """;

    [Fact]
    public async Task PrivateAndBackingFieldsUseRefReturningUnsafeAccessors()
    {
        var generated = await Generate(hotReload: false);
        Assert.Contains("UnsafeAccessorKind.Field, Name = \"_value\"", generated);
        Assert.Contains("UnsafeAccessorKind.Field, Name = \"<ReadOnly>k__BackingField\"", generated);
        Assert.Contains("UnsafeAccessorKind.Field, Name = \"<InitOnly>k__BackingField\"", generated);
        Assert.Contains("UnsafeAccessorKind.Field, Name = \"<Private>k__BackingField\"", generated);
        Assert.Contains("private extern static ref int getField_0(global::TestProject.Fields instance);", generated);
        Assert.Contains("private extern static ref int setField_0(global::TestProject.Fields instance);", generated);
        Assert.Contains("setField_0(result) =", generated);
        Assert.Contains("setField_0(instance) =", generated);
        Assert.Contains("private extern static ref int setField_0(ref global::TestProject.StructFields instance);", generated);
    }

    [Fact]
    public async Task GenericAccessorsMatchTargetRuntimeSupportAndConstraints()
    {
        var generated = await Generate(hotReload: false);
        Assert.Contains("where T : class, global::System.IEquatable<T>, new()", generated);
        if (Environment.Version.Major >= 9)
        {
            Assert.Contains("private extern static ref T setField_0(global::TestProject.GenericFields<T> instance);", generated);
            Assert.DoesNotContain("Utilities.FieldAccessor", generated);
        }
        else
        {
            Assert.Contains("Utilities.FieldAccessor.GetReferenceSetter(typeof(global::TestProject.GenericFields<T>)", generated);
            Assert.DoesNotContain("extern static ref T", generated);
        }
    }

    [Fact]
    public async Task HotReloadRetainsLazyDelegateFieldAccess()
    {
        var generated = await Generate(hotReload: true);
        Assert.DoesNotContain("UnsafeAccessorKind.Field", generated);
        Assert.Contains("Utilities.FieldAccessor.GetGetter", generated);
        Assert.Contains("Utilities.FieldAccessor.GetValueGetter", generated);
        Assert.Contains("Utilities.FieldAccessor.GetReferenceSetter", generated);
        Assert.Contains("Utilities.FieldAccessor.GetValueSetter", generated);
        Assert.Contains("(setField_0 ??=", generated);
    }

    private static async Task<string> Generate(bool hotReload)
    {
        var compilation = await TestCompilationHelper.CreateCompilation(Source);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new OrleansSerializationSourceGenerator().AsSourceGenerator()],
            optionsProvider: new HotReloadOptionsProvider(hotReload));
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics, TestContext.Current.CancellationToken);
        Assert.Empty(diagnostics);
        Assert.Empty(output.GetDiagnostics(TestContext.Current.CancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error));
        return string.Join(Environment.NewLine, driver.GetRunResult().Results.Single().GeneratedSources.Select(s => s.SourceText.ToString()));
    }

    private sealed class HotReloadOptionsProvider(bool enabled) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new HotReloadOptions(enabled);
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => GlobalOptions;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => GlobalOptions;
    }

    private sealed class HotReloadOptions(bool enabled) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value)
        {
            value = enabled ? "true" : "false";
            return key == "build_property.orleanshotreload";
        }
    }
}
