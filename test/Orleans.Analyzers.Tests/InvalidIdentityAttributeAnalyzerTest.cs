using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Diagnostics.Telemetry;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Analyzers;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.TypeSystem;
using Xunit;

namespace Analyzers.Tests;

[TestCategory("BVT"), TestCategory("Analyzer")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Analyzer")]
public class InvalidIdentityAttributeAnalyzerTest : DiagnosticAnalyzerTestBase<InvalidIdentityAttributeAnalyzer>
{
    [Theory]
    [InlineData(0x0000)]
    [InlineData(0x0009)]
    [InlineData(0x000A)]
    [InlineData(0x000D)]
    [InlineData(0x0020)]
    [InlineData(0x0022)]
    [InlineData(0x0027)]
    [InlineData(0x0026)]
    [InlineData(0x0028)]
    [InlineData(0x0029)]
    [InlineData(0x002A)]
    [InlineData(0x002C)]
    [InlineData(0x005B)]
    [InlineData(0x005D)]
    [InlineData(0x007F)]
    [InlineData(0x0085)]
    [InlineData(0x00A0)]
    [InlineData(0x2028)]
    [InlineData(0x2029)]
    [InlineData(0x3000)]
    public async Task InvalidIdentityCharacters_AreRejectedAtDeclarationBoundaries(int character)
    {
        var identity = $"counter{(char)character}v2";

        await AssertInvalidDeclarations(identity);
        AssertInvalidRuntimeDeclarations(identity);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task MissingIdentity_IsRejectedAtDeclarationBoundaries(string? identity)
    {
        await AssertInvalidDeclarations(identity);
        AssertInvalidRuntimeDeclarations(identity);
    }

    [Theory]
    [InlineData("counter`")]
    [InlineData("counter`x")]
    [InlineData("counter`-1")]
    [InlineData("counter`\u0661")]
    [InlineData("counter`1v2")]
    [InlineData("counter`1.2")]
    [InlineData("counter`1`2")]
    [InlineData("counter`1+Nested`")]
    [InlineData("counter`65")]
    [InlineData("counter`2147483648")]
    [InlineData("counter`32+Nested`33")]
    public async Task InvalidGenericArity_IsRejectedAtDeclarationBoundaries(string identity)
    {
        await AssertInvalidDeclarations(identity);
        AssertInvalidRuntimeDeclarations(identity);
    }

    [Theory]
    [InlineData("counter")]
    [InlineData("Contoso.Counter+INested`1")]
    [InlineData("counter-v2")]
    [InlineData(@"counter\v2")]
    [InlineData("\u8BA1\u6570\u5668")]
    [InlineData("counter`0")]
    [InlineData("counter`01")]
    [InlineData("counter`64")]
    [InlineData("counter`32+Nested`32")]
    public async Task ValidIdentity_IsPreservedAtDeclarationBoundaries(string identity)
    {
        var (diagnostics, _) = await GetDiagnosticsAsync(CreateSource(identity));
        Assert.Empty(diagnostics);

        Assert.Equal(identity, new AliasAttribute(identity).Alias);
        Assert.Equal(identity, new GrainTypeAttribute(identity).GetGrainType(null!, typeof(object)).ToString());
        Assert.Equal(identity, new GrainInterfaceTypeAttribute(identity).GetGrainInterfaceType(null!, typeof(object)).ToString());
        var converter = CreateConverter(identity);
        Assert.Equal(identity, converter.Format(typeof(IIdentityContract)));
        Assert.Equal(typeof(IIdentityContract), converter.Parse(identity));
        var resolver = new GrainInterfaceTypeResolver(Array.Empty<IGrainInterfaceTypeProvider>(), converter);
        Assert.Equal(identity, resolver.GetGrainInterfaceType(typeof(IIdentityContract)).ToString());
    }

    [Fact]
    public Task UnrelatedAttribute_IsUnaffected()
        => AssertNoDiagnostics("""
            [Obsolete("Spaces and \"quotes\" describe a type.")]
            public class Model { }
            """);

    [Theory]
    [InlineData("A", "AliasAttribute", "class")]
    [InlineData("O.Alias", "AliasAttribute", "class")]
    [InlineData("global::Orleans.AliasAttribute", "AliasAttribute", "class")]
    [InlineData("GT", "GrainTypeAttribute", "class")]
    [InlineData("O.GrainType", "GrainTypeAttribute", "class")]
    [InlineData("global::Orleans.GrainTypeAttribute", "GrainTypeAttribute", "class")]
    [InlineData("GIT", "GrainInterfaceTypeAttribute", "interface")]
    [InlineData("O.Runtime.GrainInterfaceType", "GrainInterfaceTypeAttribute", "interface")]
    [InlineData("global::Orleans.Runtime.GrainInterfaceTypeAttribute", "GrainInterfaceTypeAttribute", "interface")]
    public async Task AttributeNames_ReportExactDiagnosticOnConstantExpression(string attributeName, string attributeType, string declaration)
    {
        var (diagnostics, source) = await GetDiagnosticsAsync($$"""
            using A = Orleans.AliasAttribute;
            using GT = Orleans.GrainTypeAttribute;
            using GIT = Orleans.Runtime.GrainInterfaceTypeAttribute;
            using O = Orleans;

            public static class Names
            {
                public const string Invalid = "invalid" + " identity";
            }

            [{{attributeName}}(Names.Invalid)]
            public {{declaration}} InvalidModel { }

            [{{attributeName}}("valid" + "-identity")]
            public {{declaration}} ValidModel { }
            """);

        var diagnostic = Assert.Single(diagnostics);
        AssertIdentityDiagnostic(diagnostic, source, "Names.Invalid", attributeType);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Orleans.AliasAttribute")]
    [InlineData("Orleans.GrainTypeAttribute")]
    [InlineData("Orleans.Runtime.GrainInterfaceTypeAttribute")]
    public async Task AvailableIdentityAttributes_ControlSyntaxActionRegistration(string? attributeType)
    {
        var source = """
            [System.Obsolete]
            [System.CLSCompliant(true)]
            public interface IContract { }
            """;

        if (attributeType is not null)
        {
            var separator = attributeType.LastIndexOf('.');
            var attributeNamespace = attributeType[..separator];
            var attributeName = attributeType[(separator + 1)..];
            source = $$"""
                namespace {{attributeNamespace}}
                {
                    public sealed class {{attributeName}} : System.Attribute
                    {
                        public {{attributeName}}(string value) { }
                    }
                }

                [global::{{attributeType}}("invalid identity")]
                {{source}}
                """;
        }

        var (diagnostics, telemetry) = await AnalyzeSourceAsync(source);
        Assert.Equal(attributeType is null ? 0 : 1, telemetry.SyntaxNodeActionsCount);
        if (attributeType is null)
        {
            Assert.Empty(diagnostics);
        }
        else
        {
            var diagnostic = Assert.Single(diagnostics);
            AssertIdentityDiagnostic(diagnostic, source, "\"invalid identity\"", attributeType[(attributeType.LastIndexOf('.') + 1)..]);
        }
    }

    [Fact]
    public async Task GeneratedDeclarations_ReportExactDiagnostic()
    {
        const string source = """
            // <auto-generated/>
            namespace Orleans
            {
                public sealed class AliasAttribute : System.Attribute
                {
                    public AliasAttribute(string value) { }
                }
            }

            [global::Orleans.AliasAttribute("invalid" + " identity")]
            public partial class Model { }
            """;

        var (diagnostics, _) = await AnalyzeSourceAsync(source, "Model.g.cs");
        var diagnostic = Assert.Single(diagnostics);
        AssertIdentityDiagnostic(diagnostic, source, "\"invalid\" + \" identity\"", "AliasAttribute");
        Assert.Equal("Model.g.cs", diagnostic.Location.SourceTree!.FilePath);
    }

    [Fact]
    public async Task AttributesWithoutArguments_KeepDeclarationDiagnosticsUnchanged()
    {
        const string source = """
            namespace Orleans
            {
                public sealed class AliasAttribute : System.Attribute
                {
                    public AliasAttribute(string value = "") { }
                }
            }

            [System.Obsolete]
            [global::Orleans.Alias]
            public class NoArgumentList { }

            [System.Serializable()]
            [global::Orleans.Alias()]
            public class EmptyArgumentList { }
            """;

        var (diagnostics, _) = await AnalyzeSourceAsync(source);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task NonconstantArgument_PreservesDiagnosticOnResolvedConstant()
    {
        const string source = """
            namespace Orleans
            {
                public sealed class AliasAttribute : System.Attribute
                {
                    public AliasAttribute(string value) { }
                }
            }

            [Orleans.Alias(InvalidModel.Value)]
            public class NonconstantModel { }

            [Orleans.Alias("invalid identity")]
            public class InvalidModel
            {
                public static string Value = "invalid identity";
            }
            """;

        var (diagnostics, _) = await AnalyzeSourceAsync(source, expectedCompilerError: "CS0182");
        var diagnostic = Assert.Single(diagnostics);
        AssertIdentityDiagnostic(diagnostic, source, "\"invalid identity\"", "AliasAttribute");
    }

    private static void AssertIdentityDiagnostic(Diagnostic diagnostic, string source, string expression, string attributeType)
    {
        Assert.Equal(InvalidIdentityAttributeAnalyzer.RuleId, diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(source.IndexOf(expression, StringComparison.Ordinal), diagnostic.Location.SourceSpan.Start);
        Assert.Equal(expression, source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length));
        Assert.Equal(
            $"{attributeType} requires a non-empty identity containing no quotes, whitespace, control characters, or reserved type-name delimiters ([](),*&), with decimal generic arity suffixes totaling at most 64",
            diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static async Task<(ImmutableArray<Diagnostic> Diagnostics, AnalyzerTelemetryInfo Telemetry)> AnalyzeSourceAsync(
        string source,
        string filePath = "Test.cs",
        string? expectedCompilerError = null)
    {
        var compilation = CSharpCompilation.Create(
            "IdentityAttributes",
            [CSharpSyntaxTree.ParseText(source, path: filePath)],
            [MetadataReference.CreateFromFile(typeof(Attribute).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error);
        if (expectedCompilerError is null)
        {
            Assert.Empty(errors);
        }
        else
        {
            Assert.Equal(expectedCompilerError, Assert.Single(errors).Id);
        }

        var analyzer = new InvalidIdentityAttributeAnalyzer();
        var driver = compilation.WithAnalyzers(
            [analyzer],
            new CompilationWithAnalyzersOptions(new AnalyzerOptions([]), null, concurrentAnalysis: false, logAnalyzerExecutionTime: true, reportSuppressedDiagnostics: false));
        var diagnostics = await driver.GetAnalyzerDiagnosticsAsync();
        return (diagnostics, await driver.GetAnalyzerTelemetryInfoAsync(analyzer, default));
    }

    private async Task AssertInvalidDeclarations(string? identity)
    {
        var (diagnostics, source) = await GetDiagnosticsAsync(CreateSource(identity));

        Assert.Equal(4, diagnostics.Length);
        foreach (var diagnostic in diagnostics)
        {
            Assert.Equal(InvalidIdentityAttributeAnalyzer.RuleId, diagnostic.Id);
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
            Assert.Equal("Identity", source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length));
        }
    }

    private static void AssertInvalidRuntimeDeclarations(string? identity)
    {
        if (identity is null)
        {
            Assert.Equal("alias", Assert.Throws<ArgumentNullException>(() => new AliasAttribute(identity!)).ParamName);
            Assert.Equal("grainType", Assert.Throws<ArgumentNullException>(() => new GrainTypeAttribute(identity!)).ParamName);
            Assert.Equal("value", Assert.Throws<ArgumentNullException>(() => new GrainInterfaceTypeAttribute(identity!)).ParamName);
        }
        else
        {
            Assert.Equal("alias", Assert.Throws<ArgumentException>(() => new AliasAttribute(identity)).ParamName);
            Assert.Equal("grainType", Assert.Throws<ArgumentException>(() => new GrainTypeAttribute(identity)).ParamName);
            Assert.Equal("value", Assert.Throws<ArgumentException>(() => new GrainInterfaceTypeAttribute(identity)).ParamName);
            Assert.Equal(nameof(TypeManifestOptions.WellKnownTypeAliases), Assert.Throws<ArgumentException>(() => CreateConverter(identity)).ParamName);
        }
    }

    private static string CreateSource(string? identity)
        => $$"""
            using static Names;

            public static class Names
            {
                public const string Identity = {{(identity is null ? "null" : SymbolDisplay.FormatLiteral(identity, quote: true))}};
            }
            [Alias(Identity)]
            public class Model
            {
                public interface INested : IGrain { }

                [Alias(Identity)]
                public void Method() { }
            }
            [GrainType(Identity)]
            public class GrainModel { }
            [Orleans.Runtime.GrainInterfaceType(Identity)]
            public interface IContract { }
            """;

    private static TypeConverter CreateConverter(string identity)
    {
        var options = new TypeManifestOptions { AllowAllTypes = true };
        options.WellKnownTypeAliases.Add(identity, typeof(IIdentityContract));
        return new TypeConverter(
            Array.Empty<ITypeConverter>(),
            Array.Empty<ITypeNameFilter>(),
            Array.Empty<ITypeFilter>(),
            Options.Create(options),
            new CachedTypeResolver());
    }

    public interface IIdentityContract : IGrain
    {
    }
}
