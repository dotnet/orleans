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
        Assert.Equal($"Failed to convert configuration value at 'Clustering:{key}' to type '{expectedType}'.", exception.Message);
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
        Assert.Equal($"Failed to convert configuration value at 'Clustering:{key}' to type '{expectedType}'.", exception.Message);
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
