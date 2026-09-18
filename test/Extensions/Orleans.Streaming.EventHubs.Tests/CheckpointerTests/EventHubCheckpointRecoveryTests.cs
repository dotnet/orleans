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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistentDeliveryFailure_CheckpointsAccordingToConfiguredPolicy(bool retry)
    {
        using var services = CreateServices();
        var adapter = new EventHubDataAdapter(services.GetRequiredService<Serializer>());
        var idleId = StreamId.Create("policy", "idle");
        var busyId = StreamId.Create("policy", "busy");
        var events = Enumerable.Range(1, 4)
            .Select(sequence => CreateEvent(adapter, sequence % 2 == 1 ? idleId : busyId, sequence)).ToArray();
        var store = new CheckpointStore();
        var acknowledged = new List<int>();
        var idle = new RecordingConsumer
        {
            OnDelivery = batch =>
            {
                if (batch.SequenceToken.SequenceNumber == 3)
                {
                    return Task.FromException<StreamHandshakeToken?>(new InvalidOperationException("Persistent consumer failure"));
                }
                acknowledged.AddRange(batch.GetEvents<int>().Select(item => item.Item1));
                return Task.FromResult<StreamHandshakeToken?>(null);
            },
        };
        var busy = new RecordingConsumer();
        var backoff = Substitute.For<IBackoffProvider>();
        backoff.Next(Arg.Any<int>()).Returns(_ => throw new TimeoutException("Retry budget exhausted"));
        await using var lifetime = await CreateAgent(services, adapter, events, store,
            options: new StreamPullingAgentOptions { RetryFailedDeliveries = retry }, deliveryBackoff: backoff);
        var idleSubscription = await lifetime.AddConsumer(idleId, idle);
        var busySubscription = await lifetime.AddConsumer(busyId, busy);
        Assert.True(await lifetime.Read(2));
        await lifetime.Accessor.AddSubscriber(idleSubscription);
        await lifetime.Accessor.AddSubscriber(busySubscription);

        Assert.True(await lifetime.Read(2));
        Assert.Equal([1], acknowledged);
        Assert.Equal([2, 4], busy.Events);
        Assert.Equal(1, idleSubscription.LastProcessedToken?.SequenceNumber);
        Assert.Equal(retry ? 2 : 4, idleSubscription.LastSafePartitionToken?.SequenceNumber);
        Assert.Single(idle.Errors);
        await lifetime.Accessor.Shutdown();
        Assert.Equal([retry ? "2" : "4"], store.Writes);
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
    [InlineData(false, false, false, false)]
    [InlineData(false, false, false, true)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, false, true)]
    [InlineData(false, true, true, false)]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(true, false, true, false)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    [InlineData(true, true, true, true)]
    public async Task CertifiedProgressRequiresNativeComposition(
        bool derivedCache, bool customDataAdapter, bool customEviction, bool legacyTransport)
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer>();
        var adapter = customDataAdapter ? new ThrowingDataAdapter(serializer) : new EventHubDataAdapter(serializer);
        var stream = StreamId.Create("legacy", "cache");
        var pool = new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(1024 * 1024));
        var checkpointer = Substitute.For<IStreamQueueCheckpointer<string>>();
        IEvictionStrategy eviction = customEviction
            ? new LegacyEvictionStrategy()
            : new ChronologicalEvictionStrategy(NullLogger.Instance, new ImmediatePurgePredicate(), null, null);
        EventHubQueueCache cache = derivedCache
            ? new LegacyDerivedCache(pool, adapter, eviction, checkpointer)
            : CreateCache(pool, adapter, eviction, checkpointer);
        var transport = new PartitionTransport([], EventHubConstants.StartOfStream);
        var receiver = new EventHubAdapterReceiver(
            new EventHubPartitionSettings { Hub = new(), Partition = "0", ReceiverOptions = new() },
            cacheFactory: (_, _, _) => cache,
            checkpointerFactory: _ => Task.FromResult(checkpointer),
            loggerFactory: NullLoggerFactory.Instance,
            monitor: new DefaultEventHubReceiverMonitor(new(), services.GetRequiredService<OrleansInstruments>()),
            loadSheddingOptions: new(),
            environmentStatisticsProvider: new EnvironmentStatisticsProvider(),
            eventHubReceiverFactory: (_, _, _) => legacyTransport ? new LegacyPartitionTransport(transport) : transport);
        await receiver.Initialize(TimeSpan.FromSeconds(5));
        try
        {
            var certified = !derivedCache && !customDataAdapter && !customEviction && !legacyTransport;
            Assert.Equal(certified, receiver.UsesCertifiedDeliveryProgress);
            cache.Add([CreateEvent(adapter, stream, 1), CreateEvent(adapter, stream, 2)], Now.UtcDateTime);
            Assert.Equal(certified ? 999 : 1000, cache.GetMaxAddCount());
            var cursor = ((IQueueCache)receiver).TryGetCacheCursor(stream, Token(1)).Cursor!;
            Assert.Equal(certified, cursor is IQueueCacheCursorProgress);
            cache.SignalPurge();
            var updates = checkpointer.ReceivedCalls().Where(call => call.GetMethodInfo().Name == nameof(IStreamQueueCheckpointer<string>.Update)).ToArray();
            if (certified)
            {
                Assert.Empty(updates);
                Assert.Throws<ArgumentNullException>(() => receiver.UpdateDeliveryProgress(null, Now.UtcDateTime));
                Assert.Equal(QueueCacheCursorMoveResultKind.Success, cursor.MoveNextWithResult().Kind);
                Assert.Equal(1, Assert.Single(cursor.GetCurrent(out _)!.GetEvents<int>()).Item1);
            }
            else
            {
                Assert.Equal("2", Assert.Single(updates).GetArguments()[0]);
                receiver.UpdateDeliveryProgress(null, Now.UtcDateTime);
                Assert.Single(checkpointer.ReceivedCalls(), call => call.GetMethodInfo().Name == nameof(IStreamQueueCheckpointer<string>.Update));
            }

            receiver.UpdateDeliveryProgress(Token(2), Now.UtcDateTime);
            var lastUpdate = checkpointer.ReceivedCalls().Last(call => call.GetMethodInfo().Name == nameof(IStreamQueueCheckpointer<string>.Update));
            Assert.Equal("2", lastUpdate.GetArguments()[0]);
        }
        finally
        {
            await receiver.Shutdown(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class LegacyEvictionStrategy()
        : ChronologicalEvictionStrategy(NullLogger.Instance, new ImmediatePurgePredicate(), null, null);

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

        await using var lifetime = await CreateAgent(services, adapter, events, store, enableCertifiedProgress: true);
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
        using var cache = CreateCache(pool, adapter,
            new ChronologicalEvictionStrategy(NullLogger.Instance, new ImmediatePurgePredicate(), null!, null),
            checkpointer);
        cache.EnableCertifiedDeliveryProgress();
        cache.Add([CreateEvent(adapter, streamId, 1), CreateEvent(adapter, streamId, 2)], Now.UtcDateTime);

        cache.SignalPurge();
        var cursor = cache.TryGetCursor(streamId, Token(1)).Cursor!;
        ((IQueueCacheCursorProgress)cursor).EnableDeliveryProgress();
        Assert.Equal(QueueCacheCursorMoveResultKind.Success, cache.TryGetNextMessageWithResult(cursor, out var first).Kind);
        Assert.Equal(1, Assert.Single(first!.GetEvents<int>()).Item1);
        ((IQueueCacheCursorProgress)cursor).RecordDeliveryCompletion();
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
        using var cache = CreateCache(
            new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(1024 * 1024)), adapter,
            new ThrowingEvictionStrategy(), Substitute.For<IStreamQueueCheckpointer<string>>());
        cache.EnableCertifiedDeliveryProgress();
        cache.Add([CreateEvent(adapter, streamId, 1)], Now.UtcDateTime);
        Assert.Throws<InvalidOperationException>(() => cache.UpdateDeliveryProgress(Token(1), Now.UtcDateTime.AddMinutes(1)));

        cache.SignalPurge();
        var cursor = cache.TryGetCursor(streamId, Token(1)).Cursor!;
        ((IQueueCacheCursorProgress)cursor).EnableDeliveryProgress();
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
        using var cache = CreateCache(pool, adapter,
            new ChronologicalEvictionStrategy(NullLogger.Instance, new ImmediatePurgePredicate(), monitor, null),
            Substitute.For<IStreamQueueCheckpointer<string>>());
        cache.EnableCertifiedDeliveryProgress();
        var messages = new List<EventData> { CreateEvent(adapter, streamId, 1), CreateEvent(adapter, streamId, 2) };
        Assert.Throws<InvalidOperationException>(() => cache.Add(messages, Now.UtcDateTime));

        Assert.Equal(2, cache.Add(messages, Now.UtcDateTime).Count);
        Assert.Equal(1, notificationCount);
        var cursor = cache.TryGetCursor(streamId, Token(1)).Cursor!;
        ((IQueueCacheCursorProgress)cursor).EnableDeliveryProgress();
        foreach (var expected in new[] { 1, 2 })
        {
            Assert.Equal(QueueCacheCursorMoveResultKind.Success, cache.TryGetNextMessageWithResult(cursor, out var batch).Kind);
            Assert.Equal(expected, Assert.Single(batch!.GetEvents<int>()).Item1);
            ((IQueueCacheCursorProgress)cursor).RecordDeliveryCompletion();
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

    [Fact]
    public void CertifiedAdmission_HealthyConsumersCannotOutweighPinnedSelection()
    {
        using var services = CreateServices();
        var adapter = new EventHubDataAdapter(services.GetRequiredService<Serializer>());
        var stream = StreamId.Create("admission", "weighted");
        var messages = Enumerable.Range(1000, 256).Select(sequence => CreateEvent(adapter, stream, sequence)).ToList();
        var segmentSize = GetEncodedSize(adapter, messages[0]);
        Assert.All(messages, message => Assert.Equal(segmentSize, GetEncodedSize(adapter, message)));
        const int bufferLimit = 4;
        const int messagesPerBuffer = 16;
        var allocated = 0;
        var pool = new ObjectPool<FixedSizeBuffer>(() =>
        {
            allocated++;
            return new FixedSizeBuffer(messagesPerBuffer * segmentSize);
        });
        var checkpointer = Substitute.For<IStreamQueueCheckpointer<string>>();
        using var cache = CreateCache(pool, adapter,
            new ChronologicalEvictionStrategy(NullLogger.Instance, new ImmediatePurgePredicate(), null, null),
            checkpointer, bufferLimit);
        var pressure = new AveragingCachePressureMonitor(NullLogger.Instance);
        cache.AddCachePressureMonitor(pressure);
        Assert.True(cache.TryEnableCertifiedDeliveryProgress());

        var admitted = 0;
        var healthyConsumers = new object[12];
        while (admitted < messages.Count)
        {
            var allowance = cache.GetMaxAddCount();
            if (allowance == 0)
            {
                break;
            }

            var count = Math.Min(allowance, messages.Count - admitted);
            cache.Add(messages.GetRange(admitted, count), Now.UtcDateTime);
            for (var consumer = 0; consumer < healthyConsumers.Length; consumer++)
            {
                if (healthyConsumers[consumer] is null)
                {
                    healthyConsumers[consumer] = cache.TryGetCursor(stream, Token(1000)).Cursor!;
                    ((IQueueCacheCursorProgress)healthyConsumers[consumer]).EnableDeliveryProgress();
                }

                var healthy = healthyConsumers[consumer];
                for (var index = 0; index < count; index++)
                {
                    Assert.Equal(QueueCacheCursorMoveResultKind.Success, cache.TryGetNextMessageWithResult(healthy, out var delivered).Kind);
                    Assert.Equal(1000 + admitted + index, Assert.Single(delivered!.GetEvents<int>()).Item1);
                    ((IQueueCacheCursorProgress)healthy).RecordDeliveryCompletion();
                }
            }

            admitted += count;
        }

        Assert.Equal(bufferLimit, allocated);
        Assert.Equal((bufferLimit - 1) * messagesPerBuffer + 1, admitted);
        var pinned = cache.TryGetCursor(stream, Token(1000)).Cursor!;
        var pinnedProgress = Assert.IsAssignableFrom<IQueueCacheCursorProgress>(pinned);
        pinnedProgress.EnableDeliveryProgress();
        Assert.Equal(QueueCacheCursorMoveResultKind.Success, cache.TryGetNextMessageWithResult(pinned, out var selected).Kind);
        Assert.Null(pinnedProgress.SafeSequenceToken);
        for (var consumer = 0; consumer < 12; consumer++)
        {
            var healthy = cache.TryGetCursor(stream, Token(1000 + admitted - 1)).Cursor!;
            var progress = Assert.IsAssignableFrom<IQueueCacheCursorProgress>(healthy);
            progress.EnableDeliveryProgress();
            Assert.Equal(QueueCacheCursorMoveResultKind.Success, cache.TryGetNextMessageWithResult(healthy, out var delivered).Kind);
            Assert.Equal(1000 + admitted - 1, Assert.Single(delivered!.GetEvents<int>()).Item1);
            progress.RecordDeliveryCompletion();
        }

        // Evaluate the default weighted monitor deterministically, after all contributions.
        Assert.False(pressure.IsUnderPressure(new DateTime(9999, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        for (var pausedRead = 0; pausedRead < 10; pausedRead++)
        {
            cache.SignalPurge();
            Assert.Equal(0, cache.GetMaxAddCount());
            Assert.Equal(1000, Assert.Single(selected!.GetEvents<int>()).Item1);
            Assert.Equal(bufferLimit, allocated);
        }
        Assert.DoesNotContain(checkpointer.ReceivedCalls(), call => call.GetMethodInfo().Name == nameof(IStreamQueueCheckpointer<string>.Update));

        pinnedProgress.RecordDeliveryCompletion();
        cache.UpdateDeliveryProgress(Token(1000), Now.UtcDateTime);
        Assert.Equal(0, cache.GetMaxAddCount()); // The certified record still shares its buffer with retained records.
        cache.UpdateDeliveryProgress(Token(1000 + messagesPerBuffer - 1), Now.UtcDateTime);
        Assert.Equal(1, cache.GetMaxAddCount());
        cache.Add([messages[admitted]], Now.UtcDateTime);
        Assert.Equal(bufferLimit, allocated);
        cache.UpdateDeliveryProgress(Token(1000 + admitted), Now.UtcDateTime);
        Assert.Equal(bufferLimit, cache.GetMaxAddCount());
    }

    [Fact]
    public void CertifiedAdmission_LargeRecordsReserveTheWholeReadWithoutOvershoot()
    {
        using var services = CreateServices();
        var adapter = new EventHubDataAdapter(services.GetRequiredService<Serializer>());
        var stream = StreamId.Create("admission", "large");
        var messages = Enumerable.Range(1000, 4).Select(sequence => CreateEvent(adapter, stream, sequence)).ToList();
        var bufferSize = GetEncodedSize(adapter, messages[0]);
        Assert.All(messages, message => Assert.Equal(bufferSize, GetEncodedSize(adapter, message)));
        var allocated = 0;
        var pool = new ObjectPool<FixedSizeBuffer>(() =>
        {
            allocated++;
            return new FixedSizeBuffer(bufferSize);
        });
        using var cache = CreateCache(pool, adapter,
            new ChronologicalEvictionStrategy(NullLogger.Instance, new ImmediatePurgePredicate(), null, null),
            Substitute.For<IStreamQueueCheckpointer<string>>(), defaultMaxAddCount: 3);
        Assert.True(cache.TryEnableCertifiedDeliveryProgress());

        Assert.Equal(3, cache.GetMaxAddCount());
        cache.Add([messages[0]], Now.UtcDateTime);
        Assert.Equal(2, cache.GetMaxAddCount());
        cache.Add(messages.GetRange(1, cache.GetMaxAddCount()), Now.UtcDateTime);
        Assert.Equal(3, allocated);
        Assert.Equal(0, cache.GetMaxAddCount());
        cache.SignalPurge();
        Assert.Equal(0, cache.GetMaxAddCount());

        cache.UpdateDeliveryProgress(Token(1000), Now.UtcDateTime);
        Assert.Equal(1, cache.GetMaxAddCount());
        cache.Add([messages[3]], Now.UtcDateTime);
        Assert.Equal(3, allocated);
        Assert.Equal(0, cache.GetMaxAddCount());
        var cursor = cache.TryGetCursor(stream, Token(1001)).Cursor!;
        foreach (var sequence in new[] { 1001, 1002, 1003 })
        {
            Assert.Equal(QueueCacheCursorMoveResultKind.Success, cache.TryGetNextMessageWithResult(cursor, out var message).Kind);
            Assert.Equal(sequence, Assert.Single(message!.GetEvents<int>()).Item1);
        }
    }

    [Fact]
    public void CertifiedAdmission_FailedPackingRestoresBufferReservation()
    {
        using var services = CreateServices();
        var adapter = new ThrowingDataAdapter(services.GetRequiredService<Serializer>());
        var stream = StreamId.Create("admission", "rollback");
        var messages = Enumerable.Range(1000, 2).Select(sequence => CreateEvent(adapter, stream, sequence)).ToList();
        var bufferSize = GetEncodedSize(adapter, messages[0]);
        using var cache = CreateCache(new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(bufferSize)), adapter,
            new ChronologicalEvictionStrategy(NullLogger.Instance, new ImmediatePurgePredicate(), null, null),
            Substitute.For<IStreamQueueCheckpointer<string>>(), defaultMaxAddCount: 2);
        cache.EnableCertifiedDeliveryProgress();
        adapter.FailAtSequence = 1001;

        Assert.Throws<InvalidOperationException>(() => cache.Add(messages, Now.UtcDateTime));
        Assert.Equal(2, cache.GetMaxAddCount());
        cache.Add(messages, Now.UtcDateTime);
        Assert.Equal(0, cache.GetMaxAddCount());
        cache.UpdateDeliveryProgress(Token(1001), Now.UtcDateTime);
        Assert.Equal(2, cache.GetMaxAddCount());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void CertifiedAdmission_PurgeObserverFailureReleasesOwnedBuffers(int failureStage)
    {
        using var services = CreateServices();
        var adapter = new EventHubDataAdapter(services.GetRequiredService<Serializer>());
        var stream = StreamId.Create("admission", "purge-observer");
        var messages = Enumerable.Range(1000, 2).Select(sequence => CreateEvent(adapter, stream, sequence)).ToList();
        var buffers = new List<FixedSizeBuffer>();
        var pool = new ObjectPool<FixedSizeBuffer>(() =>
        {
            var buffer = new FixedSizeBuffer(2 * GetEncodedSize(adapter, messages[0]));
            buffers.Add(buffer);
            return buffer;
        });
        var monitor = Substitute.For<ICacheMonitor>();
        var failure = new InvalidOperationException("Purge observer failed");
        if (failureStage == 0)
            monitor.WhenForAnyArgs(value => value.TrackMessagesPurged(default)).Do(_ => throw failure);
        if (failureStage == 1)
            monitor.WhenForAnyArgs(value => value.TrackMemoryReleased(default)).Do(_ => throw failure);
        var eviction = new ChronologicalEvictionStrategy(NullLogger.Instance, new ImmediatePurgePredicate(), monitor, null);
        using var cache = CreateCache(pool, adapter, eviction, Substitute.For<IStreamQueueCheckpointer<string>>(), defaultMaxAddCount: 2);
        if (failureStage == 2) eviction.OnPurged = (_, _) => throw failure;
        Assert.True(cache.TryEnableCertifiedDeliveryProgress());
        cache.Add([messages[0]], Now.UtcDateTime);
        Assert.Equal(1, cache.GetMaxAddCount());

        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => cache.UpdateDeliveryProgress(Token(1000), Now.UtcDateTime)));

        Assert.Equal(2, cache.GetMaxAddCount());
        using var reclaimed = pool.Allocate();
        Assert.Same(Assert.Single(buffers), reclaimed);
        cache.Add([messages[1]], Now.UtcDateTime);
        Assert.Equal(2, buffers.Count);
        Assert.Equal(1, cache.GetMaxAddCount());
        var cursor = cache.TryGetCursor(stream, Token(1001)).Cursor!;
        Assert.Equal(QueueCacheCursorMoveResultKind.Success, cache.TryGetNextMessageWithResult(cursor, out var message).Kind);
        Assert.Equal(1001, Assert.Single(message!.GetEvents<int>()).Item1);
    }

    [Fact]
    public async Task CertifiedAdmission_FullCacheCanRetryPendingHandoffWithoutReceivingAgain()
    {
        using var services = CreateServices();
        var adapter = new EventHubDataAdapter(services.GetRequiredService<Serializer>());
        var stream = StreamId.Create("admission", "handoff");
        var messages = Enumerable.Range(1000, 2).Select(sequence => CreateEvent(adapter, stream, sequence)).ToArray();
        var monitor = Substitute.For<ICacheMonitor>();
        monitor.WhenForAnyArgs(value => value.TrackMemoryAllocated(default))
            .Do(_ => throw new InvalidOperationException("Allocation observer failed after ownership transfer"));
        var checkpointer = Substitute.For<IStreamQueueCheckpointer<string>>();
        using var cache = CreateCache(new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(GetEncodedSize(adapter, messages[0]))), adapter,
            new ChronologicalEvictionStrategy(NullLogger.Instance, new ImmediatePurgePredicate(), monitor, null),
            checkpointer, defaultMaxAddCount: 1);
        var transport = new PartitionTransport(messages, EventHubConstants.StartOfStream);
        var receiver = new EventHubAdapterReceiver(
            new EventHubPartitionSettings { Hub = new(), Partition = "0", ReceiverOptions = new() },
            cacheFactory: (_, _, _) => cache,
            checkpointerFactory: _ => Task.FromResult(checkpointer),
            loggerFactory: NullLoggerFactory.Instance,
            monitor: new DefaultEventHubReceiverMonitor(new(), services.GetRequiredService<OrleansInstruments>()),
            loadSheddingOptions: new(),
            environmentStatisticsProvider: new EnvironmentStatisticsProvider(),
            eventHubReceiverFactory: (_, _, _) => transport);
        await receiver.Initialize(TimeSpan.FromSeconds(5));
        try
        {
            Assert.True(receiver.UsesCertifiedDeliveryProgress);
            Assert.Equal(1, receiver.GetMaxAddCount());
            await Assert.ThrowsAsync<InvalidOperationException>(() => receiver.GetQueueMessagesAsync(
                receiver.GetMaxAddCount(), TestContext.Current.CancellationToken));
            Assert.Equal(0, cache.GetMaxAddCount());
            Assert.Equal(1, receiver.GetMaxAddCount());
            Assert.Equal([1000L], transport.ReceivedSequences);
            await receiver.RecoverReadAsync(TestContext.Current.CancellationToken);
            Assert.Single(await receiver.GetQueueMessagesAsync(receiver.GetMaxAddCount(), TestContext.Current.CancellationToken));
            Assert.Equal([1000L], transport.ReceivedSequences);
            Assert.True(receiver.UsesCertifiedDeliveryProgress);
            Assert.Equal(0, receiver.GetMaxAddCount());
        }
        finally
        {
            await receiver.Shutdown(TimeSpan.FromSeconds(5));
        }
    }

    private static int GetEncodedSize(EventHubDataAdapter adapter, EventData message)
        => adapter.FromQueueMessage(adapter.GetStreamPosition("0", message), message, Now.UtcDateTime,
            size => new ArraySegment<byte>(new byte[size])).Segment.Count;

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

    private static EventHubQueueCache CreateCache(
        IObjectPool<FixedSizeBuffer> pool,
        IEventHubDataAdapter adapter,
        IEvictionStrategy eviction,
        IStreamQueueCheckpointer<string> checkpointer,
        int defaultMaxAddCount = 1000)
        => new("0", defaultMaxAddCount, pool, adapter, eviction, checkpointer, NullLogger.Instance, null!, null, null);

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
        public void EnableDeliveryProgress() => ((IQueueCacheCursorProgress)inner).EnableDeliveryProgress();
        public void SetDeliveredThrough(StreamSequenceToken token) => ((IQueueCacheCursorProgress)inner).SetDeliveredThrough(token);
        public void RecordDeliveryCompletion() => ((IQueueCacheCursorProgress)inner).RecordDeliveryCompletion();
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
        bool legacyTransport = false,
        bool enableCertifiedProgress = false,
        StreamPullingAgentOptions? options = null,
        IBackoffProvider? deliveryBackoff = null)
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
            cacheFactory: (_, checkpointer, _) =>
            {
                var cache = CreateCache(
                    new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(1024 * 1024)),
                    adapter,
                    new ChronologicalEvictionStrategy(
                        NullLogger.Instance,
                        purgeImmediately ? new ImmediatePurgePredicate() : new TimePurgePredicate(TimeSpan.FromHours(1), TimeSpan.FromHours(1)),
                        null!,
                        null),
                    checkpointer);
                if (enableCertifiedProgress)
                {
                    cache.EnableCertifiedDeliveryProgress();
                }

                return cache;
            },
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
            options ?? new StreamPullingAgentOptions(),
            queueAdapter,
            adapterCache,
            new NoOpStreamDeliveryFailureHandler(),
            deliveryBackoff ?? new FixedBackoff(TimeSpan.Zero),
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
