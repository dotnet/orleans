using System.Globalization;
using System.Net;
using Azure.Messaging.EventHubs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Internal;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Runtime.Scheduler;
using Orleans.Serialization;
using Orleans.Statistics;
using Orleans.Streams;
using Orleans.Streams.Filtering;
using Orleans.Streaming.EventHubs;
using Orleans.Streaming.EventHubs.Testing;
using Orleans.Timers;
using Xunit;

namespace ServiceBus.Tests;

[TestSuite("BVT")]
[TestProvider("EventHub")]
[TestArea("Streaming")]
[TestCategory("EventHub"), TestCategory("Streaming")]
public sealed class CacheMemoryTests : IDisposable
{
    private readonly ServiceProvider serviceProvider;
    private readonly Serializer serializer;

    public CacheMemoryTests()
    {
        serviceProvider = new ServiceCollection()
            .AddMetrics()
            .AddSingleton<OrleansInstruments>()
            .AddSingleton<CatalogInstruments>()
            .AddSingleton<SchedulerInstruments>()
            .AddSingleton<GrainInstruments>()
            .AddSingleton<MessagingInstruments>()
            .AddSingleton<MessagingProcessingInstruments>()
            .AddSerializer()
            .BuildServiceProvider();
        serializer = serviceProvider.GetRequiredService<Serializer>();
    }

    [Fact, TestCategory("BVT")]
    public void CacheMemoryOptionsHaveBoundedDefaults()
    {
        var options = new EventHubStreamCacheMemoryOptions();

        Assert.Equal(512L * 1024 * 1024, options.MaxActiveCacheMemory);
        Assert.Equal(64L * 1024 * 1024, options.MaxBufferPoolMemory);
        new EventHubStreamCacheMemoryOptionsValidator(options, "test").ValidateConfiguration();
    }

    [Fact, TestCategory("BVT")]
    public void BufferPoolUsesSizeClassesAndBoundsIdleMemory()
    {
        var controller = new EventHubCacheMemoryController(1024 * 1024);
        var pool = new EventHubCacheBufferPool(controller, 64 * 1024, null, TimeSpan.FromMinutes(1));

        var small = pool.Allocate(1);
        var medium = pool.Allocate(100 * 1024);

        Assert.Equal(64 * 1024, small.SizeInByte);
        Assert.Equal(128 * 1024, medium.SizeInByte);
        Assert.Equal(192 * 1024, pool.ActiveMemory);
        Assert.Equal(pool.ActiveMemory, controller.ActiveCacheMemory);

        small.Dispose();
        medium.Dispose();

        Assert.Equal(0, pool.ActiveMemory);
        Assert.Equal(64 * 1024, pool.PooledMemory);

        var reused = pool.Allocate(1);
        Assert.Same(small, reused);
        reused.Dispose();
    }

    [Fact, TestCategory("BVT")]
    public void BufferPoolAccountingConvergesAfterConcurrentAllocationAndRelease()
    {
        const int maxPooledMemory = 2 * 1024 * 1024;
        var controller = new EventHubCacheMemoryController(32 * 1024 * 1024);
        var pool = new EventHubCacheBufferPool(controller, maxPooledMemory, null, TimeSpan.FromMinutes(1));
        var buffers = new FixedSizeBuffer[128];

        Parallel.For(0, buffers.Length, i =>
        {
            var minimumSize = 1 << (16 + i % 5);
            buffers[i] = pool.Allocate(minimumSize);
        });

        var expectedActiveMemory = buffers.Sum(static buffer => (long)buffer.SizeInByte);
        Assert.Equal(expectedActiveMemory, pool.ActiveMemory);
        Assert.Equal(expectedActiveMemory, controller.ActiveCacheMemory);

        Parallel.ForEach(buffers, static buffer => buffer.Dispose());

        Assert.Equal(0, pool.ActiveMemory);
        Assert.Equal(0, controller.ActiveCacheMemory);
        Assert.InRange(pool.PooledMemory, 0, maxPooledMemory);
    }

    [Fact, TestCategory("BVT")]
    public void ProviderMemoryLimitAggregatesPartitionsAndRecoversAfterPurge()
    {
        const int maxActiveMemory = 450 * 1024;
        var controller = new EventHubCacheMemoryController(maxActiveMemory);
        var pool = new EventHubCacheBufferPool(controller, 64 * 1024, null, TimeSpan.FromMinutes(1));
        using var first = CreateCache("0", pool, controller);
        using var second = CreateCache("1", pool, controller);

        first.Add([MakeEventData(0, 80 * 1024), MakeEventData(1, 80 * 1024)], DateTime.UtcNow);
        Assert.True(first.GetMaxAddCount() > 0);

        // This simulates the batch which was already received when another partition crossed the watermark.
        second.Add([MakeEventData(2, 80 * 1024)], DateTime.UtcNow);
        Assert.True(controller.ActiveCacheMemory > maxActiveMemory);
        Assert.True(first.GetMaxAddCount() > 0);
        Assert.True(controller.ActiveCacheMemory < maxActiveMemory);
        Assert.True(controller.ActiveCacheMemory > 300 * 1024);
        Assert.True(second.GetMaxAddCount() > 0);
    }

    [Fact, TestCategory("BVT")]
    public void MemoryPressurePurgeStopsAtSlowConsumerDeliveryBoundary()
    {
        const int maxActiveMemory = 100 * 1024;
        var controller = new EventHubCacheMemoryController(maxActiveMemory);
        var pool = new EventHubCacheBufferPool(controller, 64 * 1024, null, TimeSpan.FromMinutes(1));
        var checkpointer = new TestCheckpointer();
        var evictionStrategy = new EventHubQueueCacheFactory.EventHubCacheEvictionStrategy(
            NullLogger.Instance, new AlwaysPurgePredicate(), null, null);
        using var cache = CreateCache("0", pool, controller, evictionStrategy, checkpointer);
        var positions = cache.Add(
            Enumerable.Range(0, 20).Select(i => MakeEventData(i, 8 * 1024)).ToList(),
            DateTime.UtcNow);
        Assert.True(controller.ActiveCacheMemory > maxActiveMemory);

        cache.UpdatePurgeProtection(hasActiveSubscriptions: true);

        var retainedMemory = controller.ActiveCacheMemory;
        Assert.Equal(0, cache.GetMaxAddCount());
        cache.SignalPurge();
        Assert.Equal(retainedMemory, controller.ActiveCacheMemory);
        Assert.Null(checkpointer.LastOffset);
        var nextCursor = GetCursor(cache, positions[0].StreamId, positions[0].SequenceToken);
        for (var i = 0; i < positions.Count; i++)
        {
            var next = GetNextMessage(cache, nextCursor);
            Assert.Equal(positions[i].SequenceToken, next.SequenceToken);
        }
        AssertNoNextMessage(cache, nextCursor);

        var laggingCursor = GetCursor(cache, positions[3].StreamId, positions[3].SequenceToken);
        cache.AddCachePressureMonitor(new AlwaysPressureMonitor());
        cache.UpdateDeliveryProgress(positions[2].SequenceToken, DateTime.UtcNow);
        Assert.Equal(((IEventHubPartitionLocation)positions[2].SequenceToken).EventHubOffset, checkpointer.LastOffset);
        Assert.Equal(0, cache.GetMaxAddCount());

        for (var i = 3; i < positions.Count; i++)
        {
            Assert.Equal(positions[i].SequenceToken, GetNextMessage(cache, laggingCursor).SequenceToken);
        }

        AssertNoNextMessage(cache, laggingCursor);
    }

