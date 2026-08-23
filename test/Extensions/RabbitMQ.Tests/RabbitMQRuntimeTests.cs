using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streaming.RabbitMQ.Adapters;
using Orleans.Streaming.RabbitMQ.RabbitMQ;
using Orleans.Streams;
using RabbitMQ.Stream.Client;
using Xunit;

namespace RabbitMQ.Tests;

[TestSuite("BVT"), TestProvider("None"), TestArea("Streaming")]
public class RabbitMQRuntimeTests
{
    [Fact]
    public async Task RawMessageBufferBlocksAtCapacityAndCloseCancelsWriter()
    {
        var consumer = new RabbitMQConsumer(
            null!,
            null!,
            NullLoggerFactory.Instance,
            default,
            null!,
            new RabbitMQQueueCacheOptions { CacheSize = 1 });
        consumer.StartBuffering();
        await consumer.BufferMessage([1], null!, 0);

        var blockedWrite = consumer.BufferMessage([2], null!, 1);
        await Task.Yield();
        Assert.False(blockedWrite.IsCompleted);

        await consumer.CloseConsumer();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await blockedWrite);
        Assert.Equal(0, consumer.BufferedMessageCount);
    }

    [Fact]
    public async Task DeliveredPrefixCheckpointsHighestOffsetWithoutReadCountGate()
    {
        ulong? storedOffset = null;
        var receiver = new RabbitMQAdapterReceiver(
            null!,
            null!,
            NullLogger<RabbitMQAdapterReceiver>.Instance,
            new RabbitMQClientOptions { IntervalToUpdateOffset = TimeSpan.Zero },
            offset =>
            {
                storedOffset = offset;
                return Task.CompletedTask;
            });
        var streamId = StreamId.Create("test", Guid.NewGuid());
        IList<IBatchContainer> delivered =
        [
            CreateBatch(streamId, 3),
            CreateBatch(streamId, 4)
        ];

        await receiver.MessagesDeliveredAsync(delivered);

        Assert.Equal(5UL, storedOffset);
    }

    [Fact]
    public async Task ShutdownFlushesPendingCheckpoint()
    {
        ulong? storedOffset = null;
        var receiver = new RabbitMQAdapterReceiver(
            null!,
            null!,
            NullLogger<RabbitMQAdapterReceiver>.Instance,
            new RabbitMQClientOptions { IntervalToUpdateOffset = TimeSpan.FromHours(1) },
            offset =>
            {
                storedOffset = offset;
                return Task.CompletedTask;
            },
            () => Task.CompletedTask);
        var streamId = StreamId.Create("test", Guid.NewGuid());

        await receiver.MessagesDeliveredAsync([CreateBatch(streamId, 7)]);
        Assert.Null(storedOffset);

        await receiver.Shutdown(TimeSpan.Zero);

        Assert.Equal(8UL, storedOffset);
    }

    [Fact]
    public async Task ShutdownClosesConsumerWhenCheckpointFails()
    {
        var closed = false;
        var receiver = new RabbitMQAdapterReceiver(
            null!,
            null!,
            NullLogger<RabbitMQAdapterReceiver>.Instance,
            new RabbitMQClientOptions { IntervalToUpdateOffset = TimeSpan.FromHours(1) },
            _ => Task.FromException(new InvalidOperationException("checkpoint failed")),
            () =>
            {
                closed = true;
                return Task.CompletedTask;
            });
        var streamId = StreamId.Create("test", Guid.NewGuid());
        await receiver.MessagesDeliveredAsync([CreateBatch(streamId, 7)]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => receiver.Shutdown(TimeSpan.Zero));

        Assert.True(closed);
    }

    [Fact]
    public async Task ResumeOffsetAdvancesPastLocallyBufferedMessages()
    {
        var consumer = new RabbitMQConsumer(
            null!,
            null!,
            NullLoggerFactory.Instance,
            default,
            null!,
            new RabbitMQQueueCacheOptions { CacheSize = 1 });
        consumer.StartBuffering();
        await consumer.BufferMessage([], null!, 4);

        Assert.Equal(5UL, consumer.GetResumeOffset(2));
        Assert.Equal(6UL, consumer.GetResumeOffset(6));
    }

    private static RabbitMqBatchContainer CreateBatch(StreamId streamId, long sequenceNumber) =>
        new(streamId, [new object()], new EventSequenceTokenV2(sequenceNumber));

    [Fact]
    public async Task DisconnectGeneration_CancelsBlockedWriterAndRejectsStaleDelivery()
    {
        var consumer = new RabbitMQConsumer(
            null!,
            null!,
            NullLoggerFactory.Instance,
            default,
            null!,
            new RabbitMQQueueCacheOptions { CacheSize = 1 });
        var oldGeneration = consumer.StartBuffering();
        await consumer.BufferMessage(oldGeneration, [10], null!, 10);

        var blockedWrite = consumer.BufferMessage(oldGeneration, [11], null!, 11);
        await Task.Yield();
        Assert.False(blockedWrite.IsCompleted);

        consumer.DisconnectGeneration(oldGeneration);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await blockedWrite);

        var newGeneration = consumer.StartBuffering();
        Assert.Equal(oldGeneration.Id + 1, newGeneration.Id);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await consumer.BufferMessage(oldGeneration, [8], null!, 8));

        Assert.Equal(1, consumer.BufferedMessageCount);
        Assert.Equal(10, consumer.LastReceivedOffset);
        Assert.Equal(11UL, consumer.GetResumeOffset(0));
    }

    [Fact]
    public async Task ShutdownDuringConsumerCreationClosesRejectedConsumer()
    {
        var consumer = new RabbitMQConsumer(
            null!,
            null!,
            NullLoggerFactory.Instance,
            default,
            null!,
            new RabbitMQQueueCacheOptions { CacheSize = 1 });
        var generation = consumer.StartBuffering();
        var createdConsumer = new TrackingConsumer();

        await consumer.CloseConsumer();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => consumer.CompleteConsumerCreation(createdConsumer, generation, connectionClosed: false));
        Assert.Equal(1, createdConsumer.CloseCount);
    }

    private sealed class TrackingConsumer : IConsumer
    {
        public int CloseCount { get; private set; }

        public Task StoreOffset(ulong offset) => Task.CompletedTask;

        public Task<ResponseCode> Close()
        {
            CloseCount++;
            return Task.FromResult(ResponseCode.Ok);
        }

        public void Dispose()
        {
        }
    }
}
