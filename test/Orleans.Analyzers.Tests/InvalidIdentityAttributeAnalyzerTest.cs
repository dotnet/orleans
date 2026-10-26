using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Analyzers;
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
    [InlineData("counter")]
    [InlineData("Contoso.Counter+INested`1")]
    [InlineData("counter-v2")]
    [InlineData(@"counter\v2")]
    [InlineData("\u8BA1\u6570\u5668")]
    public async Task ValidIdentity_IsPreservedAtDeclarationBoundaries(string identity)
    {
        var (diagnostics, _) = await GetDiagnosticsAsync(CreateSource(identity));
        Assert.Empty(diagnostics);

        Assert.Equal(identity, new AliasAttribute(identity).Alias);
        Assert.Equal(identity, new GrainTypeAttribute(identity).GetGrainType(null!, typeof(object)).ToString());
        Assert.Equal(identity, new GrainInterfaceTypeAttribute(identity).GetGrainInterfaceType(null!, typeof(object)).ToString());
        Assert.Equal(identity, CreateConverter(identity).Format(typeof(InvalidIdentityAttributeAnalyzerTest)));
    }

    [Fact]
    public Task UnrelatedAttribute_IsUnaffected()
        => AssertNoDiagnostics("""
            [Obsolete("Spaces and \"quotes\" describe a type.")]
            public class Model { }
            """);

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
        options.WellKnownTypeAliases.Add(identity, typeof(InvalidIdentityAttributeAnalyzerTest));
        return new TypeConverter(
            Array.Empty<ITypeConverter>(),
            Array.Empty<ITypeNameFilter>(),
            Array.Empty<ITypeFilter>(),
            Options.Create(options),
            new CachedTypeResolver());
    }
}
