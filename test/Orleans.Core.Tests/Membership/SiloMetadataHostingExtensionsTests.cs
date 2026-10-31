using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Runtime.MembershipService.SiloMetadata;
using Xunit;

namespace NonSilo.Tests.Membership;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public sealed class SiloMetadataHostingExtensionsTests
{
    [Fact]
    public void DefaultConfigurationRejectsNullBuilder()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => ((ISiloBuilder)null!).UseSiloMetadata());

        Assert.Equal("builder", exception.ParamName);
    }

    [Theory]
    [InlineData(0, "configuration")]
    [InlineData(1, "configurationSection")]
    [InlineData(2, "metadata")]
    public void NullMetadataInputsPreserveServiceRegistrations(int overload, string parameterName)
    {
        var builder = new TestBuilder();
        var exception = Assert.Throws<ArgumentNullException>(() =>
        {
            _ = overload switch
            {
                0 => builder.UseSiloMetadata((IConfiguration)null!),
                1 => builder.UseSiloMetadata((IConfigurationSection)null!),
                2 => builder.UseSiloMetadata((Dictionary<string, string>)null!),
                _ => throw new ArgumentOutOfRangeException(nameof(overload)),
            };
        });

        Assert.Equal(parameterName, exception.ParamName);
        Assert.Empty(builder.Services);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ConfigurationOverloadsPreserveBuilderAndPublishMetadata(int overload)
    {
        var builder = new TestBuilder();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Orleans:Metadata:region"] = "west",
                ["Orleans:Metadata:zone"] = "one",
            }).Build();
        builder.Configuration = configuration;

        var result = overload switch
        {
            0 => builder.UseSiloMetadata(),
            1 => builder.UseSiloMetadata((IConfiguration)configuration),
            2 => builder.UseSiloMetadata(configuration.GetSection("Orleans:Metadata")),
            3 => builder.UseSiloMetadata(new Dictionary<string, string> { ["region"] = "west", ["zone"] = "one" }),
            _ => throw new ArgumentOutOfRangeException(nameof(overload)),
        };
        using var provider = builder.Services.BuildServiceProvider();
        var metadata = provider.GetRequiredService<IOptions<SiloMetadata>>().Value.Metadata;

        Assert.Same(builder, result);
        Assert.Equal(2, metadata.Count);
        Assert.Equal("west", metadata["region"]);
        Assert.Equal("one", metadata["zone"]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EmptyConfigurationOverloadsPublishEmptyMetadata(int overload)
    {
        var builder = new TestBuilder();
        var result = overload switch
        {
            0 => builder.UseSiloMetadata(),
            1 => builder.UseSiloMetadata(builder.Configuration),
            2 => builder.UseSiloMetadata(builder.Configuration.GetSection("Orleans:Metadata")),
            3 => builder.UseSiloMetadata(new Dictionary<string, string>()),
            _ => throw new ArgumentOutOfRangeException(nameof(overload)),
        };
        using var provider = builder.Services.BuildServiceProvider();

        Assert.Same(builder, result);
        Assert.Empty(provider.GetRequiredService<IOptions<SiloMetadata>>().Value.Metadata);
    }

    [Fact]
    public void RepeatedMetadataConfigurationCombinesKeysAndUsesLatestValue()
    {
        var builder = new TestBuilder();
        builder.UseSiloMetadata(new Dictionary<string, string> { ["region"] = "west" });
        builder.UseSiloMetadata(new Dictionary<string, string> { ["region"] = "east", ["zone"] = "two" });
        using var provider = builder.Services.BuildServiceProvider();
        var metadata = provider.GetRequiredService<IOptions<SiloMetadata>>().Value.Metadata;

        Assert.Equal(2, metadata.Count);
        Assert.Equal("east", metadata["region"]);
        Assert.Equal("two", metadata["zone"]);
    }

    private sealed class TestBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();
        public IConfiguration Configuration { get; set; } = new ConfigurationBuilder().Build();
    }
}
