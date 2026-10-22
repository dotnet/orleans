using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Clustering.Cosmos;
using Orleans.Hosting;
using Orleans.Providers;

namespace Tester.Cosmos.Clustering;

[TestCategory("Cosmos"), TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("Cosmos")]
[TestArea("Clustering")]
public class CosmosClusteringProviderBuilderTests
{
    [Fact]
    public void Configure_Silo_TypedValues_Bind()
    {
        var configuration = CreateConfiguration(
            (nameof(CosmosClusteringOptions.IsResourceCreationEnabled), "true"),
            (nameof(CosmosClusteringOptions.DatabaseThroughput), "800"),
            (nameof(CosmosClusteringOptions.CleanResourcesOnInitialization), "true"));
        var builder = new TestSiloBuilder(configuration);
        IProviderBuilder<ISiloBuilder> provider = new CosmosClusteringProviderBuilder();

        provider.Configure(builder, null, configuration.GetSection("Clustering"));

        using var services = builder.Services.BuildServiceProvider();
        var options = services.GetRequiredService<IOptions<CosmosClusteringOptions>>().Value;
        Assert.True(options.IsResourceCreationEnabled);
        Assert.Equal(800, options.DatabaseThroughput);
        Assert.True(options.CleanResourcesOnInitialization);
    }

    [Theory]
    [InlineData(nameof(CosmosClusteringOptions.IsResourceCreationEnabled), "not-a-boolean", typeof(bool))]
    [InlineData(nameof(CosmosClusteringOptions.DatabaseThroughput), "not-an-integer", typeof(int))]
    [InlineData(nameof(CosmosClusteringOptions.CleanResourcesOnInitialization), "not-a-boolean", typeof(bool))]
    public void Configure_Silo_InvalidTypedValue_Throws(string key, string invalidValue, Type expectedType)
    {
        var configuration = CreateConfiguration((key, invalidValue));
        var builder = new TestSiloBuilder(configuration);
        IProviderBuilder<ISiloBuilder> provider = new CosmosClusteringProviderBuilder();

        provider.Configure(builder, null, configuration.GetSection("Clustering"));

        using var services = builder.Services.BuildServiceProvider();
        var exception = Assert.Throws<InvalidOperationException>(
            () => services.GetRequiredService<IOptions<CosmosClusteringOptions>>().Value);
        Assert.Contains($"at 'Clustering:{key}'", exception.Message);
        Assert.Contains($"type '{expectedType}'", exception.Message);
    }

    [Theory]
    [InlineData(nameof(CosmosClusteringOptions.IsResourceCreationEnabled), "not-a-boolean", typeof(bool))]
    [InlineData(nameof(CosmosClusteringOptions.DatabaseThroughput), "not-an-integer", typeof(int))]
    [InlineData(nameof(CosmosClusteringOptions.CleanResourcesOnInitialization), "not-a-boolean", typeof(bool))]
    public void Configure_Client_InvalidTypedValue_Throws(string key, string invalidValue, Type expectedType)
    {
        var configuration = CreateConfiguration((key, invalidValue));
        var builder = new TestClientBuilder(configuration);
        IProviderBuilder<IClientBuilder> provider = new CosmosClusteringProviderBuilder();

        provider.Configure(builder, null, configuration.GetSection("Clustering"));

        using var services = builder.Services.BuildServiceProvider();
        var exception = Assert.Throws<InvalidOperationException>(
            () => services.GetRequiredService<IOptions<CosmosClusteringOptions>>().Value);
        Assert.Contains($"at 'Clustering:{key}'", exception.Message);
        Assert.Contains($"type '{expectedType}'", exception.Message);
    }

    [Theory]
    [InlineData(nameof(CosmosClusteringOptions.IsResourceCreationEnabled), "", typeof(bool))]
    [InlineData(nameof(CosmosClusteringOptions.DatabaseThroughput), "", typeof(int))]
    [InlineData(nameof(CosmosClusteringOptions.IsResourceCreationEnabled), "   ", typeof(bool))]
    [InlineData(nameof(CosmosClusteringOptions.DatabaseThroughput), "   ", typeof(int))]
    public void Configure_Silo_BlankTypedValue_FollowsBinderContract(string key, string blankValue, Type expectedType)
    {
        var configuration = CreateConfiguration((key, blankValue));
        var builder = new TestSiloBuilder(configuration);
        builder.Services.Configure<CosmosClusteringOptions>(PreconfigureNonDefaultValues);
        IProviderBuilder<ISiloBuilder> provider = new CosmosClusteringProviderBuilder();

        provider.Configure(builder, null, configuration.GetSection("Clustering"));

        using var services = builder.Services.BuildServiceProvider();
        AssertBlankValueContract(() => services.GetRequiredService<IOptions<CosmosClusteringOptions>>().Value, key, blankValue, expectedType);
    }

    [Theory]
    [InlineData(nameof(CosmosClusteringOptions.IsResourceCreationEnabled), "", typeof(bool))]
    [InlineData(nameof(CosmosClusteringOptions.DatabaseThroughput), "", typeof(int))]
    [InlineData(nameof(CosmosClusteringOptions.IsResourceCreationEnabled), "   ", typeof(bool))]
    [InlineData(nameof(CosmosClusteringOptions.DatabaseThroughput), "   ", typeof(int))]
    public void Configure_Client_BlankTypedValue_FollowsBinderContract(string key, string blankValue, Type expectedType)
    {
        var configuration = CreateConfiguration((key, blankValue));
        var builder = new TestClientBuilder(configuration);
        builder.Services.Configure<CosmosClusteringOptions>(PreconfigureNonDefaultValues);
        IProviderBuilder<IClientBuilder> provider = new CosmosClusteringProviderBuilder();

        provider.Configure(builder, null, configuration.GetSection("Clustering"));

        using var services = builder.Services.BuildServiceProvider();
        AssertBlankValueContract(() => services.GetRequiredService<IOptions<CosmosClusteringOptions>>().Value, key, blankValue, expectedType);
    }

    // The configuration binding source generator treats an empty value as absent on .NET 10 and rejects it on .NET 8.
    // Whitespace-only values are invalid on both.
#if NET10_0_OR_GREATER
    private static readonly bool EmptyValueIsAbsent = true;
#else
    private static readonly bool EmptyValueIsAbsent = false;
#endif

    private static void AssertBlankValueContract(Func<CosmosClusteringOptions> getOptions, string key, string blankValue, Type expectedType)
    {
        if (EmptyValueIsAbsent && blankValue.Length == 0)
        {
            var options = getOptions();
            Assert.True(options.IsResourceCreationEnabled);
            Assert.Equal(800, options.DatabaseThroughput);
            return;
        }

        var exception = Assert.Throws<InvalidOperationException>(() => getOptions());
        Assert.Contains($"at 'Clustering:{key}'", exception.Message);
        Assert.Contains($"type '{expectedType}'", exception.Message);
    }

    private static void PreconfigureNonDefaultValues(CosmosClusteringOptions options)
    {
        options.IsResourceCreationEnabled = true;
        options.DatabaseThroughput = 800;
    }

    private static IConfiguration CreateConfiguration(params (string Key, string Value)[] settings)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(setting => new KeyValuePair<string, string?>($"Clustering:{setting.Key}", setting.Value)))
            .Build();

    private sealed class TestSiloBuilder(IConfiguration configuration) : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = configuration;
    }

    private sealed class TestClientBuilder(IConfiguration configuration) : IClientBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = configuration;
    }
}
