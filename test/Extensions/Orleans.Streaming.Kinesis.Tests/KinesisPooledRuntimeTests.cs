using System.Collections.Concurrent;
using Amazon.Kinesis;
using Amazon.Kinesis.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.Extensions;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streams;
using TestExtensions;
using Xunit;
using KinesisRecord = Amazon.Kinesis.Model.Record;

namespace Orleans.Streaming.Kinesis.Tests;

[TestSuite("BVT")]
[TestArea("Streaming")]
[TestProvider("Kinesis")]
[TestCategory("AWS"), TestCategory("Kinesis")]
public sealed class KinesisPooledRuntimeTests
{
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("")]
    [InlineData("123456789012345678901234567890")]
    public async Task PooledFactory_InitialIteratorUsesDurableCheckpointOrTrimHorizon(string checkpoint)
    {
        await using var fixture = new Fixture(checkpoint);
        await fixture.Factory.CreateAdapter(TestCancellation);
        var receiver = fixture.GetReceiver();
        var client = Assert.Single(fixture.ReceiverClients);
        var record = fixture.Record("123456789012345678901234567891", "next");
        fixture.SetReads(client, () => Response("next-iterator", record));

        var notifications = await receiver.GetQueueMessagesAsync(1, TestCancellation);

        Assert.Equal(fixture.StreamId, Assert.Single(notifications).StreamId);
        using var cursor = receiver.GetCacheCursorAtPosition(fixture.StreamId, StreamSubscriptionStartPosition.EarliestAvailable);
        Assert.Equal(["next"], ReadBatch(cursor).GetEvents<string>().Select(item => item.Item1));
        Assert.False(cursor.MoveNext());
        await client.Received(1).GetShardIteratorAsync(
            Arg.Is<GetShardIteratorRequest>(request =>
                request.StreamName == "stream"
                && request.ShardId == "shard-1"
                && request.ShardIteratorType == (checkpoint.Length == 0
                    ? ShardIteratorType.TRIM_HORIZON
                    : ShardIteratorType.AFTER_SEQUENCE_NUMBER)
                && request.StartingSequenceNumber == (checkpoint.Length == 0 ? null : checkpoint)),
            Arg.Any<CancellationToken>());
        Assert.Empty(fixture.Store.Writes);
    }

    [Fact]
    public async Task PooledReceiver_ExpiredIteratorResumesAfterLastAdmittedOffset()
    {
        await using var fixture = new Fixture("9");
        await fixture.Factory.CreateAdapter(TestCancellation);
        var receiver = fixture.GetReceiver();
        var client = Assert.Single(fixture.ReceiverClients);
        client.GetShardIteratorAsync(Arg.Any<GetShardIteratorRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                new GetShardIteratorResponse { ShardIterator = "initial" },
                new GetShardIteratorResponse { ShardIterator = "renewed" });
        fixture.SetReads(client,
            () => Response("expired", fixture.Record("10", "first")),
            () => throw new ExpiredIteratorException("expired after cache admission"),
            () => Response("tail", fixture.Record("11", "second")));

        await receiver.GetQueueMessagesAsync(1, TestCancellation);
        Assert.Equal("9", fixture.Store.Checkpoint);
        await receiver.GetQueueMessagesAsync(1, TestCancellation);

        using var cursor = receiver.GetCacheCursorAtPosition(fixture.StreamId, StreamSubscriptionStartPosition.EarliestAvailable);
        AssertBatch(ReadBatch(cursor), "10", 0, "first");
        AssertBatch(ReadBatch(cursor), "11", 1, "second");
        Assert.False(cursor.MoveNext());
        Assert.Equal(["initial", "expired", "renewed"], fixture.ReadRequests.Select(request => request.Iterator));
        Assert.Equal(1, fixture.Store.LoadCount);
        Assert.Empty(fixture.Store.Writes);
        await client.Received(1).GetShardIteratorAsync(
            Arg.Is<GetShardIteratorRequest>(request =>
                request.ShardIteratorType == ShardIteratorType.AFTER_SEQUENCE_NUMBER
                && request.StartingSequenceNumber == "10"),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PooledReceiver_CacheAdmissionFailureReplaysWholeBatchWithoutAdvancingOrdinal(bool admitPrefix)
    {
        await using var fixture = new Fixture();
        await fixture.Factory.CreateAdapter(TestCancellation);
        var receiver = fixture.GetReceiver();
        var client = Assert.Single(fixture.ReceiverClients);
        if (admitPrefix)
        {
            fixture.SetReads(client, () => Response("after-prefix", fixture.Record("9", "prefix")));
            await receiver.GetQueueMessagesAsync(1, TestCancellation);
        }

        var failure = new IOException("record body unavailable");
        var valid = fixture.Record("10", "first");
        var failed = new KinesisRecord { SequenceNumber = "11", Data = new FailingPayloadStream(failure) };
        fixture.SetReads(client,
            () => Response("after-rejected-batch", valid, failed),
            () => Response("tail", valid, fixture.Record("11", "second")));
        var capacity = receiver.GetMaxAddCount();

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(
            () => receiver.GetQueueMessagesAsync(2, TestCancellation)));

        Assert.Equal(capacity, receiver.GetMaxAddCount());
        Assert.Empty(fixture.Store.Writes);
        await receiver.GetQueueMessagesAsync(2, TestCancellation);

        using var cursor = receiver.GetCacheCursorAtPosition(fixture.StreamId, StreamSubscriptionStartPosition.EarliestAvailable);
        if (admitPrefix)
        {
            AssertBatch(ReadBatch(cursor), "9", 0, "prefix");
        }

        AssertBatch(ReadBatch(cursor), "10", admitPrefix ? 1 : 0, "first");
        AssertBatch(ReadBatch(cursor), "11", admitPrefix ? 2 : 1, "second");
        Assert.False(cursor.MoveNext());
        await client.Received(admitPrefix ? 1 : 2).GetShardIteratorAsync(
            Arg.Is<GetShardIteratorRequest>(request =>
                request.ShardIteratorType == (admitPrefix ? ShardIteratorType.AFTER_SEQUENCE_NUMBER : ShardIteratorType.TRIM_HORIZON)
                && request.StartingSequenceNumber == (admitPrefix ? "9" : null)),
            Arg.Any<CancellationToken>());
        Assert.Empty(fixture.Store.Writes);
    }

    [Fact]
    public async Task PooledReceiver_RepeatedThrottlingAndTransientFailuresPreserveReadPosition()
    {
        await using var fixture = new Fixture();
        await fixture.Factory.CreateAdapter(TestCancellation);
        var receiver = fixture.GetReceiver();
        var client = Assert.Single(fixture.ReceiverClients);
        var throttled = new ProvisionedThroughputExceededException("throttled");
        var unavailable = new AmazonKinesisException("temporarily unavailable");
        fixture.SetReads(client,
            () => Response("after-first", fixture.Record("10", "first")),
            () => throw throttled,
            () => throw throttled,
            () => throw unavailable,
            () => Response("tail", fixture.Record("11", "second")));
        await receiver.GetQueueMessagesAsync(1, TestCancellation);
        var capacity = receiver.GetMaxAddCount();

        for (var attempt = 0; attempt < 2; attempt++)
        {
            Assert.Same(throttled, await Assert.ThrowsAsync<ProvisionedThroughputExceededException>(
                () => receiver.GetQueueMessagesAsync(1, TestCancellation)));
            Assert.Equal(capacity, receiver.GetMaxAddCount());
        }

        Assert.Same(unavailable, await Assert.ThrowsAsync<AmazonKinesisException>(
            () => receiver.GetQueueMessagesAsync(1, TestCancellation)));
        Assert.Equal(capacity, receiver.GetMaxAddCount());
        await receiver.GetQueueMessagesAsync(1, TestCancellation);

        Assert.Equal(["iterator", "after-first", "after-first", "after-first", "after-first"],
            fixture.ReadRequests.Select(request => request.Iterator));
        using var cursor = receiver.GetCacheCursorAtPosition(fixture.StreamId, StreamSubscriptionStartPosition.EarliestAvailable);
        AssertBatch(ReadBatch(cursor), "10", 0, "first");
        AssertBatch(ReadBatch(cursor), "11", 1, "second");
        Assert.False(cursor.MoveNext());
        Assert.Empty(fixture.Store.Writes);
        await client.Received(1).GetShardIteratorAsync(Arg.Any<GetShardIteratorRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PooledReceiver_ReadRetriesFailedInitialization()
    {
        await using var fixture = new Fixture("9");
        await fixture.Factory.CreateAdapter(TestCancellation);
        var receiver = fixture.GetReceiver();
        var client = Assert.Single(fixture.ReceiverClients);
        var failure = new AmazonKinesisException("iterator acquisition failed");
        client.GetShardIteratorAsync(Arg.Any<GetShardIteratorRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromException<GetShardIteratorResponse>(failure),
                Task.FromResult(new GetShardIteratorResponse { ShardIterator = "recovered" }));
        fixture.SetReads(client, () => Response("tail", fixture.Record("10", "recovered")));

        Assert.Same(failure, await Assert.ThrowsAsync<AmazonKinesisException>(
            () => receiver.GetQueueMessagesAsync(1, TestCancellation)));
        Assert.Empty(fixture.ReadRequests);
        Assert.Empty(fixture.Store.Writes);
        await receiver.GetQueueMessagesAsync(1, TestCancellation);

        Assert.Equal(2, fixture.Store.LoadCount);
        await fixture.CheckpointerFactory.Received(1).Create("shard-1", Arg.Any<CancellationToken>());
        await client.Received(2).GetShardIteratorAsync(
            Arg.Is<GetShardIteratorRequest>(request =>
                request.ShardIteratorType == ShardIteratorType.AFTER_SEQUENCE_NUMBER
                && request.StartingSequenceNumber == "9"),
            Arg.Any<CancellationToken>());
        using var cursor = receiver.GetCacheCursorAtPosition(fixture.StreamId, StreamSubscriptionStartPosition.EarliestAvailable);
        AssertBatch(ReadBatch(cursor), "10", 0, "recovered");
    }

    [Fact]
    public async Task PooledReceiver_OwningCallerCancellationAllowsWaitingCallerToRetry()
    {
        await using var fixture = new Fixture();
        await fixture.Factory.CreateAdapter(TestCancellation);
        var receiver = fixture.GetReceiver();
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        fixture.CheckpointerFactory.Configure().Create("shard-1", Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    var token = call.Arg<CancellationToken>();
                    started.SetResult(token);
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }

                return fixture.CreateCheckpointer();
            });
        fixture.SetReads(Assert.Single(fixture.ReceiverClients), () => Response("tail", fixture.Record("10", "recovered")));
        using var ownerCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestCancellation);

        var owner = receiver.GetQueueMessagesAsync(1, ownerCancellation.Token);
        var initializationToken = await started.Task.WaitAsync(TestCancellation);
        var waiter = receiver.GetQueueMessagesAsync(1, TestCancellation);
        ownerCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner);
        Assert.Single(await waiter);
        Assert.True(initializationToken.IsCancellationRequested);
        Assert.Equal(2, calls);
        Assert.Single(fixture.ReadRequests);
        Assert.Equal(1, fixture.Store.LoadCount);
        using var cursor = receiver.GetCacheCursorAtPosition(fixture.StreamId, StreamSubscriptionStartPosition.EarliestAvailable);
        AssertBatch(ReadBatch(cursor), "10", 0, "recovered");
    }

    [Fact]
    public async Task PooledReceiver_IndependentProviderCancellationPropagatesToBothInitializationCallers()
    {
        await using var fixture = new Fixture();
        await fixture.Factory.CreateAdapter(TestCancellation);
        var receiver = fixture.GetReceiver();
        var creation = new TaskCompletionSource<IStreamQueueCheckpointer<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.CheckpointerFactory.Configure().Create("shard-1", Arg.Any<CancellationToken>()).Returns(creation.Task);
        using var providerCancellation = new CancellationTokenSource();

        var owner = receiver.GetQueueMessagesAsync(1, TestCancellation);
        var waiter = receiver.GetQueueMessagesAsync(1, TestCancellation);
        providerCancellation.Cancel();
        creation.SetCanceled(providerCancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        Assert.False(TestCancellation.IsCancellationRequested);
        await fixture.CheckpointerFactory.Received(1).Create("shard-1", Arg.Any<CancellationToken>());
        await Assert.Single(fixture.ReceiverClients).DidNotReceive().GetShardIteratorAsync(
            Arg.Any<GetShardIteratorRequest>(), Arg.Any<CancellationToken>());
        Assert.Empty(fixture.Store.Writes);
    }

    [Fact]
    public async Task PooledFactory_ConcurrentReceiverAndCacheRequestsShareOneInstanceAndReassignAfterShutdown()
    {
        await using var fixture = new Fixture();
        await fixture.Factory.CreateAdapter(TestCancellation);
        var queue = Assert.Single(fixture.Factory.GetStreamQueueMapper().GetAllQueues());
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = Enumerable.Range(0, 16).Select(index => Task.Run(async () =>
        {
            await start.Task.WaitAsync(TestCancellation);
            return index % 2 == 0
                ? (object)fixture.Factory.CreateReceiver(queue)
                : fixture.Factory.CreateQueueCache(queue);
        }, TestCancellation)).ToArray();

        start.SetResult();
        var results = await Task.WhenAll(calls);
        var receiver = fixture.GetReceiver();
        Assert.All(results, result => Assert.Same(receiver, result));
        var firstClient = Assert.Single(fixture.ReceiverClients);
        await receiver.Shutdown(TimeSpan.FromSeconds(5));
        var replacement = fixture.GetReceiver();

        Assert.NotSame(receiver, replacement);
        Assert.Same(replacement, fixture.Factory.CreateQueueCache(queue));
        Assert.Equal(2, fixture.ReceiverClients.Count);
        firstClient.Received(1).Dispose();
        fixture.ReceiverClients.Last().DidNotReceive().Dispose();
        await receiver.Shutdown(TimeSpan.FromSeconds(5));
        Assert.Same(replacement, fixture.GetReceiver());
        Assert.Empty(await receiver.GetQueueMessagesAsync(1, TestCancellation));
        fixture.DiscoveryClient.DidNotReceive().Dispose();
        await replacement.Shutdown(TimeSpan.FromSeconds(5));
        Assert.All(fixture.ReceiverClients, client => client.Received(1).Dispose());
        fixture.Factory.Dispose();
        fixture.Factory.Dispose();
        fixture.DiscoveryClient.Received(1).Dispose();
    }

    [Fact]
    public async Task PooledFactory_ByteBudgetStagesUnreadSuffixUntilDeliveredPrefixIsPurged()
    {
        await using var fixture = new Fixture(maxCacheSizeBytes: 1);
        await fixture.Factory.CreateAdapter(TestCancellation);
        var receiver = fixture.GetReceiver();
        var client = Assert.Single(fixture.ReceiverClients);
        fixture.SetReads(client, () => Response("tail",
            fixture.Record("10", "first"),
            fixture.Record("11", "second")));

        var firstNotification = Assert.Single(await receiver.GetQueueMessagesAsync(2, TestCancellation));

        Assert.Equal("10", Assert.IsType<KinesisSequenceToken>(firstNotification.SequenceToken).ShardSequence);
        Assert.True(receiver.IsUnderPressure());
        Assert.Equal(0, receiver.GetMaxAddCount());
        using (var cursor = receiver.GetCacheCursorAtPosition(fixture.StreamId, StreamSubscriptionStartPosition.EarliestAvailable))
        {
            AssertBatch(ReadBatch(cursor), "10", 0, "first");
            Assert.False(cursor.MoveNext());
            var progress = Assert.IsAssignableFrom<IQueueCacheCursorProgress>(cursor);
            progress.RecordDeliverySuccess();
            receiver.UpdateDeliveryProgress(
                Assert.IsType<KinesisSequenceToken>(progress.SafeSequenceToken),
                fixture.Time.GetUtcNow().UtcDateTime);
        }

        Assert.False(receiver.IsUnderPressure());
        Assert.Equal("10", fixture.Store.Checkpoint);
        var secondNotification = Assert.Single(await receiver.GetQueueMessagesAsync(2, TestCancellation));

        Assert.Equal("11", Assert.IsType<KinesisSequenceToken>(secondNotification.SequenceToken).ShardSequence);
        Assert.Single(fixture.ReadRequests);
        using var nextCursor = receiver.GetCacheCursorAtPosition(fixture.StreamId, StreamSubscriptionStartPosition.EarliestAvailable);
        AssertBatch(ReadBatch(nextCursor), "11", 1, "second");
        Assert.False(nextCursor.MoveNext());
        Assert.Equal("10", fixture.Store.Checkpoint);
    }

    [Fact]
    public async Task PooledFactory_CanceledDiscoveryRetriesBeforePublishingReceiver()
    {
        await using var fixture = new Fixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var discoveries = 0;
        fixture.DiscoveryClient.Configure().ListShardsAsync(Arg.Any<ListShardsRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                if (Interlocked.Increment(ref discoveries) == 1)
                {
                    started.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());
                }

                return new ListShardsResponse { Shards = [new Shard { ShardId = "shard-1" }] };
            });
        using var ownerCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestCancellation);
        var initialization = fixture.Factory.CreateAdapter(ownerCancellation.Token);
        await started.Task.WaitAsync(TestCancellation);
        var queue = QueueId.GetQueueId("queue", 0, 0);
        Assert.Throws<InvalidOperationException>(() => fixture.Factory.CreateReceiver(queue));
        Assert.Throws<InvalidOperationException>(() => fixture.Factory.CreateQueueCache(queue));
        Assert.Empty(fixture.ReceiverClients);

        ownerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization);
        await fixture.Factory.CreateAdapter(TestCancellation);

        Assert.Equal(2, discoveries);
        Assert.Same(fixture.GetReceiver(), fixture.Factory.CreateQueueCache(
            Assert.Single(fixture.Factory.GetStreamQueueMapper().GetAllQueues())));
        Assert.Single(fixture.ReceiverClients);
    }

    [Fact]
    public async Task PooledFactory_ShutdownKeepsReceiverReservedUntilInitializationSettles()
    {
        await using var fixture = new Fixture();
        await fixture.Factory.CreateAdapter(TestCancellation);
        var receiver = fixture.GetReceiver();
        var client = Assert.Single(fixture.ReceiverClients);
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<GetShardIteratorResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.GetShardIteratorAsync(Arg.Any<GetShardIteratorRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                started.SetResult(call.Arg<CancellationToken>());
                return release.Task;
            });
        var read = receiver.GetQueueMessagesAsync(1, TestCancellation);
        var initializationToken = await started.Task.WaitAsync(TestCancellation);
        var shutdown = receiver.Shutdown(TimeSpan.FromSeconds(10));
        try
        {
            Assert.True(initializationToken.IsCancellationRequested);
            Assert.False(shutdown.IsCompleted);
            Assert.Same(receiver, fixture.GetReceiver());
            Assert.Single(fixture.ReceiverClients);
            client.DidNotReceive().Dispose();
        }
        finally
        {
            release.TrySetResult(new GetShardIteratorResponse { ShardIterator = "late" });
        }

        await shutdown;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        client.Received(1).Dispose();
        Assert.Empty(fixture.ReadRequests);
        Assert.NotSame(receiver, fixture.GetReceiver());
    }

    [Fact]
    public async Task PooledReceiver_CanceledTopologyDiscoveryReleasesMonitorForReassignedReceiver()
    {
        await using var fixture = new Fixture();
        await fixture.Factory.CreateAdapter(TestCancellation);
        var receiver = fixture.GetReceiver();
        await receiver.Initialize(TimeSpan.FromSeconds(5));
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.DiscoveryClient.Configure().ListShardsAsync(Arg.Any<ListShardsRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var token = call.Arg<CancellationToken>();
                started.SetResult(token);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return null!;
            });
        fixture.Time.Advance(TimeSpan.FromMinutes(1));
        using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestCancellation);
        var read = receiver.GetQueueMessagesAsync(1, readCancellation.Token);
        Assert.Equal(readCancellation.Token, await started.Task.WaitAsync(TestCancellation));

        // The pulling agent owns read cancellation and settles reads before receiver shutdown.
        readCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        await receiver.Shutdown(TimeSpan.FromSeconds(5));

        Assert.Empty(fixture.ReadRequests);
        Assert.Single(fixture.ReceiverClients).Received(1).Dispose();
        fixture.DiscoveryClient.DidNotReceive().Dispose();
        fixture.DiscoveryClient.Configure().ListShardsAsync(Arg.Any<ListShardsRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ListShardsResponse { Shards = [new Shard { ShardId = "shard-1" }] });
        var replacement = fixture.GetReceiver();
        fixture.SetReads(fixture.ReceiverClients.Last(), () => Response("tail", fixture.Record("10", "replacement")));
        Assert.Single(await replacement.GetQueueMessagesAsync(1, TestCancellation));
        await fixture.DiscoveryClient.Received(3).ListShardsAsync(Arg.Any<ListShardsRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PooledReceiver_MixedTypeNonzeroIndicesPersistSafeWatermarkAndRestartAtNextRecord()
    {
        const string previousOffset = "123456789012345678901234567889";
        const string checkpoint = "123456789012345678901234567890";
        const string nextOffset = "123456789012345678901234567891";
        await using var fixture = new Fixture();
        await fixture.Factory.CreateAdapter(TestCancellation);
        var receiver = fixture.GetReceiver();
        var client = Assert.Single(fixture.ReceiverClients);
        var record = fixture.Record(checkpoint, 10, "eleven", 12, "thirteen");
        var wirePayload = record.Data.ToArray();
        fixture.SetReads(client,
            () => Response("mixed-record", fixture.Record(previousOffset, "prefix")),
            () => Response("tail", record));
        await receiver.GetQueueMessagesAsync(1, TestCancellation);
        using (var prefixCursor = receiver.GetCacheCursorAtPosition(fixture.StreamId, StreamSubscriptionStartPosition.EarliestAvailable))
        {
            AssertBatch(ReadBatch(prefixCursor), previousOffset, 0, "prefix");
            var prefixProgress = Assert.IsAssignableFrom<IQueueCacheCursorProgress>(prefixCursor);
            prefixProgress.RecordDeliverySuccess();
            receiver.UpdateDeliveryProgress(
                Assert.IsType<KinesisSequenceToken>(prefixProgress.SafeSequenceToken),
                fixture.Time.GetUtcNow().UtcDateTime);
        }

        Assert.Equal([previousOffset], fixture.Store.Writes);
        var notification = Assert.Single(await receiver.GetQueueMessagesAsync(1, TestCancellation));
        using var cursor = receiver.GetCacheCursorAtPosition(fixture.StreamId, StreamSubscriptionStartPosition.EarliestAvailable);
        var batch = ReadBatch(cursor);
        var progress = Assert.IsAssignableFrom<IQueueCacheCursorProgress>(cursor);

        Assert.Equal([10, 12], batch.GetEvents<int>().Select(item => item.Item1));
        Assert.Equal([0, 2], batch.GetEvents<int>().Select(item => item.Item2.EventIndex));
        Assert.Equal(["eleven", "thirteen"], batch.GetEvents<string>().Select(item => item.Item1));
        Assert.Equal([1, 3], batch.GetEvents<string>().Select(item => item.Item2.EventIndex));
        Assert.All(batch.GetEvents<object>(), item =>
            Assert.Equal(checkpoint, Assert.IsType<KinesisSequenceToken>(item.Item2).ShardSequence));
        Assert.Equal(wirePayload, record.Data.ToArray());
        Assert.True(batch.ImportRequestContext());
        try
        {
            Assert.Equal("trace-42", RequestContext.Get("trace-id"));
        }
        finally
        {
            RequestContext.Clear();
        }

        var enteredLastEvent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLastEvent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new List<(object Value, int EventIndex)>();
        var delivery = Deliver();
        await enteredLastEvent.Task.WaitAsync(TestCancellation);
        try
        {
            Assert.Equal([0, 1, 2], delivered.Select(item => item.EventIndex));
            Assert.Null(progress.SafeSequenceToken);
            await receiver.MessagesDeliveredAsync([notification], TestCancellation);
            Assert.Equal([previousOffset], fixture.Store.Writes);
            Assert.Equal(previousOffset, fixture.Store.Checkpoint);
        }
        finally
        {
            releaseLastEvent.TrySetResult();
        }

        await delivery;
        Assert.Equal([0, 1, 2, 3], delivered.Select(item => item.EventIndex));
        Assert.Equal(new object[] { 10, "eleven", 12, "thirteen" }, delivered.Select(item => item.Value));
        progress.RecordDeliverySuccess();
        var safeToken = Assert.IsType<KinesisSequenceToken>(progress.SafeSequenceToken);
        Assert.Equal(checkpoint, safeToken.ShardSequence);
        receiver.UpdateDeliveryProgress(safeToken, fixture.Time.GetUtcNow().UtcDateTime);
        Assert.Equal(previousOffset, fixture.Store.Checkpoint);
        await receiver.Shutdown(TimeSpan.FromSeconds(5));
        Assert.Equal([previousOffset, checkpoint], fixture.Store.Writes);
        Assert.Equal(checkpoint, fixture.Store.Checkpoint);

        var replacement = fixture.GetReceiver();
        Assert.NotSame(receiver, replacement);
        var replacementClient = fixture.ReceiverClients.Last();
        fixture.SetReads(replacementClient, () => Response("tail", fixture.Record(nextOffset, "next-record")));
        await replacement.GetQueueMessagesAsync(1, TestCancellation);
        using var restartedCursor = replacement.GetCacheCursorAtPosition(fixture.StreamId, StreamSubscriptionStartPosition.EarliestAvailable);
        AssertBatch(ReadBatch(restartedCursor), nextOffset, 0, "next-record");
        Assert.False(restartedCursor.MoveNext());
        await replacementClient.Received(1).GetShardIteratorAsync(
            Arg.Is<GetShardIteratorRequest>(request =>
                request.ShardIteratorType == ShardIteratorType.AFTER_SEQUENCE_NUMBER
                && request.StartingSequenceNumber == checkpoint),
            Arg.Any<CancellationToken>());
        Assert.Equal(2, fixture.Store.LoadCount);

        async Task Deliver()
        {
            foreach (var item in batch.GetEvents<object>())
            {
                if (item.Item2.EventIndex == 3)
                {
                    enteredLastEvent.SetResult();
                    await releaseLastEvent.Task.WaitAsync(TestCancellation);
                }

                delivered.Add((item.Item1, item.Item2.EventIndex));
            }
        }
    }

    [Fact]
    public async Task Source_ReadReturnsOrderedRawRecordMetadataWithoutDecodingOrMutatingWirePayload()
    {
        await using var fixture = new Fixture();
        var client = Substitute.For<IAmazonKinesis>();
        client.GetShardIteratorAsync(Arg.Any<GetShardIteratorRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GetShardIteratorResponse { ShardIterator = "source-iterator" });
        var first = fixture.Record("999999999999999999999999999999", 10, "eleven");
        var second = fixture.Record("1000000000000000000000000000000", "second");
        first.Data.Position = 1;
        var payload = first.Data.ToArray();
        fixture.SetReads(client, () => Response("next", first, second));
        var source = new KinesisRecoverableStreamSource(
            client, "stream", "shard-1",
            new KinesisShardTopologyMonitor(fixture.DiscoveryClient, "stream", ["shard-1"],
                TimeSpan.FromMinutes(1), fixture.Time, NullLogger<KinesisShardTopologyMonitor>.Instance),
            TimeSpan.Zero, fixture.Time);
        try
        {
            await source.Initialize(new RecoverableStreamStartPosition(null, false), TestCancellation);
            var records = await source.Read(2, TestCancellation);

            Assert.Equal([0L, 1L], records.Select(record => record.SequenceNumber));
            Assert.Collection(records,
                record => Assert.Same(first, record.Record),
                record => Assert.Same(second, record.Record));
            Assert.All(records, record => Assert.Null(record.Body));
            Assert.Equal(payload, records[0].RawPayload);
            Assert.Equal(1, first.Data.Position);
            Assert.Equal("partition-key", first.PartitionKey);
            Assert.Equal(fixture.Time.GetUtcNow().UtcDateTime, first.ApproximateArrivalTimestamp);
            var body = fixture.Serializer.Deserialize(records[0].RawPayload);
            Assert.Equal(fixture.StreamId, body.StreamId);
            Assert.Equal(new object[] { 10, "eleven" }, body.Events);
            Assert.Equal("trace-42", body.RequestContext!["trace-id"]);
            Assert.Equal(payload, fixture.Serializer.SerializeToArray(body));
            var request = Assert.Single(fixture.ReadRequests);
            Assert.Equal("source-iterator", request.Iterator);
            Assert.Equal(2, request.Limit);
        }
        finally
        {
            await source.Shutdown(TestCancellation);
        }

        client.Received(1).Dispose();
    }

    private static KinesisBatchContainer ReadBatch(IQueueCacheCursor cursor)
    {
        Assert.True(cursor.MoveNext());
        var batch = Assert.IsType<KinesisBatchContainer>(cursor.GetCurrent(out var exception));
        Assert.Null(exception);
        return batch;
    }

    private static void AssertBatch(KinesisBatchContainer batch, string offset, long ordinal, string value)
    {
        Assert.Equal(offset, batch.Token.ShardSequence);
        Assert.Equal(ordinal, batch.Token.SequenceNumber);
        Assert.Equal(value, Assert.Single(batch.GetEvents<string>()).Item1);
    }

    private static GetRecordsResponse Response(string nextIterator, params KinesisRecord[] records)
        => new() { NextShardIterator = nextIterator, Records = [.. records] };

    private sealed class FailingPayloadStream(IOException failure) : MemoryStream
    {
        public override byte[] ToArray() => throw failure;
    }

    private sealed class CheckpointStore(string checkpoint) : IStreamCheckpointStore
    {
        public string Checkpoint { get; private set; } = checkpoint;
        public int LoadCount { get; private set; }
        public List<string> Writes { get; } = [];

        public ValueTask<StreamCheckpointStoreState> Load(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadCount++;
            return ValueTask.FromResult(new StreamCheckpointStoreState(Checkpoint, Checkpoint));
        }

        public ValueTask<StreamCheckpointStoreState> Update(string checkpoint, string expectedVersion, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(Checkpoint, expectedVersion);
            Checkpoint = checkpoint;
            Writes.Add(checkpoint);
            return ValueTask.FromResult(new StreamCheckpointStoreState(checkpoint, checkpoint));
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        private readonly HashSet<KinesisPooledAdapterReceiver> _receivers = [];
        private int _clientCount;

        public Fixture(string checkpoint = "", long? maxCacheSizeBytes = null)
        {
            Store = new(checkpoint);
            Serializer = _services.GetRequiredService<Serializer<KinesisBatchContainer.Body>>();
            DiscoveryClient.ListShardsAsync(Arg.Any<ListShardsRequest>(), Arg.Any<CancellationToken>())
                .Returns(new ListShardsResponse { Shards = [new Shard { ShardId = "shard-1" }] });
            CheckpointerFactory.Create("shard-1", Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult(CreateCheckpointer()));
            var options = new KinesisStreamOptions
            {
                StreamName = "stream",
                GetRecordsInterval = TimeSpan.Zero,
                TopologyCheckInterval = TimeSpan.FromMinutes(1),
            };
            if (maxCacheSizeBytes is { } byteBudget)
            {
                options.MaxCacheSizeBytes = byteBudget;
            }

            Factory = new KinesisAdapterFactory(
                "Kinesis",
                options,
                new SimpleQueueCacheOptions { CacheSize = 8 },
                Serializer, CheckpointerFactory, NullLoggerFactory.Instance, Time, CreateClient);
        }

        public StreamId StreamId { get; } = StreamId.Create("pooled-runtime", "stream-key");
        public FakeTimeProvider Time { get; } = new();
        public IAmazonKinesis DiscoveryClient { get; } = Substitute.For<IAmazonKinesis>();
        public ConcurrentQueue<IAmazonKinesis> ReceiverClients { get; } = new();
        public IStreamQueueCheckpointerFactory CheckpointerFactory { get; } = Substitute.For<IStreamQueueCheckpointerFactory>();
        public Serializer<KinesisBatchContainer.Body> Serializer { get; }
        public KinesisAdapterFactory Factory { get; }
        public CheckpointStore Store { get; }
        public List<(string Iterator, int? Limit)> ReadRequests { get; } = [];

        public IStreamQueueCheckpointer<string> CreateCheckpointer()
            => new StreamQueueCheckpointer(Store, new StreamQueueCheckpointerOptions
            {
                PersistInterval = TimeSpan.FromMinutes(1),
                CheckpointComparer = StreamCheckpointComparers.Numeric,
            });

        public KinesisPooledAdapterReceiver GetReceiver()
        {
            var queue = Assert.Single(Factory.GetStreamQueueMapper().GetAllQueues());
            var receiver = Assert.IsType<KinesisPooledAdapterReceiver>(Factory.CreateReceiver(queue));
            Assert.Same(receiver, Factory.CreateQueueCache(queue));
            _receivers.Add(receiver);
            return receiver;
        }

        public KinesisRecord Record(string offset, params object[] events)
            => new()
            {
                SequenceNumber = offset,
                Data = new MemoryStream(KinesisBatchContainer.ToKinesisPayload(
                    Serializer, StreamId, events, new Dictionary<string, object> { ["trace-id"] = "trace-42" })),
                PartitionKey = "partition-key",
                ApproximateArrivalTimestamp = Time.GetUtcNow().UtcDateTime,
            };

        public void SetReads(IAmazonKinesis client, params Func<GetRecordsResponse>[] responses)
        {
            var pending = new Queue<Func<GetRecordsResponse>>(responses);
            client.Configure().GetRecordsAsync(Arg.Any<GetRecordsRequest>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var request = call.Arg<GetRecordsRequest>();
                    ReadRequests.Add((request.ShardIterator, request.Limit));
                    return Task.FromResult(pending.Dequeue()());
                });
        }

        private IAmazonKinesis CreateClient()
        {
            if (Interlocked.Increment(ref _clientCount) == 1)
            {
                return DiscoveryClient;
            }

            var client = Substitute.For<IAmazonKinesis>();
            client.GetShardIteratorAsync(Arg.Any<GetShardIteratorRequest>(), Arg.Any<CancellationToken>())
                .Returns(new GetShardIteratorResponse { ShardIterator = "iterator" });
            ReceiverClients.Enqueue(client);
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                foreach (var receiver in _receivers)
                {
                    await receiver.Shutdown(TimeSpan.FromSeconds(5));
                }
            }
            finally
            {
                Factory.Dispose();
                await _services.DisposeAsync();
            }
        }
    }
}
