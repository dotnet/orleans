using System.Globalization;
using System.Net;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Consumer;
using Azure.Messaging.EventHubs.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Internal;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Runtime.Scheduler;
using Orleans.Serialization;
using Orleans.Statistics;
using Orleans.Streaming.EventHubs;
using Orleans.Streams;
using Orleans.Streams.Filtering;
using Orleans.Timers;
using Xunit;

namespace ServiceBus.Tests.CheckpointerTests;

[TestSuite("BVT")]
[TestProvider("EventHub")]
[TestArea("Streaming")]
[TestCategory("BVT"), TestCategory("EventHub"), TestCategory("Streaming")]
public class EventHubCheckpointRecoveryTests
{
    private const string ProviderName = "checkpoint-recovery";
    private static readonly QueueId Queue = QueueId.GetQueueId("queue", 0, 0);
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shutdown_IdleStreamDoesNotPinCheckpoint_ResumesWithoutOldBacklog(bool recoverCursor)
    {
        using var services = CreateServices();
        var adapter = new EventHubDataAdapter(services.GetRequiredService<Serializer>());
        var idleId = StreamId.Create("idle", "idle");
        var busyId = StreamId.Create("busy", "busy");
        var events = Enumerable.Range(1, 202)
            .Select(sequence => CreateEvent(adapter, sequence == 1 ? idleId : busyId, sequence))
            .ToArray();
        var store = new CheckpointStore();
        var idle = new RecordingConsumer();
        var busy = new RecordingConsumer();

        await using (var first = await CreateAgent(services, adapter, events, store))
        {
            Assert.Equal(EventHubConstants.StartOfStream, first.Transport.RequestedOffset);
            var idleSubscription = await first.AddConsumer(idleId, idle);
            var busySubscription = await first.AddConsumer(busyId, busy);
            Assert.True(await first.Read(2));
            await first.Accessor.AddSubscriber(idleSubscription);
            await first.Accessor.AddSubscriber(busySubscription);

            if (recoverCursor)
            {
                busySubscription.Cursor = new ThrowingQueueCursor(busySubscription.Cursor!);
            }

            Assert.True(await first.Read(198));
            if (recoverCursor)
            {
                Assert.Equal([2], busy.Events);
                await first.Accessor.RunConsumerCursor(busySubscription);
            }

            Assert.Equal([1], idle.Events);
            Assert.Equal(Enumerable.Range(2, 199), busy.Events);
            Assert.Equal(1, idleSubscription.LastProcessedToken!.SequenceNumber);
            Assert.Equal(200, busySubscription.LastProcessedToken!.SequenceNumber);
            Assert.Equal(200, idleSubscription.LastSafePartitionToken!.SequenceNumber);
            Assert.Equal(200, busySubscription.LastSafePartitionToken!.SequenceNumber);
            Assert.Empty(store.State.Checkpoint);

            await first.Accessor.Shutdown();

            Assert.Equal("200", store.State.Checkpoint);
            Assert.Equal(["200"], store.Writes);
            Assert.True(first.Transport.Closed);
        }

        var resumedConsumer = new RecordingConsumer(StreamHandshakeToken.CreateDeliveyToken(Token(200)));
        await using var restarted = await CreateAgent(services, adapter, events, store);
        Assert.Equal("200", restarted.Transport.RequestedOffset);
        var resumedSubscription = await restarted.AddConsumer(busyId, resumedConsumer);
        Assert.True(await restarted.Read(1000));
        await restarted.Accessor.AddSubscriber(resumedSubscription);

        // Event Hubs resumes inclusively. The real agent handshake skips the acknowledged boundary event.
        Assert.Equal([200L, 201L, 202L], restarted.Transport.ReceivedSequences);
        Assert.Equal([201, 202], resumedConsumer.Events);
        Assert.Equal(202, resumedSubscription.LastProcessedToken!.SequenceNumber);
        Assert.False(await restarted.Read(1000));
        Assert.Empty(idle.Errors);
        if (recoverCursor)
        {
            Assert.Equal("Injected cursor read failure", Assert.Single(busy.Errors).Message);
        }
        else
        {
            Assert.Empty(busy.Errors);
        }
        Assert.Empty(resumedConsumer.Errors);
    }

