using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Serialization;
using Orleans.Streaming.Redis;
using Orleans.Streams;
using StackExchange.Redis;
using TestExtensions;
using Xunit;

namespace Tester.Redis.Streaming;

[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestCategory("Redis"), TestCategory("Streaming")]
public sealed class RedisStreamAdapterReceiverTests
{
    [SkippableFact]
    public async Task RedisStreamAdapterReceiver_TracksReadOffsetAndPersistsCheckpoint()
    {
        TestUtils.CheckForRedis();

        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer<RedisStreamBatchContainer>>();
        using var connection = await ConnectionMultiplexer.ConnectAsync(RedisStreamTestUtils.GetConfigurationOptions());

        const string providerName = "redis-receiver-tests";
        var serviceId = Guid.NewGuid().ToString("N");
        var clusterOptions = new ClusterOptions { ServiceId = serviceId };
        var queueId = QueueId.GetQueueId(providerName, 0, 0);
        var redisOptions = new RedisStreamingOptions
        {
            ConfigurationOptions = RedisStreamTestUtils.GetConfigurationOptions(),
            CreateMultiplexer = _ => Task.FromResult((Multiplexer: (IConnectionMultiplexer)connection, IsShared: true)),
            EntryExpiry = TimeSpan.FromHours(1),
            CheckpointPersistInterval = TimeSpan.Zero,
            UseApproximateMaxLength = false,
        };
        var receiverOptions = new RedisStreamReceiverOptions
        {
            ReadCount = 1,
        };

        try
        {
            var writer = new RedisStreamStorage(providerName, clusterOptions, redisOptions, receiverOptions, queueId);
            await writer.ConnectAsync();

            await writer.AddMessageAsync(CreatePayload(serializer, 0));
            await writer.AddMessageAsync(CreatePayload(serializer, 1));
            await writer.AddMessageAsync(CreatePayload(serializer, 2));

            var firstReceiver = new RedisStreamAdapterReceiver(
                serializer,
                new RedisStreamStorage(providerName, clusterOptions, redisOptions, receiverOptions, queueId));
            await firstReceiver.Initialize(TimeSpan.FromSeconds(10));

            var firstBatch = Assert.IsType<RedisStreamBatchContainer>(Assert.Single(await firstReceiver.GetQueueMessagesAsync(1)));
            var secondBatch = Assert.IsType<RedisStreamBatchContainer>(Assert.Single(await firstReceiver.GetQueueMessagesAsync(1)));

            Assert.Equal(0, Assert.Single(firstBatch.GetEvents<int>()).Item1);
            Assert.Equal(1, Assert.Single(secondBatch.GetEvents<int>()).Item1);

            var firstToken = Assert.IsType<RedisStreamSequenceToken>(firstBatch.SequenceToken);
            var secondToken = Assert.IsType<RedisStreamSequenceToken>(secondBatch.SequenceToken);
            Assert.True(firstToken.CompareTo(secondToken) < 0);

            await firstReceiver.MessagesDeliveredAsync(new List<IBatchContainer> { firstBatch, secondBatch });
            await firstReceiver.Shutdown(TimeSpan.FromSeconds(10));

            var checkpointKey = $"{serviceId}/streaming/{providerName}/{queueId}/checkpoint";
            Assert.Equal(secondToken.EntryId, await connection.GetDatabase().StringGetAsync(checkpointKey));

            var resumedReceiver = new RedisStreamAdapterReceiver(
                serializer,
                new RedisStreamStorage(providerName, clusterOptions, redisOptions, receiverOptions, queueId));
            await resumedReceiver.Initialize(TimeSpan.FromSeconds(10));

            var resumedBatch = Assert.IsType<RedisStreamBatchContainer>(Assert.Single(await resumedReceiver.GetQueueMessagesAsync(10)));
            Assert.Equal(2, Assert.Single(resumedBatch.GetEvents<int>()).Item1);

            var resumedToken = Assert.IsType<RedisStreamSequenceToken>(resumedBatch.SequenceToken);
            Assert.True(secondToken.CompareTo(resumedToken) < 0);

            await resumedReceiver.MessagesDeliveredAsync(new List<IBatchContainer> { resumedBatch });
            await resumedReceiver.Shutdown(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await RedisStreamTestUtils.DeleteServiceKeysAsync(serviceId);
        }
    }

    private static RedisValue CreatePayload(Serializer<RedisStreamBatchContainer> serializer, int value) =>
        RedisStreamBatchContainer.ToRedisValue(
            serializer,
            StreamId.Create(nameof(RedisStreamAdapterReceiverTests), "stream"),
            new object[] { value },
            new Dictionary<string, object>());
}
