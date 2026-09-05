using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Providers;
using Orleans.Providers.Streams.Common;
using Orleans.Providers.Streams.Generator;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streams;
using Xunit;

namespace UnitTests.OrleansRuntime.Streams;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Streaming")]
public class PooledCacheCompatibilityTests
{
    [Theory]
    [InlineData(false, StreamSubscriptionStartPosition.Latest)]
    [InlineData(false, StreamSubscriptionStartPosition.EarliestAvailable)]
    [InlineData(true, StreamSubscriptionStartPosition.Latest)]
    [InlineData(true, StreamSubscriptionStartPosition.EarliestAvailable)]
    public void LegacyPositionPreservesPooledProviderBehavior(
        bool useMemoryCache,
        StreamSubscriptionStartPosition position)
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var pool = new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(1024));
        var stream = StreamId.Create("namespace", Guid.NewGuid());
        IQueueCache cache;
        if (useMemoryCache)
        {
            var serializer = new DefaultMemoryMessageBodySerializer(services.GetRequiredService<Serializer<MemoryMessageBody>>());
            cache = new MemoryPooledCache<DefaultMemoryMessageBodySerializer>(
                pool,
                new TimePurgePredicate(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10)),
                NullLogger.Instance,
                serializer,
                cacheMonitor: null,
                monitorWriteInterval: null,
                purgeMetadataInterval: null);
            cache.AddToCache(
            [
                new MemoryBatchContainer<DefaultMemoryMessageBodySerializer>(
                    new MemoryMessageData
                    {
                        StreamId = stream,
                        SequenceNumber = 1,
                        EnqueueTimeUtc = DateTime.UtcNow,
                        Payload = serializer.Serialize(new MemoryMessageBody([1], requestContext: null)),
                    },
                    serializer),
            ]);
        }
        else
        {
            cache = new GeneratorPooledCache(
                pool,
                NullLogger.Instance,
                services.GetRequiredService<Serializer>(),
                cacheMonitor: null,
                monitorWriteInterval: null);
            cache.AddToCache([new GeneratedBatchContainer(stream, 1, new EventSequenceTokenV2(1))]);
        }

#pragma warning disable CS0618 // Verify compatibility of the obsolete wrapper.
        using var legacyCursor = cache.GetCacheCursorAtPosition(stream, position);
        var legacyHasMessage = legacyCursor.MoveNext();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => cache.GetCacheCursorAtPosition(stream, (StreamSubscriptionStartPosition)123));
#pragma warning restore CS0618

        var result = cache.TryGetCacheCursorAtPosition(stream, position);
        Assert.Equal(QueueCacheCursorResultKind.Success, result.Kind);
        Assert.NotNull(result.Cursor);
        using var typedCursor = result.Cursor;
        var expectedKind = position == StreamSubscriptionStartPosition.EarliestAvailable
            ? QueueCacheCursorMoveResultKind.Success
            : QueueCacheCursorMoveResultKind.NoData;
        Assert.Equal(expectedKind == QueueCacheCursorMoveResultKind.Success, legacyHasMessage);
        Assert.Equal(expectedKind, typedCursor.MoveNextWithResult().Kind);
        if (legacyHasMessage)
        {
            Assert.Equal(1, legacyCursor.GetCurrent(out _)!.SequenceToken.SequenceNumber);
            Assert.Equal(1, typedCursor.GetCurrent(out _)!.SequenceToken.SequenceNumber);
        }
        else
        {
            Assert.Null(legacyCursor.GetCurrent(out _));
            Assert.Null(typedCursor.GetCurrent(out _));
        }
    }
}
