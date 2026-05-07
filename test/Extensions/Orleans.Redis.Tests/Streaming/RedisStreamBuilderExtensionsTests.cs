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
    public void ClientBuilder_AddRedisStreams_WithConfiguratorDelegate_ConfiguresServices()
    {
        const string providerName = "client-configurator";
        var services = new ServiceCollection();
        var builder = new ClientBuilder(services, new ConfigurationBuilder().Build());

        builder.AddRedisStreams(providerName, configurator =>
        {
            configurator.ConfigurePartitioning(7);
            configurator.RedisStreamingOptions.Configure(options => options.MaxStreamLength = 256L);
        });

        using var serviceProvider = services.BuildServiceProvider();
        var options = serviceProvider.GetOptionsByName<RedisStreamingOptions>(providerName);
        var partitioning = serviceProvider.GetOptionsByName<HashRingStreamQueueMapperOptions>(providerName);

        Assert.Equal(256L, options.MaxStreamLength);
        Assert.Equal(7, partitioning.TotalQueueCount);
    }
}
