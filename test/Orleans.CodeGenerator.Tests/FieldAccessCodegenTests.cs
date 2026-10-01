using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
        var generated = await Generate(hotReload: false, frameworkVersion: "v10.0");
        Assert.Contains("UnsafeAccessorKind.Field, Name = \"_value\"", generated);
        Assert.Contains("UnsafeAccessorKind.Field, Name = \"<ReadOnly>k__BackingField\"", generated);
        Assert.Contains("UnsafeAccessorKind.Field, Name = \"<InitOnly>k__BackingField\"", generated);
        Assert.Contains("UnsafeAccessorKind.Field, Name = \"<Private>k__BackingField\"", generated);
        Assert.Contains("private extern static ref int accessField_0(global::TestProject.Fields instance);", generated);
        Assert.Contains("accessField_0(result) =", generated);
        Assert.Contains("accessField_0(instance) =", generated);
        Assert.Contains("private extern static ref int accessField_0(ref global::TestProject.StructFields instance);", generated);
    }

    [Theory]
    [InlineData(".NETCoreApp", "v7.0", false, false)]
    [InlineData(".NETCoreApp", "v8.0", true, false)]
    [InlineData(".NETCoreApp", "v9.0", true, true)]
    [InlineData(".NETCoreApp", "v10.0", true, true)]
    [InlineData(".NETCoreApp", "v11.0", true, true)]
    [InlineData(".NETStandard", "v2.1", false, false)]
    [InlineData(".NETStandard", "v9.0", false, false)]
    [InlineData(".NETFramework", "v4.8", false, false)]
    [InlineData("", "", false, false)]
    public async Task GenericAccessorsMatchTargetRuntimeSupportAndConstraints(
        string frameworkIdentifier, string frameworkVersion, bool expectFieldAccessors, bool expectGenericAccessors)
    {
        var generated = await Generate(hotReload: false, frameworkIdentifier, frameworkVersion);
        Assert.Contains("where T : class, global::System.IEquatable<T>, new()", generated);
        Assert.Equal(expectFieldAccessors, generated.Contains(
            "private extern static ref int accessField_0(global::TestProject.Fields instance);", StringComparison.Ordinal));
        Assert.Equal(expectFieldAccessors, generated.Contains(
            "private extern static ref int accessField_0(ref global::TestProject.StructFields instance);", StringComparison.Ordinal));
        if (expectGenericAccessors)
        {
            Assert.Contains("private extern static ref T accessField_0(global::TestProject.GenericFields<T> instance);", generated);
            Assert.DoesNotContain("Utilities.FieldAccessor", generated);
        }
        else
        {
            Assert.Contains("Utilities.FieldAccessor.GetReferenceSetter(typeof(global::TestProject.GenericFields<T>)", generated);
            Assert.DoesNotContain("extern static ref T", generated);
        }
    }

    [Theory]
    [InlineData("Codec_Fields", 4)]
    [InlineData("Copier_Fields", 4)]
    [InlineData("Codec_StructFields", 2)]
    [InlineData("Copier_StructFields", 1)]
    [InlineData("Codec_GenericFields", 2)]
    [InlineData("Copier_GenericFields", 2)]
    public async Task SingleUnsafeAccessorServesFieldReadsAndWrites(string className, int expectedAccessors)
    {
        var generated = await Generate(hotReload: false);
        var root = CSharpSyntaxTree.ParseText(generated, cancellationToken: TestContext.Current.CancellationToken)
            .GetRoot(TestContext.Current.CancellationToken);
        var type = Assert.Single(root.DescendantNodes().OfType<ClassDeclarationSyntax>(), c => c.Identifier.ValueText == className);
        var accessors = type.Members.OfType<MethodDeclarationSyntax>()
            .Where(m => m.Modifiers.Any(SyntaxKind.ExternKeyword)).ToList();

        Assert.Equal(expectedAccessors, accessors.Count);
        Assert.Equal(expectedAccessors, accessors.Select(m => m.Identifier.ValueText).Distinct(StringComparer.Ordinal).Count());
        Assert.All(accessors, accessor =>
        {
            Assert.StartsWith("accessField_", accessor.Identifier.ValueText, StringComparison.Ordinal);
            Assert.IsType<RefTypeSyntax>(accessor.ReturnType);
            var attribute = Assert.Single(accessor.AttributeLists.SelectMany(a => a.Attributes));
            Assert.Contains("UnsafeAccessorKind.Field", attribute.ToString(), StringComparison.Ordinal);
        });

        var target = className.Contains("StructFields", StringComparison.Ordinal) ? "accessField_1" : "accessField_0";
        var calls = type.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression.ToString() == target).ToList();
        Assert.Contains(calls, call => call.Parent is AssignmentExpressionSyntax assignment && assignment.Left == call);
        Assert.Contains(calls, call => call.Parent is not AssignmentExpressionSyntax assignment || assignment.Right == call);
        Assert.DoesNotContain("getField_", type.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("setField_", type.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("v8.0")]
    [InlineData("v9.0")]
    [InlineData("v10.0")]
    public async Task HotReloadRetainsLazyDelegateFieldAccess(string frameworkVersion)
    {
        var generated = await Generate(hotReload: true, frameworkVersion: frameworkVersion);
        Assert.DoesNotContain("UnsafeAccessorKind.Field", generated);
        Assert.Contains("Utilities.FieldAccessor.GetGetter", generated);
        Assert.Contains("Utilities.FieldAccessor.GetValueGetter", generated);
        Assert.Contains("Utilities.FieldAccessor.GetReferenceSetter", generated);
        Assert.Contains("Utilities.FieldAccessor.GetValueSetter", generated);
        Assert.Contains("(setField_0 ??=", generated);
    }

    [Theory]
    [InlineData("v7.0", "v8.0", true)]
    [InlineData("v8.0", "v9.0", true)]
    [InlineData("v9.0", "v10.0", false)]
    public async Task TargetFrameworkChangesInvalidateOnlyChangedAccessorCapabilities(
        string beforeVersion, string afterVersion, bool expectChanges)
    {
        var compilation = await TestCompilationHelper.CreateCompilation(Source);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new OrleansSerializationSourceGenerator().AsSourceGenerator()],
            optionsProvider: CreateOptions(hotReload: false, ".NETCoreApp", beforeVersion),
            driverOptions: new GeneratorDriverOptions(default, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGenerators(compilation, TestContext.Current.CancellationToken);
        var before = driver.GetRunResult().Results.Single();

        driver = driver.WithUpdatedAnalyzerConfigOptions(CreateOptions(hotReload: false, ".NETCoreApp", afterVersion));
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics, TestContext.Current.CancellationToken);
        var after = driver.GetRunResult().Results.Single();
        Assert.Empty(diagnostics);
        Assert.Empty(output.GetDiagnostics(TestContext.Current.CancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Equal(expectChanges, GetSource(before) != GetSource(after));
        var optionsStep = Assert.Single(after.TrackedSteps[OrleansSerializationSourceGenerator.GeneratorOptionsTrackingName]);
        Assert.Equal(
            expectChanges ? IncrementalStepRunReason.Modified : IncrementalStepRunReason.Unchanged,
            Assert.Single(optionsStep.Outputs).Reason);
        if (!expectChanges)
        {
            var serializerStep = Assert.Single(after.TrackedSteps[OrleansSerializationSourceGenerator.SerializerOutputsTrackingName]);
            Assert.Equal(IncrementalStepRunReason.Cached, Assert.Single(serializerStep.Outputs).Reason);
        }
    }

    private static async Task<string> Generate(bool hotReload, string frameworkIdentifier = ".NETCoreApp", string frameworkVersion = "v10.0")
    {
        var compilation = await TestCompilationHelper.CreateCompilation(Source);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new OrleansSerializationSourceGenerator().AsSourceGenerator()],
            optionsProvider: CreateOptions(hotReload, frameworkIdentifier, frameworkVersion));
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics, TestContext.Current.CancellationToken);
        Assert.Empty(diagnostics);
        Assert.Empty(output.GetDiagnostics(TestContext.Current.CancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error));
        return GetSource(driver.GetRunResult().Results.Single());
    }

    private static string GetSource(GeneratorRunResult result)
        => string.Join(Environment.NewLine, result.GeneratedSources.Select(s => s.SourceText.ToString()));

    private static AnalyzerConfigOptionsProvider CreateOptions(
        bool hotReload, string frameworkIdentifier, string frameworkVersion)
        => TestCompilationHelper.CreateOptionsProvider(new Dictionary<string, string>
        {
            ["build_property.orleanshotreload"] = hotReload ? "true" : "false",
            ["build_property.TargetFrameworkIdentifier"] = frameworkIdentifier,
            ["build_property.TargetFrameworkVersion"] = frameworkVersion,
        });
}