    [Fact, TestCategory("BVT")]
    public void TimeBasedPurgeContinuesWithRegisteredSubscriptionsBelowMemoryLimit()
    {
        var controller = new EventHubCacheMemoryController(1024 * 1024);
        var pool = new EventHubCacheBufferPool(controller, 0, null, TimeSpan.FromMinutes(1));
        var checkpointer = new TestCheckpointer();
        var evictionStrategy = new EventHubQueueCacheFactory.EventHubCacheEvictionStrategy(
            NullLogger.Instance, new AlwaysPurgePredicate(), null, null);
        using var cache = CreateCache("0", pool, controller, evictionStrategy, checkpointer);
        var position = Assert.Single(cache.Add([MakeEventData(0)], DateTime.UtcNow));
        cache.UpdatePurgeProtection(hasActiveSubscriptions: true);
        Assert.False(controller.IsUnderPressure);

        cache.SignalPurge();

        Assert.Equal(((IEventHubPartitionLocation)position.SequenceToken).EventHubOffset, checkpointer.LastOffset);
        Assert.Equal(0, controller.ActiveCacheMemory);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [TestCategory("BVT")]
    public void MemoryPressurePreservesUntrackedAdapterStorage(
        bool nullArray, bool hasActiveSubscriptions, bool trackedTail)
    {
        var controller = new EventHubCacheMemoryController(1);
        var pool = new EventHubCacheBufferPool(controller, 0, null, TimeSpan.FromMinutes(1));
        var checkpointer = new TestCheckpointer();
        var adapter = new UntrackedStorageEventHubDataAdapter(serializer, nullArray);
        using var cache = CreateCache("0", pool, controller, checkpointer: checkpointer, adapter: adapter);
        var messages = new List<EventData> { MakeSerializedEventData(0, 2 * 1024) };
        if (trackedTail)
        {
            messages.Add(MakeSerializedEventData(1, 80 * 1024));
        }

        var positions = cache.Add(messages, DateTime.UtcNow);
        cache.UpdatePurgeProtection(hasActiveSubscriptions);
        var retainedMemory = controller.ActiveCacheMemory;
        var expectedBufferMemory = trackedTail ? 128 * 1024 : 0;
        Assert.True(controller.IsUnderPressure);
        Assert.Equal(expectedBufferMemory, pool.ActiveMemory);

        cache.SignalPurge();
        if (hasActiveSubscriptions)
        {
            cache.UpdateDeliveryProgress(positions[0].SequenceToken, DateTime.UtcNow);
        }

        Assert.Equal(retainedMemory, controller.ActiveCacheMemory);
        Assert.Equal(expectedBufferMemory, pool.ActiveMemory);
        Assert.Null(checkpointer.LastOffset);
        Assert.Equal(0, cache.GetMaxAddCount());
        var cursor = GetCursor(cache, positions[0].StreamId, positions[0].SequenceToken);
        foreach (var position in positions)
        {
            var message = GetNextMessage(cache, cursor);
            Assert.Equal(position.SequenceToken, message.SequenceToken);
            Assert.All(Assert.Single(message.GetEvents<byte[]>()).Item1,
                value => Assert.Equal((byte)position.SequenceToken.SequenceNumber, value));
        }

        AssertNoNextMessage(cache, cursor);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [TestCategory("BVT")]
    public void MemoryPressureStopsAtUntrackedStorageAfterTrackedPrefix(bool nullArray, bool sharedBuffer)
    {
        var controller = new EventHubCacheMemoryController(100 * 1024);
        var pool = new EventHubCacheBufferPool(controller, 0, null, TimeSpan.FromMinutes(1));
        var checkpointer = new TestCheckpointer();
        using var cache = CreateCache("0", pool, controller, checkpointer: checkpointer,
            adapter: new UntrackedStorageEventHubDataAdapter(serializer, nullArray, untrackedSequence: 1));
        var positions = cache.Add(
            [MakeSerializedEventData(0, 80 * 1024), MakeSerializedEventData(1, 2 * 1024),
                MakeSerializedEventData(2, (sharedBuffer ? 2 : 160) * 1024)],
            DateTime.UtcNow);
        cache.UpdatePurgeProtection(hasActiveSubscriptions: false);
        var expectedBufferMemory = (sharedBuffer ? 128 : 128 + 256) * 1024;
        Assert.Equal(expectedBufferMemory, pool.ActiveMemory);

        cache.SignalPurge();

        Assert.Equal("0", checkpointer.LastOffset);
        Assert.Equal(expectedBufferMemory, pool.ActiveMemory);
        var retainedMemory = controller.ActiveCacheMemory;
        Assert.Equal(0, cache.GetMaxAddCount());
        Assert.Equal(retainedMemory, controller.ActiveCacheMemory);
        Assert.Equal("0", checkpointer.LastOffset);
        var cursor = GetCursor(cache, positions[1].StreamId, positions[1].SequenceToken);
        foreach (var position in positions.Skip(1))
        {
            var message = GetNextMessage(cache, cursor);
            Assert.Equal(position.SequenceToken, message.SequenceToken);
            Assert.All(Assert.Single(message.GetEvents<byte[]>()).Item1,
                value => Assert.Equal((byte)position.SequenceToken.SequenceNumber, value));
        }

        AssertNoNextMessage(cache, cursor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [TestCategory("BVT")]
    public void TimeBasedPurgeRetainsCustomAdapterEvictionPolicy(bool nullArray)
    {
        var controller = new EventHubCacheMemoryController(1024 * 1024);
        var pool = new EventHubCacheBufferPool(controller, 0, null, TimeSpan.FromMinutes(1));
        var checkpointer = new TestCheckpointer();
        var evictionStrategy = new EventHubQueueCacheFactory.EventHubCacheEvictionStrategy(
            NullLogger.Instance, new AlwaysPurgePredicate(), null, null);
        using var cache = CreateCache("0", pool, controller, evictionStrategy, checkpointer,
            new UntrackedStorageEventHubDataAdapter(serializer, nullArray));
        cache.Add([MakeSerializedEventData(0, 2 * 1024)], DateTime.UtcNow);
        cache.UpdatePurgeProtection(hasActiveSubscriptions: true);
        Assert.False(controller.IsUnderPressure);

        cache.SignalPurge();

        Assert.Equal("0", checkpointer.LastOffset);
        Assert.Equal(0, controller.ActiveCacheMemory);
    }

    [Fact, TestCategory("BVT")]
    public void MemoryPressurePreservesSharedTailAtPartialDeliveryBoundary()
    {
        var controller = new EventHubCacheMemoryController(100 * 1024);
        var pool = new EventHubCacheBufferPool(controller, 0, null, TimeSpan.FromMinutes(1));
        var checkpointer = new TestCheckpointer();
        using var cache = CreateCache("0", pool, controller, checkpointer: checkpointer);
        var positions = cache.Add(
            [MakeSerializedEventData(0, 80 * 1024, "first"), MakeSerializedEventData(1, 2 * 1024, "second")],
            DateTime.UtcNow);
        Assert.NotEqual(positions[0].StreamId, positions[1].StreamId);
        cache.UpdatePurgeProtection(hasActiveSubscriptions: true);
        var firstCursor = GetCursor(cache, positions[0].StreamId, positions[0].SequenceToken);
        Assert.Equal(positions[0].SequenceToken, GetNextMessage(cache, firstCursor).SequenceToken);
        AssertNoNextMessage(cache, firstCursor);
        var tailCursor = GetCursor(cache, positions[1].StreamId, positions[1].SequenceToken);
        Assert.Equal(128 * 1024, pool.ActiveMemory);

        cache.UpdateDeliveryProgress(positions[0].SequenceToken, DateTime.UtcNow);

        Assert.Equal("0", checkpointer.LastOffset);
        Assert.Equal(0, cache.GetMaxAddCount());
        Assert.Equal(128 * 1024, pool.ActiveMemory);
        var tail = GetNextMessage(cache, tailCursor);
        Assert.Equal(positions[1].SequenceToken, tail.SequenceToken);
        Assert.All(Assert.Single(tail.GetEvents<byte[]>()).Item1, value => Assert.Equal((byte)1, value));
        AssertNoNextMessage(cache, tailCursor);

        cache.UpdateDeliveryProgress(positions[1].SequenceToken, DateTime.UtcNow);

        Assert.Equal("1", checkpointer.LastOffset);
        Assert.True(cache.GetMaxAddCount() > 0);
        Assert.Equal(0, controller.ActiveCacheMemory);
    }

    [Theory]
    [InlineData(StreamSubscriptionStartPosition.Latest, false)]
    [InlineData(StreamSubscriptionStartPosition.EarliestAvailable, false)]
    [InlineData(null, false)]
    [InlineData(StreamSubscriptionStartPosition.Latest, true)]
    [TestCategory("BVT")]
    public async Task ExplicitStartPositionControlsPressureRecoveryBeforeFirstDelivery(
        StreamSubscriptionStartPosition? startPosition,
        bool shutdownBeforePump)
    {
        var controller = new EventHubCacheMemoryController(100 * 1024);
        var pool = new EventHubCacheBufferPool(controller, 0, null, TimeSpan.FromMinutes(1));
        var checkpointer = new TestCheckpointer();
        using var cache = CreateCache("0", pool, controller, checkpointer: checkpointer);
        var broker = Substitute.For<IEventHubReceiver>();
        var pendingMessages = new Queue<List<EventData>>();
        var readCount = 0;
        broker.ReceiveAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                readCount++;
                return Task.FromResult<IEnumerable<EventData>>(pendingMessages.TryDequeue(out var messages) ? messages : []);
            });
        var receiver = CreatePressureReceiver(cache, broker);
        var queueId = QueueId.GetQueueId("pressure", 0, 0);
        var agent = CreatePressureAgent(receiver, queueId);
        var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
        await agent.RunOrQueueTask(() => agent.Initialize(TestContext.Current.CancellationToken));
        var positions = cache.Add(
            Enumerable.Range(0, 20).Select(i => MakeSerializedEventData(i, 8 * 1024)).ToList(),
            DateTime.UtcNow);
        var streamId = new QualifiedStreamId("provider", positions[0].StreamId);
        await accessor.RegisterStream(streamId, positions[0].SequenceToken, DateTime.UtcNow);
        var stream = Assert.Single(await accessor.GetPubSubCache()).Value;
        var deliveries = new List<long>();
        var errors = new List<Exception>();
        var handshakeToken = startPosition is { } position
            ? StreamHandshakeToken.CreateStartPositionToken(position)
            : StreamHandshakeToken.CreateStartToken(positions[0].SequenceToken);
        var consumer = new PressureConsumer(
            batch =>
            {
                deliveries.Add(batch.SequenceToken.SequenceNumber);
                return Task.FromResult<StreamHandshakeToken?>(null);
            },
            errors,
            handshakeToken);
        var data = stream.AddConsumer(
            GuidId.GetGuidId(SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid())),
            streamId, consumer, null, DateTime.UtcNow);
        Assert.True(await accessor.DoHandshakeWithConsumer(data, cacheToken: null));
        data.IsRegistered = true;
        Assert.Null(data.LastProcessedToken);
        Assert.Equal(StreamConsumerDataState.Inactive, data.State);
        Assert.True(controller.IsUnderPressure);

        if (shutdownBeforePump)
        {
            await accessor.Shutdown();
            Assert.Null(checkpointer.LastOffset);
            Assert.Null(data.LastProcessedToken);
            Assert.Empty(deliveries);
            return;
        }

        await accessor.RunQueuePump(queueId, TestContext.Current.CancellationToken);
        await accessor.RunQueuePump(queueId, TestContext.Current.CancellationToken);

        Assert.Empty(deliveries);
        Assert.Null(data.LastProcessedToken);
        if (startPosition == StreamSubscriptionStartPosition.Latest)
        {
            Assert.Equal(1, readCount);
            Assert.Equal("19", checkpointer.LastOffset);
            Assert.False(controller.IsUnderPressure);
        }
        else
        {
            Assert.Equal(0, readCount);
            Assert.Null(checkpointer.LastOffset);
            Assert.True(controller.IsUnderPressure);
            await accessor.RunConsumerCursor(data);
            Assert.Equal(Enumerable.Range(0, 20).Select(i => (long)i), deliveries);
        }

        pendingMessages.Enqueue([MakeSerializedEventData(20, 2 * 1024)]);
        await accessor.RunQueuePump(queueId, TestContext.Current.CancellationToken);
        await accessor.RunQueuePump(queueId, TestContext.Current.CancellationToken);

        Assert.Equal(startPosition == StreamSubscriptionStartPosition.Latest
            ? [20L]
            : Enumerable.Range(0, 21).Select(i => (long)i), deliveries);
        Assert.NotNull(data.LastProcessedToken);
        Assert.Equal(20, data.LastProcessedToken.SequenceNumber);
        Assert.Empty(errors);
        await accessor.Shutdown();
    }

    [Fact, TestCategory("BVT")]
    public async Task LatestPressureBoundaryPreservesPostRegistrationMessages()
    {
        var controller = new EventHubCacheMemoryController(100 * 1024);
        var pool = new EventHubCacheBufferPool(controller, 0, null, TimeSpan.FromMinutes(1));
        var checkpointer = new TestCheckpointer();
        using var cache = CreateCache("0", pool, controller, checkpointer: checkpointer);
        var broker = Substitute.For<IEventHubReceiver>();
        broker.ReceiveAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IEnumerable<EventData>>([]));
        var receiver = CreatePressureReceiver(cache, broker);
        var queueId = QueueId.GetQueueId("pressure", 0, 0);
        var agent = CreatePressureAgent(receiver, queueId);
        var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
        await agent.RunOrQueueTask(() => agent.Initialize(TestContext.Current.CancellationToken));
        var first = Assert.Single(cache.Add([MakeSerializedEventData(0, 80 * 1024)], DateTime.UtcNow));
        var streamId = new QualifiedStreamId("provider", first.StreamId);
        await accessor.RegisterStream(streamId, first.SequenceToken, DateTime.UtcNow);
        var stream = Assert.Single(await accessor.GetPubSubCache()).Value;
        var deliveries = new List<long>();
        var errors = new List<Exception>();
        var consumer = new PressureConsumer(
            batch =>
            {
                deliveries.Add(batch.SequenceToken.SequenceNumber);
                return Task.FromResult<StreamHandshakeToken?>(null);
            },
            errors,
            StreamHandshakeToken.CreateStartPositionToken(StreamSubscriptionStartPosition.Latest));
        var data = stream.AddConsumer(
            GuidId.GetGuidId(SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid())),
            streamId, consumer, null, DateTime.UtcNow);
        Assert.True(await accessor.DoHandshakeWithConsumer(data, cacheToken: null));
        data.IsRegistered = true;
        var next = Assert.Single(cache.Add([MakeSerializedEventData(1, 2 * 1024)], DateTime.UtcNow));
        Assert.NotNull(data.Cursor);
        data.Cursor.Refresh(next.SequenceToken);

