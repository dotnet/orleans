using Orleans.Configuration;
using Orleans.Streaming.Redis;
using Orleans.Streams;
using StackExchange.Redis;
using TestExtensions;
using Xunit;

namespace Tester.Redis.Streaming;

[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestCategory("Redis"), TestCategory("Streaming")]
public sealed class RedisStreamStorageTests
{
    [SkippableFact]
    public async Task RedisStreamStorage_Shutdown_DoesNotDisposeSharedMultiplexer()
    {
        TestUtils.CheckForRedis();

        using var connection = await ConnectionMultiplexer.ConnectAsync(RedisStreamTestUtils.GetConfigurationOptions());
        var storage = CreateStorage(new RedisStreamingOptions
        {
            ConfigurationOptions = RedisStreamTestUtils.GetConfigurationOptions(),
            CreateMultiplexer = _ => Task.FromResult((Multiplexer: (IConnectionMultiplexer)connection, IsShared: true)),
        });

        await storage.ConnectAsync();

        Assert.True(connection.IsConnected);

        await storage.ShutdownAsync();

        Assert.True(connection.IsConnected);
        await connection.GetDatabase().PingAsync();
    }

    [SkippableFact]
    public async Task RedisStreamStorage_Shutdown_DisposesExclusiveMultiplexer()
    {
        TestUtils.CheckForRedis();

        ConnectionMultiplexer connection = null!;

        try
        {
            var storage = CreateStorage(new RedisStreamingOptions
            {
                ConfigurationOptions = RedisStreamTestUtils.GetConfigurationOptions(),
                CreateMultiplexer = async _ =>
                {
                    connection = await ConnectionMultiplexer.ConnectAsync(RedisStreamTestUtils.GetConfigurationOptions());
                    return ((IConnectionMultiplexer)connection, false);
                },
            });

            await storage.ConnectAsync();

            Assert.True(connection.IsConnected);

            await storage.ShutdownAsync();

            Assert.False(connection.IsConnected);
        }
        finally
        {
            if (connection is not null)
            {
                connection.Dispose();
            }
        }
    }

    private static RedisStreamStorage CreateStorage(RedisStreamingOptions options)
    {
        const string providerName = "redis-storage-tests";
        var serviceId = Guid.NewGuid().ToString("N");

        return new RedisStreamStorage(
            providerName,
            new ClusterOptions { ServiceId = serviceId },
            options,
            new RedisStreamReceiverOptions(),
            QueueId.GetQueueId(providerName, 0, 0));
    }
}