    private static ServiceProvider CreateServices()
        => new ServiceCollection()
            .AddMetrics()
            .AddSerializer()
            .AddSingleton<OrleansInstruments>()
            .AddSingleton<SchedulerInstruments>()
            .AddSingleton<CatalogInstruments>()
            .AddSingleton<GrainInstruments>()
            .AddSingleton<MessagingInstruments>()
            .AddSingleton<MessagingProcessingInstruments>()
            .BuildServiceProvider();

    [Fact]
    public async Task NativeCacheWithReleasedTransportKeepsLegacyPurgeCheckpointing()
    {
        using var services = CreateServices();
        var adapter = new EventHubDataAdapter(services.GetRequiredService<Serializer>());
        var stream = StreamId.Create("legacy", "transport");
        var store = new CheckpointStore();
        await using var lifetime = await CreateAgent(
            services, adapter, [CreateEvent(adapter, stream, 1), CreateEvent(adapter, stream, 2)],
            store, purgeImmediately: true, legacyTransport: true);
        Assert.False(lifetime.UsesCertifiedDeliveryProgress);
        Assert.True(await lifetime.Read(2));
        var streams = await lifetime.Accessor.GetPubSubCache();
        await Task.WhenAll(streams.Values.Select(value => value.RegistrationTask ?? Task.CompletedTask));
        Assert.False(await lifetime.Read(0));
        await lifetime.Accessor.Shutdown();
        Assert.Equal("2", store.State.Checkpoint);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ExistingDerivedCachesAndEvictionPoliciesKeepTheirReleasedDefaults(bool derivedCache, bool customEviction)
    {
        using var services = CreateServices();
        var adapter = new EventHubDataAdapter(services.GetRequiredService<Serializer>());
        var stream = StreamId.Create("legacy", "cache");
        var pool = new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(1024 * 1024));
        var checkpointer = Substitute.For<IStreamQueueCheckpointer<string>>();
        IEvictionStrategy eviction = customEviction
            ? new LegacyEvictionStrategy()
            : new ChronologicalEvictionStrategy(NullLogger.Instance, new ImmediatePurgePredicate(), null, null);
        using EventHubQueueCache cache = derivedCache
            ? new LegacyDerivedCache(pool, adapter, eviction, checkpointer)
            : new EventHubQueueCache("0", 1000, pool, adapter, eviction, checkpointer, NullLogger.Instance, null!, null, null);
        var certified = cache.TryEnableCertifiedDeliveryProgress();
        Assert.Equal(!derivedCache && !customEviction, certified);
        cache.Add([CreateEvent(adapter, stream, 1), CreateEvent(adapter, stream, 2)], Now.UtcDateTime);
        cache.SignalPurge();
        var updates = checkpointer.ReceivedCalls().Where(call => call.GetMethodInfo().Name == nameof(IStreamQueueCheckpointer<string>.Update)).ToArray();
        if (certified)
        {
            Assert.Empty(updates);
        }
        else
        {
            Assert.Equal("2", Assert.Single(updates).GetArguments()[0]);
        }
    }

    private sealed class LegacyEvictionStrategy()
        : ChronologicalEvictionStrategy(NullLogger.Instance, new ImmediatePurgePredicate(), null, null);