        await accessor.RunQueuePump(queueId, TestContext.Current.CancellationToken);

        Assert.Equal("0", checkpointer.LastOffset);
        Assert.Null(data.LastProcessedToken);
        Assert.Empty(deliveries);
        Assert.Equal(128 * 1024, pool.ActiveMemory);
        await broker.DidNotReceive().ReceiveAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());

        await accessor.RunConsumerCursor(data);
        await accessor.RunQueuePump(queueId, TestContext.Current.CancellationToken);

        Assert.Equal([1L], deliveries);
        Assert.Equal(next.SequenceToken, data.LastProcessedToken);
        Assert.Equal("1", checkpointer.LastOffset);
        Assert.False(controller.IsUnderPressure);
        Assert.Empty(errors);
        await accessor.Shutdown();
    }

    [Theory, InlineData(false), InlineData(true), TestCategory("BVT")]
    public async Task RegisteredSubscriptionsResumeIngestionAfterMemoryPressure(bool holdSlowDelivery)
    {
        const int maxActiveMemory = 100 * 1024;
        const int messageCount = 20;
        var controller = new EventHubCacheMemoryController(maxActiveMemory);
        var pool = new EventHubCacheBufferPool(controller, 64 * 1024, null, TimeSpan.FromMinutes(1));
        using var cache = CreateCache("0", pool, controller);
        var broker = Substitute.For<IEventHubReceiver>();
        var pendingMessages = new Queue<List<EventData>>();
        var memoryAtReads = new List<long>();
        broker.ReceiveAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                memoryAtReads.Add(controller.ActiveCacheMemory);
                return Task.FromResult<IEnumerable<EventData>>(pendingMessages.TryDequeue(out var messages) ? messages : []);
            });
        var receiver = CreatePressureReceiver(cache, broker);
        var queueId = QueueId.GetQueueId("pressure", 0, 0);
        var agent = CreatePressureAgent(receiver, queueId);
        var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
        await agent.RunOrQueueTask(() => agent.Initialize(TestContext.Current.CancellationToken));
        var positions = cache.Add(
            Enumerable.Range(0, messageCount).Select(i => MakeSerializedEventData(i, 8 * 1024)).ToList(),
            DateTime.UtcNow);
        var streamId = new QualifiedStreamId("provider", positions[0].StreamId);
        await accessor.RegisterStream(streamId, positions[0].SequenceToken, DateTime.UtcNow);
        var stream = Assert.Single(await accessor.GetPubSubCache()).Value;
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSlow = new TaskCompletionSource<StreamHandshakeToken?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!holdSlowDelivery)
        {
            releaseSlow.SetResult(null);
        }

        var deliveries = new[] { new List<long>(), new List<long>() };
        var errors = new List<Exception>();
        var consumers = new StreamConsumerData[2];
        for (var i = 0; i < consumers.Length; i++)
        {
            var index = i;
            var consumer = new PressureConsumer(
                batch =>
                {
                    var payload = Assert.Single(batch.GetEvents<byte[]>()).Item1;
                    Assert.All(payload, value => Assert.Equal((byte)batch.SequenceToken.SequenceNumber, value));
                    deliveries[index].Add(batch.SequenceToken.SequenceNumber);
                    if (index == 1)
                    {
                        slowStarted.TrySetResult();
                        return releaseSlow.Task;
                    }

                    return Task.FromResult<StreamHandshakeToken?>(null);
                },
                errors);
            var data = stream.AddConsumer(GuidId.GetGuidId(Guid.NewGuid()), streamId, consumer, null, DateTime.UtcNow);
            data.IsRegistered = true;
            data.Cursor = ((IQueueCache)receiver).TryGetCacheCursor(streamId.StreamId, positions[0].SequenceToken).Cursor;
            Assert.NotNull(data.Cursor);
            consumers[i] = data;
        }

        Assert.True(controller.IsUnderPressure);
        await accessor.RunConsumerCursor(consumers[0]);
        var slowDelivery = accessor.RunConsumerCursor(consumers[1]);
        await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        if (holdSlowDelivery)
        {
            var retainedMemory = controller.ActiveCacheMemory;
            await accessor.RunQueuePump(queueId, TestContext.Current.CancellationToken);
            Assert.Empty(memoryAtReads);
            Assert.Equal(retainedMemory, controller.ActiveCacheMemory);
            Assert.Equal(StreamConsumerDataState.Active, consumers[1].State);
            releaseSlow.SetResult(null);
        }

        await slowDelivery.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.All(consumers, consumer =>
        {
            Assert.Equal(StreamConsumerDataState.Inactive, consumer.State);
            Assert.Equal(positions[^1].SequenceToken, consumer.LastProcessedToken);
        });
        pendingMessages.Enqueue([MakeSerializedEventData(messageCount, 2 * 1024)]);
        for (var attempt = 0; attempt < 3 && memoryAtReads.Count == 0; attempt++)
        {
            await accessor.RunQueuePump(queueId, TestContext.Current.CancellationToken);
        }

        Assert.NotEmpty(memoryAtReads);
        Assert.All(memoryAtReads, memory => Assert.InRange(memory, 0, maxActiveMemory - 1));
        Assert.False(controller.IsUnderPressure);
        Assert.Equal(2, stream.Count);
        Assert.All(consumers, consumer => Assert.True(consumer.IsRegistered));
        Assert.All(deliveries, delivered => Assert.Equal(Enumerable.Range(0, messageCount + 1).Select(i => (long)i), delivered));
        Assert.Empty(errors);
        await accessor.Shutdown();
    }

    [Fact, TestCategory("BVT")]
    public void SparseCachesRemainSmallAcross1024Partitions()
    {
        var options = new EventHubStreamCacheMemoryOptions();
        var controller = new EventHubCacheMemoryController(options.MaxActiveCacheMemory);
        var pool = new EventHubCacheBufferPool(controller, options.MaxBufferPoolMemory, null, TimeSpan.FromMinutes(1));
        var caches = new List<EventHubQueueCache>(1024);
        long memoryPerSparseCache = 0;

        try
        {
            for (var i = 0; i < 1024; i++)
            {
                var cache = CreateCache(i.ToString(CultureInfo.InvariantCulture), pool, controller);
                cache.Add([MakeEventData(i)], DateTime.UtcNow);
                caches.Add(cache);
                if (i == 0)
                {
                    memoryPerSparseCache = controller.ActiveCacheMemory;
                }
            }

            Assert.InRange(memoryPerSparseCache, EventHubCacheBufferPool.MinBufferSize + 1L, 96L * 1024);
            Assert.Equal(memoryPerSparseCache * caches.Count, controller.ActiveCacheMemory);
        }
        finally
        {
            foreach (var cache in caches)
            {
                cache.Dispose();
            }
        }

        Assert.Equal(0, controller.ActiveCacheMemory);
        Assert.Equal(options.MaxBufferPoolMemory, pool.PooledMemory);
    }

    [Fact, TestCategory("BVT")]
    public void CustomBufferPoolPreservesLegacyMemoryBehavior()
    {
        var adapter = new TestEventHubDataAdapter(serializer);
        var factory = new CustomBufferPoolFactory(
            adapter,
            serviceProvider.GetRequiredService<OrleansInstruments>());
        using var cache = factory.CreateCache("0", NoOpCheckpointer.Instance, NullLoggerFactory.Instance);

        cache.Add([MakeEventData(0)], DateTime.UtcNow);

        Assert.True(cache.GetMaxAddCount() > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [TestCategory("BVT")]
    public void CustomBufferPoolHonorsCachePressureMonitor(bool hasActiveSubscriptions)
    {
        var factory = new CustomBufferPoolFactory(
            new TestEventHubDataAdapter(serializer),
            serviceProvider.GetRequiredService<OrleansInstruments>());
        using var cache = factory.CreateCache("0", NoOpCheckpointer.Instance, NullLoggerFactory.Instance);
        Assert.IsType<EventHubQueueCache>(cache).UpdatePurgeProtection(hasActiveSubscriptions);
        var monitor = Substitute.For<ICachePressureMonitor>();
        var underPressure = false;
        monitor.IsUnderPressure(Arg.Any<DateTime>()).Returns(_ => underPressure);
        cache.AddCachePressureMonitor(monitor);

        Assert.Equal(EventHubAdapterReceiver.MaxMessagesPerRead, cache.GetMaxAddCount());
        underPressure = true;
        Assert.Equal(0, cache.GetMaxAddCount());
        underPressure = false;
        Assert.Equal(EventHubAdapterReceiver.MaxMessagesPerRead, cache.GetMaxAddCount());
    }

    [Fact, TestCategory("BVT")]
    public void AdaptiveIdleCacheReclaimsMemoryDespiteStalePressureMonitor()
    {
        var controller = new EventHubCacheMemoryController(100 * 1024);
        var pool = new EventHubCacheBufferPool(controller, 0, null, TimeSpan.FromMinutes(1));
        var checkpointer = new TestCheckpointer();
        using var cache = CreateCache("0", pool, controller, checkpointer: checkpointer);
        var position = Assert.Single(cache.Add([MakeSerializedEventData(0, 80 * 1024)], DateTime.UtcNow));
        cache.AddCachePressureMonitor(new AlwaysPressureMonitor());
        cache.UpdatePurgeProtection(hasActiveSubscriptions: false);
        Assert.True(controller.IsUnderPressure);

        Assert.Equal(EventHubAdapterReceiver.MaxMessagesPerRead, cache.GetMaxAddCount());
        Assert.Equal(((IEventHubPartitionLocation)position.SequenceToken).EventHubOffset, checkpointer.LastOffset);
        Assert.Equal(0, controller.ActiveCacheMemory);
    }

    [Fact, TestCategory("BVT")]
    public void CustomBufferPoolReleasesFinalBufferAndAllocatesFreshAfterPurge()
    {
        var pool = new TrackingBufferPool();
        var adapter = new TestEventHubDataAdapter(serializer);
        var evictionStrategy = new EventHubQueueCacheFactory.EventHubCacheEvictionStrategy(
            NullLogger.Instance,
            new AlwaysPurgePredicate(),
            null,
            null);
        var cache = new EventHubQueueCache(
            "0",
            EventHubAdapterReceiver.MaxMessagesPerRead,
            pool,
            adapter,
            evictionStrategy,
            NoOpCheckpointer.Instance,
            NullLogger.Instance,
            null,
            null,
            null);

        cache.Add([MakeEventData(1)], DateTime.UtcNow);
        cache.SignalPurge();

        Assert.Equal(1, pool.FreeCount);
        var positions = cache.Add([MakeEventData(2)], DateTime.UtcNow);
        Assert.Equal(2, pool.AllocateCount);
        var cursor = GetCursor(cache, positions[0].StreamId, positions[0].SequenceToken);
        var message = GetNextMessage(cache, cursor);
        Assert.Equal(positions[0].SequenceToken, message.SequenceToken);

        cache.Dispose();
        cache.Dispose();
        Assert.Equal(2, pool.FreeCount);
    }

    [Fact, TestCategory("BVT")]
    public void CustomEvictionStrategyTracksFreshBufferAfterEmptyPurge()
    {
        var pool = new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(64 * 1024));
        var adapter = new TestEventHubDataAdapter(serializer);
        var evictionStrategy = new CustomEvictionStrategy();
        using var cache = new EventHubQueueCache(
            "0",
            EventHubAdapterReceiver.MaxMessagesPerRead,
            pool,
            adapter,
            evictionStrategy,
            NoOpCheckpointer.Instance,
            NullLogger.Instance,
            null,
            null,
            null);

        cache.Add([MakeEventData(1)], DateTime.UtcNow);
        cache.SignalPurge();
        Assert.Equal(1, evictionStrategy.AllocatedBufferCount);

        using var leasedBuffer = pool.Allocate();
        var positions = cache.Add([MakeEventData(2)], DateTime.UtcNow);

        Assert.Equal(2, evictionStrategy.AllocatedBufferCount);
        Assert.NotSame(leasedBuffer.Id, evictionStrategy.LastAllocatedBuffer.Id);
        var cursor = GetCursor(cache, positions[0].StreamId, positions[0].SequenceToken);
        Assert.Equal(positions[0].SequenceToken, GetNextMessage(cache, cursor).SequenceToken);

        cache.SignalPurge();
        using var returnedBuffer = pool.Allocate();
        Assert.Same(evictionStrategy.LastAllocatedBuffer, returnedBuffer);
    }

    [Fact, TestCategory("BVT")]
    public void FailedPackingRestoresActiveBufferPositionAndPreservesMessageOrder()
    {
        var controller = new EventHubCacheMemoryController(1024 * 1024);
        var pool = new EventHubCacheBufferPool(controller, 0, null, TimeSpan.FromMinutes(1));
        var adapter = new ThrowingEventHubDataAdapter(serializer) { ThrowSequenceNumber = 1 };
        using var cache = CreateCache("0", pool, controller, adapter: adapter);
        var firstPosition = Assert.Single(cache.Add([MakeEventData(0, 1024)], DateTime.UtcNow));
        var activeMemory = controller.ActiveCacheMemory;

        for (var i = 0; i < 16; i++)
        {
            var exception = Assert.Throws<InvalidOperationException>(
                () => cache.Add([MakeEventData(1)], DateTime.UtcNow));
            Assert.Equal("packing failed", exception.Message);
            Assert.Equal(activeMemory, controller.ActiveCacheMemory);
            Assert.Equal(0, pool.PooledMemory);
        }

        var lastPosition = Assert.Single(cache.Add([MakeEventData(2, 8 * 1024)], DateTime.UtcNow));
        Assert.Equal(activeMemory, controller.ActiveCacheMemory);
        var cursor = GetCursor(cache, firstPosition.StreamId, firstPosition.SequenceToken);
        var first = GetNextMessage(cache, cursor);
        Assert.Equal(firstPosition.SequenceToken, first.SequenceToken);
        var last = GetNextMessage(cache, cursor);
        Assert.Equal(lastPosition.SequenceToken, last.SequenceToken);
        AssertNoNextMessage(cache, cursor);
    }

    [Fact, TestCategory("BVT")]
    public void FailedPackingDisposesOnlyNewlyAllocatedBuffer()
    {
        var pool = new TrackingBufferPool(16 * 1024);
        var adapter = new ThrowingEventHubDataAdapter(serializer) { ThrowSequenceNumber = 1 };
        var evictionStrategy = new ChronologicalEvictionStrategy(
            NullLogger.Instance,
            new TimePurgePredicate(TimeSpan.FromDays(1), TimeSpan.FromDays(1)),
            null,
            null);
        var cache = new EventHubQueueCache(
            "0",
            EventHubAdapterReceiver.MaxMessagesPerRead,
            pool,
            adapter,
            evictionStrategy,
            NoOpCheckpointer.Instance,
            NullLogger.Instance,
            null,
            null,
            null);
        var firstPosition = Assert.Single(cache.Add([MakeEventData(0, 12 * 1024)], DateTime.UtcNow));
        Assert.Equal(1, pool.AllocateCount);
        Assert.Equal(0, pool.FreeCount);

        Assert.Throws<InvalidOperationException>(() => cache.Add([MakeEventData(1)], DateTime.UtcNow));

        Assert.Equal(2, pool.AllocateCount);
        Assert.Equal(1, pool.FreeCount);
        var lastPosition = Assert.Single(cache.Add([MakeEventData(2, 512)], DateTime.UtcNow));
        Assert.Equal(2, pool.AllocateCount);
        Assert.Equal(1, pool.FreeCount);

        var cursor = GetCursor(cache, firstPosition.StreamId, firstPosition.SequenceToken);
        var first = GetNextMessage(cache, cursor);
        Assert.Equal(firstPosition.SequenceToken, first.SequenceToken);
        var last = GetNextMessage(cache, cursor);
        Assert.Equal(lastPosition.SequenceToken, last.SequenceToken);
        AssertNoNextMessage(cache, cursor);

        cache.Dispose();
        Assert.Equal(2, pool.FreeCount);
    }

    [Fact, TestCategory("BVT")]
    public void CacheFactoryPublishesOneBufferPoolConcurrently()
    {
        var factory = new ExposedBufferPoolFactory(
            new TestEventHubDataAdapter(serializer),
            serviceProvider.GetRequiredService<OrleansInstruments>());
        var pools = new IObjectPool<FixedSizeBuffer>[64];

        Parallel.For(0, pools.Length, i => pools[i] = factory.GetBufferPool());

        Assert.All(pools, pool => Assert.Same(pools[0], pool));
    }

    [Fact, TestCategory("BVT")]
    public void DisposeClearsCacheBeforeDisposingEvictionStrategy()
    {
        var controller = new EventHubCacheMemoryController(1024 * 1024);
        var pool = new EventHubCacheBufferPool(controller, 64 * 1024, null, TimeSpan.FromMinutes(1));
        var evictionStrategy = new TrackingEvictionStrategy();
        var cache = CreateCache("0", pool, controller, evictionStrategy);
        cache.Add([MakeEventData(0)], DateTime.UtcNow);

        cache.Dispose();

        Assert.True(evictionStrategy.WasCacheEmptyOnDispose);
    }

    public void Dispose() => serviceProvider.Dispose();

    private static EventHubAdapterReceiver CreatePressureReceiver(EventHubQueueCache cache, IEventHubReceiver broker)
        => new(
            new EventHubPartitionSettings
            {
                Hub = new EventHubOptions(),
                ReceiverOptions = new EventHubReceiverOptions(),
                Partition = "0",
            },
            (_, _, _) => cache,
            (_, _) => Task.FromResult<IStreamQueueCheckpointer<string>>(NoOpCheckpointer.Instance),
            NullLoggerFactory.Instance,
            Substitute.For<IQueueAdapterReceiverMonitor>(),
            new LoadSheddingOptions(),
            Substitute.For<IEnvironmentStatisticsProvider>(),
            (_, _, _) => broker);

    private PersistentStreamPullingAgent CreatePressureAgent(EventHubAdapterReceiver receiver, QueueId queueId)
    {
        var siloAddress = SiloAddress.New(IPAddress.Loopback, 11111, 1);
        var localSilo = Substitute.For<ILocalSiloDetails>();
        localSilo.SiloAddress.Returns(siloAddress);
        var timers = Substitute.For<ITimerRegistry>();
        var shared = new SystemTargetShared(
            runtimeClient: null!,
            localSilo,
            NullLoggerFactory.Instance,
            Options.Create(new SchedulingOptions()),
            grainReferenceActivator: null!,
            timerRegistry: timers,
            activations: new ActivationDirectory(serviceProvider.GetRequiredService<CatalogInstruments>()),
            schedulerInstruments: serviceProvider.GetRequiredService<SchedulerInstruments>(),
            grainInstruments: serviceProvider.GetRequiredService<GrainInstruments>(),
            messagingInstruments: serviceProvider.GetRequiredService<MessagingInstruments>(),
            messagingProcessingInstruments: serviceProvider.GetRequiredService<MessagingProcessingInstruments>());
        var adapter = Substitute.For<IQueueAdapter>();
        adapter.Name.Returns("provider");
        adapter.CreateReceiver(queueId).Returns(receiver);
        var adapterCache = Substitute.For<IQueueAdapterCache>();
        adapterCache.CreateQueueCache(queueId).Returns(receiver);
        var pubSub = Substitute.For<IStreamPubSub>();
        pubSub.RegisterProducer(default, default, Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));
        return new PersistentStreamPullingAgent(
            SystemTargetGrainId.Create(SystemTargetGrainId.CreateGrainType("event-hub-pressure-test"), siloAddress),
            "provider", pubSub, new NoOpStreamFilter(), queueId,
            new StreamPullingAgentOptions(), adapter, adapterCache,
            new NoOpStreamDeliveryFailureHandler(),
            new FixedBackoff(TimeSpan.FromMilliseconds(1)),
            new FixedBackoff(TimeSpan.FromMilliseconds(1)),
            TimeProvider.System, shared);
    }

    private EventData MakeSerializedEventData(int sequenceNumber, int payloadSize, string? streamKey = null)
    {
        var payload = new byte[payloadSize];
        Array.Fill(payload, (byte)sequenceNumber);
        var encoded = new TestEventHubDataAdapter(serializer)
            .ToQueueMessage(StreamId.Create("test", "0"), new[] { payload }, null, null);
        return EventHubsModelFactory.EventData(
            eventBody: encoded.EventBody,
            offsetString: sequenceNumber.ToString(CultureInfo.InvariantCulture),
            sequenceNumber: sequenceNumber,
            partitionKey: streamKey,
            enqueuedTime: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    private static object GetCursor(
        EventHubQueueCache cache,
        StreamId streamId,
        StreamSequenceToken? sequenceToken)
    {
        var result = cache.TryGetCursor(streamId, sequenceToken);
        Assert.Equal(QueueCacheCursorResultKind.Success, result.Kind);
        Assert.NotNull(result.Cursor);
        return result.Cursor;
    }

    private static IBatchContainer GetNextMessage(EventHubQueueCache cache, object cursor)
    {
        var result = cache.TryGetNextMessageWithResult(cursor, out var message);
        Assert.Equal(QueueCacheCursorMoveResultKind.Success, result.Kind);
        Assert.NotNull(message);
        return message;
    }

    private static void AssertNoNextMessage(EventHubQueueCache cache, object cursor)
    {
        var result = cache.TryGetNextMessageWithResult(cursor, out var message);
        Assert.Equal(QueueCacheCursorMoveResultKind.NoData, result.Kind);
        Assert.Null(message);
    }

    private EventHubQueueCache CreateCache(
        string partition,
        IObjectPool<FixedSizeBuffer> pool,
        EventHubCacheMemoryController controller,
        IEvictionStrategy? evictionStrategy = null,
        IStreamQueueCheckpointer<string>? checkpointer = null,
        IEventHubDataAdapter? adapter = null)
    {
        adapter ??= new TestEventHubDataAdapter(serializer);
        evictionStrategy ??= new EventHubQueueCacheFactory.EventHubCacheEvictionStrategy(
            NullLogger.Instance,
            new TimePurgePredicate(TimeSpan.FromDays(1), TimeSpan.FromDays(1)),
            null,
            null);
        return new EventHubQueueCache(
            partition,
            EventHubAdapterReceiver.MaxMessagesPerRead,
            pool,
            adapter,
            evictionStrategy,
            checkpointer ?? NoOpCheckpointer.Instance,
            NullLogger.Instance,
            null,
            null,
            null,
            controller);
    }

    private static EventData MakeEventData(long sequenceNumber, int payloadSize = 2)
    {
        var now = DateTime.UtcNow;
        return EventHubsModelFactory.EventData(
            eventBody: new BinaryData(new byte[payloadSize]),
            offsetString: now.Ticks.ToString(CultureInfo.InvariantCulture),
            sequenceNumber: sequenceNumber,
            enqueuedTime: now);
    }

    private class TestEventHubDataAdapter(Serializer serializer) : EventHubDataAdapter(serializer)
    {
        public override StreamPosition GetStreamPosition(string partition, EventData queueMessage)
        {
            var streamId = StreamId.Create("test", queueMessage.PartitionKey ?? partition);
            var token = new EventHubSequenceTokenV2(queueMessage.OffsetString, queueMessage.SequenceNumber, 0);
            return new StreamPosition(streamId, token);
        }
    }

    private sealed class ThrowingEventHubDataAdapter(Serializer serializer) : TestEventHubDataAdapter(serializer)
    {
        public long ThrowSequenceNumber { get; init; }

        public override CachedMessage FromQueueMessage(
            StreamPosition streamPosition,
            EventData queueMessage,
            DateTime dequeueTime,
            Func<int, ArraySegment<byte>> getSegment)
        {
            if (queueMessage.SequenceNumber == ThrowSequenceNumber)
            {
                _ = getSegment(8 * 1024);
                throw new InvalidOperationException("packing failed");
            }

            return base.FromQueueMessage(streamPosition, queueMessage, dequeueTime, getSegment);
        }
    }

    private sealed class UntrackedStorageEventHubDataAdapter(
        Serializer serializer, bool nullArray, long untrackedSequence = 0)
        : TestEventHubDataAdapter(serializer)
    {
        private CachedMessage untrackedMessage;

        public override CachedMessage FromQueueMessage(
            StreamPosition streamPosition,
            EventData queueMessage,
            DateTime dequeueTime,
            Func<int, ArraySegment<byte>> getSegment)
        {
            if (queueMessage.SequenceNumber != untrackedSequence)
            {
                return base.FromQueueMessage(streamPosition, queueMessage, dequeueTime, getSegment);
            }

            untrackedMessage = base.FromQueueMessage(
                streamPosition, queueMessage, dequeueTime, size => new byte[size]);
            var result = untrackedMessage;
            if (nullArray)
            {
                result.Segment = default;
            }

            return result;
        }

        public override IBatchContainer GetBatchContainer(ref CachedMessage cachedMessage)
        {
            var message = cachedMessage.SequenceNumber == untrackedSequence ? untrackedMessage : cachedMessage;
            return base.GetBatchContainer(ref message);
        }

        public override string GetOffset(CachedMessage cachedMessage)
            => cachedMessage.SequenceNumber.ToString(CultureInfo.InvariantCulture);
    }

    private sealed class AlwaysPurgePredicate : TimePurgePredicate
    {
        public AlwaysPurgePredicate()
            : base(TimeSpan.Zero, TimeSpan.Zero)
        {
        }

        public override bool ShouldPurgeFromTime(TimeSpan timeInCache, TimeSpan relativeAge) => true;
    }

    private sealed class TrackingBufferPool(int bufferSize = EventHubCacheBufferPool.MinBufferSize) : IObjectPool<FixedSizeBuffer>
    {
        public int AllocateCount { get; private set; }

        public int FreeCount { get; private set; }

        public FixedSizeBuffer Allocate()
        {
            AllocateCount++;
            return new FixedSizeBuffer(bufferSize) { Pool = this };
        }

        public void Free(FixedSizeBuffer resource) => FreeCount++;
    }

    private sealed class TrackingEvictionStrategy : IEvictionStrategy, IDisposable
    {
        private IPurgeObservable? purgeObservable;

        public IPurgeObservable PurgeObservable
        {
            set => purgeObservable = value;
        }

        public Action<CachedMessage?, CachedMessage?>? OnPurged { get; set; }

        public bool WasCacheEmptyOnDispose { get; private set; }

        public void Dispose() => WasCacheEmptyOnDispose = purgeObservable?.IsEmpty == true;

        public void OnBlockAllocated(FixedSizeBuffer newBlock)
        {
        }

        public void PerformPurge(DateTime utcNow)
        {
        }
    }

    private sealed class CustomEvictionStrategy : IEvictionStrategy, IDisposable
    {
        private readonly ChronologicalEvictionStrategy inner =
            new(NullLogger.Instance, new AlwaysPurgePredicate(), null, null);

        public IPurgeObservable PurgeObservable
        {
            set => inner.PurgeObservable = value;
        }

        public Action<CachedMessage?, CachedMessage?>? OnPurged
        {
            get => inner.OnPurged;
            set => inner.OnPurged = value;
        }

        public int AllocatedBufferCount { get; private set; }

        public FixedSizeBuffer LastAllocatedBuffer { get; private set; } = null!;

        public void OnBlockAllocated(FixedSizeBuffer newBlock)
        {
            AllocatedBufferCount++;
            LastAllocatedBuffer = newBlock;
            inner.OnBlockAllocated(newBlock);
        }

        public void PerformPurge(DateTime utcNow) => inner.PerformPurge(utcNow);

        public void Dispose() => inner.Dispose();
    }

    private sealed class TestCheckpointer : IStreamQueueCheckpointer<string>
    {
        public bool CheckpointExists => LastOffset is not null;
        public string? LastOffset { get; private set; }

        public Task<string> Load() => Task.FromResult(LastOffset ?? EventHubConstants.StartOfStream);

        public void Update(string offset, DateTime utcNow)
        {
            LastOffset = offset;
        }
    }

    private sealed class AlwaysPressureMonitor : ICachePressureMonitor
    {
        public ICacheMonitor? CacheMonitor { private get; set; }

        public bool IsUnderPressure(DateTime utcNow) => true;

        public void RecordCachePressureContribution(double cachePressureContribution)
        {
        }
    }

    private sealed class PressureConsumer(
        Func<IBatchContainer, Task<StreamHandshakeToken?>> onDelivery,
        List<Exception> errors,
        StreamHandshakeToken? initialToken = null) : IStreamConsumerExtension
    {
        public Task<StreamHandshakeToken?> DeliverImmutable(GuidId subscriptionId, QualifiedStreamId streamId, object item, StreamSequenceToken currentToken, StreamHandshakeToken? handshakeToken, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<StreamHandshakeToken?> DeliverMutable(GuidId subscriptionId, QualifiedStreamId streamId, object item, StreamSequenceToken currentToken, StreamHandshakeToken? handshakeToken, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<StreamHandshakeToken?> DeliverBatch(GuidId subscriptionId, QualifiedStreamId streamId, IBatchContainer item, StreamHandshakeToken? handshakeToken, CancellationToken cancellationToken)
            => onDelivery(item);

        public Task CompleteStream(GuidId subscriptionId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ErrorInStream(GuidId subscriptionId, Exception exc, CancellationToken cancellationToken)
        {
            errors.Add(exc);
            return Task.CompletedTask;
        }

        public Task<StreamHandshakeToken?> GetSequenceToken(GuidId subscriptionId, CancellationToken cancellationToken)
            => Task.FromResult(initialToken);
    }

    private sealed class CustomBufferPoolFactory : EventHubQueueCacheFactory
    {
        private readonly IObjectPool<FixedSizeBuffer> bufferPool =
            new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(1024 * 1024));

        public CustomBufferPoolFactory(IEventHubDataAdapter adapter, OrleansInstruments instruments)
            : base(
                new EventHubStreamCachePressureOptions { AveragingCachePressureMonitorFlowControlThreshold = null },
                new EventHubStreamCacheMemoryOptions { MaxActiveCacheMemory = 1, MaxBufferPoolMemory = 0 },
                new StreamCacheEvictionOptions(),
                new StreamStatisticOptions(),
                adapter,
                new EventHubMonitorAggregationDimensions("test"),
                instruments)
        {
        }

        protected override IObjectPool<FixedSizeBuffer> CreateBufferPool(
            StreamStatisticOptions statisticOptions,
            Microsoft.Extensions.Logging.ILoggerFactory loggerFactory,
            EventHubMonitorAggregationDimensions sharedDimensions,
            out string blockPoolId)
        {
            blockPoolId = "custom";
            return bufferPool;
        }
    }

    private sealed class ExposedBufferPoolFactory : EventHubQueueCacheFactory
    {
        private readonly StreamStatisticOptions statisticOptions = new();
        private readonly EventHubMonitorAggregationDimensions dimensions = new("test");

        public ExposedBufferPoolFactory(IEventHubDataAdapter adapter, OrleansInstruments instruments)
            : base(
                new EventHubStreamCachePressureOptions(),
                new EventHubStreamCacheMemoryOptions(),
                new StreamCacheEvictionOptions(),
                new StreamStatisticOptions(),
                adapter,
                new EventHubMonitorAggregationDimensions("test"),
                instruments)
        {
        }

        public IObjectPool<FixedSizeBuffer> GetBufferPool()
            => base.CreateBufferPool(statisticOptions, NullLoggerFactory.Instance, dimensions, out _);
    }
}
