using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Serialization;
using Orleans.Streaming.Redis;
using Orleans.Streams;
using Xunit;

namespace Tester.Redis.Streaming;

[TestCategory("Redis"), TestCategory("Streaming")]
public sealed class RedisStreamAdapterFactoryTests
{
    [Fact]
    public async Task RedisStreamAdapterFactory_CreateAdapter_ReturnsRewindableReadWriteAdapter()
    {
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer<RedisStreamBatchContainer>>();
        var factory = CreateFactory(serializer, "MyProviderName", totalQueueCount: 4);

        var adapter = await factory.CreateAdapter();

        var redisAdapter = Assert.IsType<RedisStreamAdapter>(adapter);
        Assert.Equal("MyProviderName", redisAdapter.Name);
        Assert.True(redisAdapter.IsRewindable);
        Assert.Equal(StreamProviderDirection.ReadWrite, redisAdapter.Direction);
    }

    [Fact]
    public async Task RedisStreamAdapterFactory_ProvidesCacheMapperAndFailureHandler()
    {
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer<RedisStreamBatchContainer>>();
        var factory = CreateFactory(serializer, "MyProviderName", totalQueueCount: 4);

        var cache = factory.GetQueueAdapterCache();
        var mapper = factory.GetStreamQueueMapper();
        var handler = await factory.GetDeliveryFailureHandler(QueueId.GetQueueId("MyQueueName", 1, 2));

        Assert.IsType<SimpleQueueAdapterCache>(cache);

        var hashMapper = Assert.IsType<HashRingBasedStreamQueueMapper>(mapper);
        Assert.Equal(4, hashMapper.GetAllQueues().Count());

        Assert.IsType<NoOpStreamDeliveryFailureHandler>(handler);
    }

    [Fact]
    public async Task RedisStreamAdapterFactory_StaticCreate_UsesNamedOptions()
    {
        const string providerName = "NamedProvider";
        using var serviceProvider = new ServiceCollection()
            .AddOptions()
            .AddLogging()
            .AddSerializer()
            .Configure<ClusterOptions>(options =>
            {
                options.ServiceId = "ServiceId";
                options.ClusterId = "ClusterId";
            })
            .Configure<HashRingStreamQueueMapperOptions>(providerName, options => options.TotalQueueCount = 4)
            .Configure<SimpleQueueCacheOptions>(providerName, options => options.CacheSize = 1234)
            .Configure<RedisStreamingOptions>(providerName, options => options.EntryExpiry = TimeSpan.FromMinutes(1))
            .Configure<RedisStreamReceiverOptions>(providerName, options => options.ReadCount = 3)
            .BuildServiceProvider();

        var factory = RedisStreamAdapterFactory.Create(serviceProvider, providerName);
        var adapter = await factory.CreateAdapter();
        var mapper = Assert.IsType<HashRingBasedStreamQueueMapper>(factory.GetStreamQueueMapper());

        Assert.Equal(4, mapper.GetAllQueues().Count());

        var redisAdapter = Assert.IsType<RedisStreamAdapter>(adapter);
        Assert.Equal(providerName, redisAdapter.Name);
        Assert.True(redisAdapter.IsRewindable);
        Assert.Equal(StreamProviderDirection.ReadWrite, redisAdapter.Direction);
    }

    private static RedisStreamAdapterFactory CreateFactory(Serializer<RedisStreamBatchContainer> serializer, string providerName, int totalQueueCount) =>
        new(
            NullLoggerFactory.Instance,
            serializer,
            providerName,
            new ClusterOptions
            {
                ServiceId = "MyServiceId",
                ClusterId = "MyClusterId",
            },
            new HashRingStreamQueueMapperOptions
            {
                TotalQueueCount = totalQueueCount,
            },
            new SimpleQueueCacheOptions(),
            new RedisStreamingOptions(),
            new RedisStreamReceiverOptions());
}
