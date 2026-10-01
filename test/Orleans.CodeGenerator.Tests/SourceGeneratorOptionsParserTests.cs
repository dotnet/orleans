using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace Orleans.CodeGenerator.Tests;

public class SourceGeneratorOptionsParserTests
{
    [Theory]
    [InlineData(".NETCoreApp", "v8.0", true, false)]
    [InlineData(".NETCoreApp", "v9.0", true, true)]
    [InlineData(".NETCoreApp", "v10.0", true, true)]
    [InlineData(".NETCoreApp", "v11.0", true, true)]
    [InlineData(".netcoreapp", "V10.0", true, true)]
    [InlineData(".NETCoreApp", "10.0", true, true)]
    [InlineData(".NETCoreApp", "v7.0", false, false)]
    [InlineData(".NETStandard", "v2.1", false, false)]
    [InlineData(".NETStandard", "v10.0", false, false)]
    [InlineData(".NETFramework", "v4.8", false, false)]
    [InlineData(null, "v10.0", false, false)]
    [InlineData(".NETCoreApp", null, false, false)]
    [InlineData(null, null, false, false)]
    [InlineData(".NETCoreApp", "", false, false)]
    [InlineData(".NETCoreApp", "invalid", false, false)]
    [InlineData(".NETCoreApp", "net10.0-windows", false, false)]
    public void TargetFrameworkMetadataControlsAccessorCapabilities(
        string? identifier, string? version, bool expectFieldAccessors, bool expectGenericAccessors)
    {
        var options = Parse(identifier, version);
        Assert.Equal(expectFieldAccessors, options.SupportsUnsafeAccessors);
        Assert.Equal(expectGenericAccessors, options.SupportsGenericUnsafeAccessors);

        var generatorOptions = SourceGeneratorOptionsParser.CreateCodeGeneratorOptions(options);
        Assert.Equal(expectFieldAccessors, generatorOptions.SupportsUnsafeAccessors);
        Assert.Equal(expectGenericAccessors, generatorOptions.SupportsGenericUnsafeAccessors);
    }

    [Theory]
    [InlineData("v7.0", "v8.0", false)]
    [InlineData("v8.0", "v9.0", false)]
    [InlineData("v9.0", "v10.0", true)]
    [InlineData("v10.0", "v11.0", true)]
    public void TargetFrameworkCapabilitiesParticipateInOptionsEquality(string beforeVersion, string afterVersion, bool expectEqual)
    {
        var before = Parse(".NETCoreApp", beforeVersion);
        var after = Parse(".NETCoreApp", afterVersion);
        Assert.Equal(expectEqual, before.Equals(after));
        Assert.Equal(expectEqual, before.Equals((object)after));
        if (expectEqual)
        {
            Assert.Equal(before.GetHashCode(), after.GetHashCode());
        }
    }

    [Theory]
    [InlineData("net10.0")]
    [InlineData("net10.0-windows10.0.19041.0")]
    [InlineData("net10.0-android")]
    public void PlatformTargetSuffixPreservesRuntimeCapabilities(string targetFramework)
    {
        var provider = TestCompilationHelper.CreateOptionsProvider(new Dictionary<string, string>
        {
            ["build_property.TargetFramework"] = targetFramework,
            ["build_property.TargetFrameworkIdentifier"] = ".NETCoreApp",
            ["build_property.TargetFrameworkVersion"] = "v10.0",
        });

        var options = SourceGeneratorOptionsParser.ParseOptions(provider.GlobalOptions);
        Assert.True(options.SupportsUnsafeAccessors);
        Assert.True(options.SupportsGenericUnsafeAccessors);
    }

    private static SourceGeneratorOptions Parse(string? identifier, string? version)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (identifier is not null)
        {
            options.Add("build_property.TargetFrameworkIdentifier", identifier);
        }

        if (version is not null)
        {
            options.Add("build_property.TargetFrameworkVersion", version);
        }

        return SourceGeneratorOptionsParser.ParseOptions(new TestOptions(options));
    }

    private sealed class TestOptions(IReadOnlyDictionary<string, string> options) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value) => options.TryGetValue(key, out value!);
    }
}