    [Fact]
    public void ExistingCustomDataAdaptersKeepReleasedDefaults()
    {
        using var services = CreateServices();
        var adapter = new ThrowingDataAdapter(services.GetRequiredService<Serializer>());
        using var cache = new EventHubQueueCache("0", 1000,
            new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(1024 * 1024)), adapter,
            new ChronologicalEvictionStrategy(NullLogger.Instance, new ImmediatePurgePredicate(), null, null),
            Substitute.For<IStreamQueueCheckpointer<string>>(), NullLogger.Instance, null!, null, null);
        Assert.False(cache.TryEnableCertifiedDeliveryProgress());
    }

    private sealed class LegacyDerivedCache(
        IObjectPool<FixedSizeBuffer> pool, IEventHubDataAdapter adapter,
        IEvictionStrategy eviction, IStreamQueueCheckpointer<string> checkpointer)
        : EventHubQueueCache("0", 1000, pool, adapter, eviction, checkpointer, NullLogger.Instance, null!, null, null);

    [Fact]
    public async Task ReadRecovery_RetriesPackingWithoutFetchingOrAdmittingTwice()
    {
        using var services = CreateServices();
        var adapter = new ThrowingDataAdapter(services.GetRequiredService<Serializer>());
        var idleId = StreamId.Create("idle", "idle");
        var busyId = StreamId.Create("busy", "busy");
        var events = Enumerable.Range(1, 5)
            .Select(sequence => CreateEvent(adapter, sequence == 1 ? idleId : busyId, sequence))
            .ToArray();
        var store = new CheckpointStore();
        var idle = new RecordingConsumer();
        var busy = new RecordingConsumer();

        await using var lifetime = await CreateAgent(services, adapter, events, store);
        Assert.True(lifetime.UsesCertifiedDeliveryProgress);
        var idleSubscription = await lifetime.AddConsumer(idleId, idle);
        var busySubscription = await lifetime.AddConsumer(busyId, busy);
        Assert.True(await lifetime.Read(2));
        await lifetime.Accessor.AddSubscriber(idleSubscription);
        await lifetime.Accessor.AddSubscriber(busySubscription);

        adapter.FailAtSequence = 4;
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifetime.Read(3));
        Assert.True(lifetime.UsesCertifiedDeliveryProgress);
        await lifetime.Accessor.ReportDeliveryProgress();
        Assert.Empty(store.Writes);
        Assert.Equal([2], busy.Events);
        Assert.Equal([1L, 2L, 3L, 4L, 5L], lifetime.Transport.ReceivedSequences);

        Assert.True(await lifetime.Read(3));
        Assert.True(lifetime.UsesCertifiedDeliveryProgress);
        Assert.Equal([1L, 2L, 3L, 4L, 5L], lifetime.Transport.ReceivedSequences);
        Assert.Equal([2, 3, 4, 5], busy.Events);
        Assert.Equal([1], idle.Events);
        await lifetime.Accessor.Shutdown();
        Assert.Equal(["5"], store.Writes);
    }

    [Fact]
    public async Task Checkpoint_PartialEventResumeWaitsForTheRemainingRecord()
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer>();
        var adapter = new EventHubDataAdapter(serializer);
        var streamId = StreamId.Create("partial", "record");
        var payload = adapter.ToQueueMessage<int>(streamId, [10, 11, 12], null, null);
        var first = EventHubsModelFactory.EventData(
            eventBody: payload.EventBody, properties: payload.Properties, partitionKey: adapter.GetPartitionKey(streamId),
            sequenceNumber: 1, offsetString: "1", enqueuedTime: Now);
        var store = new CheckpointStore();
        var selected = new TaskCompletionSource<EventHubBatchContainer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledged = new TaskCompletionSource<StreamHandshakeToken?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deliveredSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumer = new RecordingConsumer(StreamHandshakeToken.CreateDeliveyToken(new EventHubSequenceTokenV2("1", 1, 0)))
        {
            OnDelivery = batch =>
            {
                if (batch.SequenceToken.SequenceNumber == 1)
                {
                    selected.TrySetResult(Assert.IsType<EventHubBatchContainer>(batch));
                    return acknowledged.Task;
                }

                deliveredSecond.TrySetResult();
                return Task.FromResult<StreamHandshakeToken?>(null);
            },
        };
        await using var lifetime = await CreateAgent(
            services, adapter, [first, CreateEvent(adapter, streamId, 2)], store, purgeImmediately: true);
        var subscription = await lifetime.AddConsumer(streamId, consumer);
        Assert.True(await lifetime.Read(2));
        await lifetime.Accessor.AddSubscriber(subscription);
        try
        {
            var pending = await selected.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal([11, 12], consumer.Events);
            Assert.Equal([1, 2], pending.GetEvents<int>().Select(item => item.Item2.EventIndex));
            var copy = serializer.Deserialize<EventHubBatchContainer>(serializer.SerializeToArray(pending));
            Assert.NotNull(copy);
            Assert.Equal([11, 12], copy.GetEvents<int>().Select(item => item.Item1));
            Assert.Equal([1, 2], copy.GetEvents<int>().Select(item => item.Item2.EventIndex));

            Assert.False(await lifetime.Read(0));
            await lifetime.Accessor.ReportDeliveryProgress();
            Assert.Empty(store.Writes);
            Assert.Equal([11, 12], pending.GetEvents<int>().Select(item => item.Item1));

            acknowledged.SetResult(null);
            await deliveredSecond.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await lifetime.Accessor.Shutdown();
            Assert.Equal([11, 12, 2], consumer.Events);
            Assert.Equal(["2"], store.Writes);
        }
        finally
        {
            acknowledged.TrySetResult(null);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadRecovery_PreservesCapturedLatestPosition(bool emptyPartition)
    {
        var positions = new List<EventPosition>();
        var readers = new List<PartitionReceiver>();
        var failure = new InvalidOperationException("Transient source read failure");
        var proxy = new EventHubReceiverProxy(position =>
        {
            positions.Add(position);
            var reader = Substitute.For<PartitionReceiver>();
            reader.GetPartitionPropertiesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(
                EventHubsModelFactory.PartitionProperties("hub", "0", emptyPartition, 0, emptyPartition ? -1 : 50, "50", Now)));
            reader.ReceiveBatchAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<IEnumerable<EventData>>(failure));
            readers.Add(reader);
            return reader;
        }, EventPosition.Latest, captureLatestPosition: true);
        await proxy.InitializeAsync(TestContext.Current.CancellationToken);
        var captured = emptyPartition ? EventPosition.Earliest : EventPosition.FromOffset("50", false);
        Assert.Equal(new[] { EventPosition.Latest, captured }, positions);

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(
            () => proxy.ReceiveAsync(10, TimeSpan.Zero, TestContext.Current.CancellationToken)));
        await proxy.RecoverReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new[] { EventPosition.Latest, captured, captured }, positions);
        await readers[0].Received(1).GetPartitionPropertiesAsync(Arg.Any<CancellationToken>());
        await readers[1].DidNotReceive().GetPartitionPropertiesAsync(Arg.Any<CancellationToken>());
        await proxy.CloseAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ReadRecovery_ResumesAfterLastSuccessfulRawBatch()
    {
        var positions = new List<EventPosition>();
        var reader = Substitute.For<PartitionReceiver>();
        var body = new BinaryData(Array.Empty<byte>());
        var batch = new[] { EventHubsModelFactory.EventData(body, sequenceNumber: 51, offsetString: "51"), EventHubsModelFactory.EventData(body, sequenceNumber: 52, offsetString: "52") };
        reader.ReceiveBatchAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IEnumerable<EventData>>(batch), Task.FromException<IEnumerable<EventData>>(new InvalidOperationException("Read failed")));
        var proxy = new EventHubReceiverProxy(position =>
        {
            positions.Add(position);
            return reader;
        }, EventPosition.FromOffset("50", true), captureLatestPosition: false);

        Assert.Equal(batch, await proxy.ReceiveAsync(10, TimeSpan.Zero, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => proxy.ReceiveAsync(10, TimeSpan.Zero, TestContext.Current.CancellationToken));
        await proxy.RecoverReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new[] { EventPosition.FromOffset("50", true), EventPosition.FromOffset("52", false) }, positions);
        await proxy.CloseAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Eviction_UsesOnlyTheCurrentCertificateAndPreservesSharedBuffers()
    {
        using var services = CreateServices();
        var adapter = new EventHubDataAdapter(services.GetRequiredService<Serializer>());
        var streamId = StreamId.Create("eviction", "stream");
        var buffers = new List<FixedSizeBuffer>();
        var pool = new ObjectPool<FixedSizeBuffer>(() =>
        {
            var buffer = new FixedSizeBuffer(1024 * 1024);
            buffers.Add(buffer);
            return buffer;
        });
        var checkpointer = Substitute.For<IStreamQueueCheckpointer<string>>();
        using var cache = new CertifiedTestCache(pool, adapter,
            new ChronologicalEvictionStrategy(NullLogger.Instance, new ImmediatePurgePredicate(), null!, null),
            checkpointer);
        Assert.True(cache.TryEnableCertifiedDeliveryProgress());
        cache.Add([CreateEvent(adapter, streamId, 1), CreateEvent(adapter, streamId, 2)], Now.UtcDateTime);

        cache.SignalPurge();
        var cursor = cache.TryGetCursor(streamId, Token(1)).Cursor!;
        Assert.Equal(QueueCacheCursorMoveResultKind.Success, cache.TryGetNextMessageWithResult(cursor, out var first).Kind);
        Assert.Equal(1, Assert.Single(first!.GetEvents<int>()).Item1);
        ((IQueueCacheCursorProgress)cursor).RecordDeliverySuccess();
        cache.UpdateDeliveryProgress(Token(1), Now.UtcDateTime.AddMinutes(1));

        var probe = pool.Allocate();
        Assert.NotSame(buffers[0], probe);
        probe.Dispose();
        cache.SignalPurge();
        Assert.Equal(QueueCacheCursorMoveResultKind.Success, cache.TryGetNextMessageWithResult(cursor, out var second).Kind);
        Assert.Equal(2, Assert.Single(second!.GetEvents<int>()).Item1);
        Assert.DoesNotContain(checkpointer.ReceivedCalls(), call => call.GetMethodInfo().Name == nameof(IStreamQueueCheckpointer<string>.Update));
    }

    [Fact]
    public void Eviction_FailedCallbackClearsItsBorrowedCertificate()
    {
        using var services = CreateServices();
        var adapter = new EventHubDataAdapter(services.GetRequiredService<Serializer>());
        var streamId = StreamId.Create("eviction", "failure");
        using var cache = new CertifiedTestCache(
            new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(1024 * 1024)), adapter,
            new ThrowingEvictionStrategy(), Substitute.For<IStreamQueueCheckpointer<string>>());
        Assert.True(cache.TryEnableCertifiedDeliveryProgress());
        cache.Add([CreateEvent(adapter, streamId, 1)], Now.UtcDateTime);
        Assert.Throws<InvalidOperationException>(() => cache.UpdateDeliveryProgress(Token(1), Now.UtcDateTime.AddMinutes(1)));

        cache.SignalPurge();
        var cursor = cache.TryGetCursor(streamId, Token(1)).Cursor!;
        Assert.Equal(QueueCacheCursorMoveResultKind.Success, cache.TryGetNextMessageWithResult(cursor, out var batch).Kind);
        Assert.Equal(1, Assert.Single(batch!.GetEvents<int>()).Item1);
    }

    [Fact]
    public void Admission_RetriesBufferNotificationWithoutDuplicatingOwnership()
    {
        using var services = CreateServices();
        var adapter = new EventHubDataAdapter(services.GetRequiredService<Serializer>());
        var streamId = StreamId.Create("admission", "notification");
        var notificationCount = 0;
        var monitor = Substitute.For<ICacheMonitor>();
        monitor.WhenForAnyArgs(value => value.TrackMemoryAllocated(default)).Do(_ =>
        {
            notificationCount++;
            throw new InvalidOperationException("Allocation observer failed after ownership transfer");
        });
        var buffers = new List<FixedSizeBuffer>();
        var pool = new ObjectPool<FixedSizeBuffer>(() =>
        {
            var buffer = new FixedSizeBuffer(1024 * 1024);
            buffers.Add(buffer);
            return buffer;
        });
        using var cache = new CertifiedTestCache(pool, adapter,
            new ChronologicalEvictionStrategy(NullLogger.Instance, new ImmediatePurgePredicate(), monitor, null),
            Substitute.For<IStreamQueueCheckpointer<string>>());
        Assert.True(cache.TryEnableCertifiedDeliveryProgress());
        var messages = new List<EventData> { CreateEvent(adapter, streamId, 1), CreateEvent(adapter, streamId, 2) };
        Assert.Throws<InvalidOperationException>(() => cache.Add(messages, Now.UtcDateTime));

        Assert.Equal(2, cache.Add(messages, Now.UtcDateTime).Count);
        Assert.Equal(1, notificationCount);
        var cursor = cache.TryGetCursor(streamId, Token(1)).Cursor!;
        foreach (var expected in new[] { 1, 2 })
        {
            Assert.Equal(QueueCacheCursorMoveResultKind.Success, cache.TryGetNextMessageWithResult(cursor, out var batch).Kind);
            Assert.Equal(expected, Assert.Single(batch!.GetEvents<int>()).Item1);
            ((IQueueCacheCursorProgress)cursor).RecordDeliverySuccess();
        }
        Assert.Equal(QueueCacheCursorMoveResultKind.NoData, cache.TryGetNextMessageWithResult(cursor, out _).Kind);
        cache.UpdateDeliveryProgress(Token(2), Now.UtcDateTime.AddMinutes(1));

        var firstLease = pool.Allocate();
        var secondLease = pool.Allocate();
        Assert.Same(buffers[0], firstLease);
        Assert.NotSame(firstLease, secondLease);
        firstLease.Dispose();
        secondLease.Dispose();
    }

    private sealed class ThrowingEvictionStrategy()
        : ChronologicalEvictionStrategy(NullLogger.Instance, new ImmediatePurgePredicate(), null!, null)
    {
        private bool throwNext = true;

        protected override bool ShouldPurge(ref CachedMessage cachedMessage, ref CachedMessage newestCachedMessage, DateTime nowUtc)
        {
            if (throwNext)
            {
                throwNext = false;
                throw new InvalidOperationException("Purge policy failed");
            }
            return base.ShouldPurge(ref cachedMessage, ref newestCachedMessage, nowUtc);
        }
    }

    private sealed class ImmediatePurgePredicate() : TimePurgePredicate(TimeSpan.Zero, TimeSpan.Zero)
    {
        public override bool ShouldPurgeFromTime(TimeSpan timeInCache, TimeSpan relativeAge) => true;
    }

    private sealed class CertifiedTestCache(
        IObjectPool<FixedSizeBuffer> pool,
        IEventHubDataAdapter adapter,
        IEvictionStrategy eviction,
        IStreamQueueCheckpointer<string> checkpointer)
        : EventHubQueueCache("0", 1000, pool, adapter, eviction, checkpointer, NullLogger.Instance, null!, null, null)
    {
        public override bool TryEnableCertifiedDeliveryProgress()
        {
            EnableCertifiedDeliveryProgress();
            return true;
        }
    }

    private sealed class ThrowingDataAdapter(Serializer serializer) : EventHubDataAdapter(serializer)
    {
        public long? FailAtSequence { get; set; }

        public override CachedMessage FromQueueMessage(
            StreamPosition streamPosition, EventData queueMessage, DateTime dequeueTime, Func<int, ArraySegment<byte>> getSegment)
        {
            var result = base.FromQueueMessage(streamPosition, queueMessage, dequeueTime, getSegment);
            if (FailAtSequence == queueMessage.SequenceNumber)
            {
                FailAtSequence = null;
                throw new InvalidOperationException("Transient packing failure");
            }
            return result;
        }
    }

    private sealed class ThrowingQueueCursor(IQueueCacheCursor inner) : IQueueCacheCursor, IQueueCacheCursorProgress
    {
        private bool failed;
        public void Dispose() => inner.Dispose();
        public IBatchContainer? GetCurrent(out Exception? exception) => inner.GetCurrent(out exception);
        public bool MoveNext() => MoveNextWithResult().Kind == QueueCacheCursorMoveResultKind.Success;
        public QueueCacheCursorMoveResult MoveNextWithResult()
        {
            if (!failed)
            {
                failed = true;
                throw new InvalidOperationException("Injected cursor read failure");
            }
            return inner.MoveNextWithResult();
        }
        public void Refresh(StreamSequenceToken token) => inner.Refresh(token);
        public void RecordDeliveryFailure() => inner.RecordDeliveryFailure();
        void IQueueCacheCursorProgress.RecordDeliveryFailure() => ((IQueueCacheCursorProgress)inner).RecordDeliveryFailure();
        public StreamSequenceToken? SafeSequenceToken => ((IQueueCacheCursorProgress)inner).SafeSequenceToken;
        public void SetDeliveredThrough(StreamSequenceToken token) => ((IQueueCacheCursorProgress)inner).SetDeliveredThrough(token);
        public void RecordDeliverySuccess() => ((IQueueCacheCursorProgress)inner).RecordDeliverySuccess();
    }

    private static EventData CreateEvent(EventHubDataAdapter adapter, StreamId streamId, int sequence)
    {
        var message = adapter.ToQueueMessage<int>(streamId, [sequence], null, null);
        return EventHubsModelFactory.EventData(
            eventBody: message.EventBody,
            properties: message.Properties,
            partitionKey: adapter.GetPartitionKey(streamId),
            offsetString: sequence.ToString(CultureInfo.InvariantCulture),
            sequenceNumber: sequence,
            enqueuedTime: Now);
    }

    private static EventHubSequenceTokenV2 Token(long sequence)
        => new(sequence.ToString(CultureInfo.InvariantCulture), sequence, 0);

    private static async Task<AgentLifetime> CreateAgent(
        ServiceProvider services,
        EventHubDataAdapter adapter,
        EventData[] events,
        CheckpointStore store,
        bool purgeImmediately = false,
        bool legacyTransport = false)
    {
        PartitionTransport? transport = null;
        var settings = new EventHubPartitionSettings
        {
            Hub = new EventHubOptions(),
            Partition = "0",
            ReceiverOptions = new EventHubReceiverOptions { StartFromNow = false },
        };
        var receiver = new EventHubAdapterReceiver(
            settings,
            cacheFactory: (partition, checkpointer, _) => new CertifiedTestCache(
                new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(1024 * 1024)),
                adapter,
                new ChronologicalEvictionStrategy(
                    NullLogger.Instance,
                    purgeImmediately ? new ImmediatePurgePredicate() : new TimePurgePredicate(TimeSpan.FromHours(1), TimeSpan.FromHours(1)),
                    null!,
                    null),
                checkpointer),
            checkpointerFactory: (_, _) => Task.FromResult<IStreamQueueCheckpointer<string>>(
                new StreamQueueCheckpointer(store, new StreamQueueCheckpointerOptions
                {
                    CheckpointComparer = Comparer<string>.Create((left, right) =>
                        long.Parse(left, CultureInfo.InvariantCulture).CompareTo(long.Parse(right, CultureInfo.InvariantCulture))),
                })),
            loggerFactory: NullLoggerFactory.Instance,
            monitor: new DefaultEventHubReceiverMonitor(
                new EventHubReceiverMonitorDimensions { EventHubPartition = settings.Partition },
                services.GetRequiredService<OrleansInstruments>()),
            loadSheddingOptions: new LoadSheddingOptions(),
            environmentStatisticsProvider: new EnvironmentStatisticsProvider(),
            eventHubReceiverFactory: (_, offset, _) =>
            {
                transport = new PartitionTransport(events, offset);
                return legacyTransport ? new LegacyPartitionTransport(transport) : transport;
            });

        var siloAddress = SiloAddress.New(IPAddress.Loopback, 11111, 1);
        var localSilo = Substitute.For<ILocalSiloDetails>();
        localSilo.SiloAddress.Returns(siloAddress);
        var timers = Substitute.For<ITimerRegistry>();
        timers.RegisterGrainTimer(
                Arg.Any<IGrainContext>(),
                Arg.Any<Func<QueueId, CancellationToken, Task>>(),
                Arg.Any<QueueId>(),
                Arg.Any<GrainTimerCreationOptions>())
            .Returns(Substitute.For<IGrainTimer>());
        var shared = new SystemTargetShared(
            runtimeClient: null!,
            localSilo,
            NullLoggerFactory.Instance,
            Options.Create(new SchedulingOptions()),
            grainReferenceActivator: null!,
            timerRegistry: timers,
            activations: new ActivationDirectory(services.GetRequiredService<CatalogInstruments>()),
            schedulerInstruments: services.GetRequiredService<SchedulerInstruments>(),
            grainInstruments: services.GetRequiredService<GrainInstruments>(),
            messagingInstruments: services.GetRequiredService<MessagingInstruments>(),
            messagingProcessingInstruments: services.GetRequiredService<MessagingProcessingInstruments>());
        var pubSub = Substitute.For<IStreamPubSub>();
        pubSub.RegisterProducer(default, default, Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));
        var queueAdapter = Substitute.For<IQueueAdapter>();
        queueAdapter.Name.Returns(ProviderName);
        queueAdapter.CreateReceiver(Queue).Returns(receiver);
        var adapterCache = Substitute.For<IQueueAdapterCache>();
        adapterCache.CreateQueueCache(Queue).Returns(receiver);
        var agent = new PersistentStreamPullingAgent(
            SystemTargetGrainId.Create(SystemTargetGrainId.CreateGrainType("eventhub-checkpoint-recovery"), siloAddress),
            ProviderName,
            pubSub,
            new NoOpStreamFilter(),
            Queue,
            new StreamPullingAgentOptions(),
            queueAdapter,
            adapterCache,
            new NoOpStreamDeliveryFailureHandler(),
            new FixedBackoff(TimeSpan.Zero),
            new FixedBackoff(TimeSpan.Zero),
            new FakeTimeProvider(Now),
            shared);
        await agent.RunOrQueueTask(() => agent.Initialize(TestContext.Current.CancellationToken));
        Assert.NotNull(transport);
        return new AgentLifetime(agent, receiver, transport);
    }

    private sealed class AgentLifetime(
        PersistentStreamPullingAgent agent,
        EventHubAdapterReceiver receiver,
        PartitionTransport transport) : IAsyncDisposable
    {
        public PersistentStreamPullingAgent.ITestAccessor Accessor { get; } = agent;
        public PartitionTransport Transport { get; } = transport;
        public bool UsesCertifiedDeliveryProgress => receiver.UsesCertifiedDeliveryProgress;

        public Task<bool> Read(int count) => Accessor.ReadFromQueue(Queue, receiver, count);

        public async Task<StreamConsumerData> AddConsumer(StreamId streamId, RecordingConsumer consumer)
        {
            var qualifiedId = new QualifiedStreamId(ProviderName, streamId);
            await Accessor.RegisterStream(qualifiedId, Token(0), Now.UtcDateTime);
            var stream = (await Accessor.GetPubSubCache())[qualifiedId];
            return await agent.RunOrQueueTaskResult(() => stream.AddConsumer(
                GuidId.GetGuidId(Guid.NewGuid()), qualifiedId, consumer, null, Now.UtcDateTime));
        }

        public ValueTask DisposeAsync() => new(Accessor.Shutdown());
    }

    private sealed class LegacyPartitionTransport(PartitionTransport transport) : IEventHubReceiver
    {
        public Task<IEnumerable<EventData>> ReceiveAsync(int maxCount, TimeSpan waitTime) => transport.ReceiveAsync(maxCount, waitTime);
        public Task CloseAsync() => transport.CloseAsync();
    }

    private sealed class PartitionTransport(EventData[] events, string offset) : IEventHubReceiver, IQueueAdapterReceiverReadRecovery
    {
        // Match EventHubReceiverProxy's inclusive EventPosition.FromOffset(offset, true).
        private readonly Queue<EventData> _remaining = new(events.Where(message =>
            offset == EventHubConstants.StartOfStream
            || long.Parse(message.OffsetString, CultureInfo.InvariantCulture) >= long.Parse(offset, CultureInfo.InvariantCulture)));

        public string RequestedOffset { get; } = offset;
        public List<long> ReceivedSequences { get; } = [];
        public bool Closed { get; private set; }

        public Task RecoverReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<IEnumerable<EventData>> ReceiveAsync(int maxCount, TimeSpan waitTime)
        {
            var result = new List<EventData>();
            while (result.Count < maxCount && _remaining.TryDequeue(out var message))
            {
                result.Add(message);
                ReceivedSequences.Add(message.SequenceNumber);
            }

            return Task.FromResult<IEnumerable<EventData>>(result);
        }

        public Task CloseAsync()
        {
            Closed = true;
            return Task.CompletedTask;
        }
    }

    private sealed class CheckpointStore : IStreamCheckpointStore
    {
        public StreamCheckpointStoreState State { get; private set; } = new(string.Empty, string.Empty);
        public List<string> Writes { get; } = [];

        public ValueTask<StreamCheckpointStoreState> Load(CancellationToken cancellationToken)
            => ValueTask.FromResult(State);

        public ValueTask<StreamCheckpointStoreState> Update(
            string checkpoint, string expectedVersion, CancellationToken cancellationToken)
        {
            Assert.Equal(State.Version, expectedVersion);
            Writes.Add(checkpoint);
            State = new(checkpoint, Writes.Count.ToString(CultureInfo.InvariantCulture));
            return ValueTask.FromResult(State);
        }
    }

    private sealed class RecordingConsumer(StreamHandshakeToken? resumeToken = null) : IStreamConsumerExtension
    {
        public List<int> Events { get; } = [];
        public List<Exception> Errors { get; } = [];
        public Func<IBatchContainer, Task<StreamHandshakeToken?>>? OnDelivery { get; set; }

        public Task<StreamHandshakeToken?> DeliverBatch(
            GuidId subscriptionId, QualifiedStreamId streamId, IBatchContainer item,
            StreamHandshakeToken? handshakeToken, CancellationToken cancellationToken)
        {
            Events.AddRange(item.GetEvents<int>().Select(message => message.Item1));
            return OnDelivery?.Invoke(item) ?? Task.FromResult<StreamHandshakeToken?>(null);
        }

        public Task<StreamHandshakeToken?> GetSequenceToken(GuidId subscriptionId, CancellationToken cancellationToken)
            => Task.FromResult(resumeToken);

        public Task<StreamHandshakeToken?> DeliverImmutable(
            GuidId subscriptionId, QualifiedStreamId streamId, object item, StreamSequenceToken currentToken,
            StreamHandshakeToken? handshakeToken, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<StreamHandshakeToken?> DeliverMutable(
            GuidId subscriptionId, QualifiedStreamId streamId, object item, StreamSequenceToken currentToken,
            StreamHandshakeToken? handshakeToken, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task CompleteStream(GuidId subscriptionId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ErrorInStream(GuidId subscriptionId, Exception exc, CancellationToken cancellationToken)
        {
            Errors.Add(exc);
            return Task.CompletedTask;
        }
    }
}
