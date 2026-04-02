using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Xunit;

namespace Tester.Redis.Streaming;

[TestCategory("Redis"), TestCategory("Streaming")]
public sealed class RedisStreamBuilderExtensionsTests
{
    [Fact]
    public void ClientBuilder_AddRedisStreams_WithServiceCollectionConfiguratorDelegate_ConfiguresServices()
    {
        const string providerName = "client-configurator";
        var services = new ServiceCollection();
        var marker = new Marker(256L);
        var builder = new ClientBuilder(services, new ConfigurationBuilder().Build());

        builder.AddRedisStreams(providerName, (serviceCollection, configurator) =>
        {
            serviceCollection.AddSingleton(marker);
            configurator.ConfigurePartitioning(7);
            configurator.ConfigureRedis(optionsBuilder => optionsBuilder.Configure(options => options.MaxStreamLength = marker.Value));
        });

        using var serviceProvider = services.BuildServiceProvider();
        var options = serviceProvider.GetOptionsByName<RedisStreamingOptions>(providerName);
        var partitioning = serviceProvider.GetOptionsByName<HashRingStreamQueueMapperOptions>(providerName);

        Assert.Same(marker, serviceProvider.GetRequiredService<Marker>());
        Assert.Equal(marker.Value, options.MaxStreamLength);
        Assert.Equal(7, partitioning.TotalQueueCount);
    }

    [Fact]
    public void SiloBuilder_AddRedisStreams_WithServiceCollectionConfiguratorDelegate_ConfiguresServices()
    {
        const string providerName = "silo-configurator";
        var services = new ServiceCollection();
        var marker = new Marker(1024L);
        var builder = new TestSiloBuilder(services, new ConfigurationBuilder().Build());

        builder.AddRedisStreams(providerName, (serviceCollection, configurator) =>
        {
            serviceCollection.AddSingleton(marker);
            configurator.ConfigurePartitioning(9);
            configurator.ConfigureRedis(optionsBuilder => optionsBuilder.Configure(options => options.MaxStreamLength = marker.Value));
        });

        using var serviceProvider = services.BuildServiceProvider();
        var options = serviceProvider.GetOptionsByName<RedisStreamingOptions>(providerName);
        var partitioning = serviceProvider.GetOptionsByName<HashRingStreamQueueMapperOptions>(providerName);

        Assert.Same(marker, serviceProvider.GetRequiredService<Marker>());
        Assert.Equal(marker.Value, options.MaxStreamLength);
        Assert.Equal(9, partitioning.TotalQueueCount);
    }

    private sealed record Marker(long Value);

    private sealed class TestSiloBuilder(IServiceCollection services, IConfiguration configuration) : ISiloBuilder
    {
        public IServiceCollection Services { get; } = services;

        public IConfiguration Configuration { get; } = configuration;
    }
}
