using System.Globalization;
using System.Net;
using Azure.Messaging.EventHubs;
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
        using var services = new ServiceCollection()
            .AddMetrics()
            .AddSerializer()
            .AddSingleton<OrleansInstruments>()
            .AddSingleton<SchedulerInstruments>()
            .AddSingleton<CatalogInstruments>()
            .AddSingleton<GrainInstruments>()
            .AddSingleton<MessagingInstruments>()
            .AddSingleton<MessagingProcessingInstruments>()
            .BuildServiceProvider();
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

            Assert.Equal([1], idle.Events);
            Assert.Equal(Enumerable.Range(2, 199), busy.Events);
            Assert.Equal(1, idleSubscription.LastProcessedToken!.SequenceNumber);
            Assert.Equal(200, busySubscription.LastProcessedToken!.SequenceNumber);
            Assert.True(idleSubscription.IsCaughtUp);
            Assert.True(busySubscription.IsCaughtUp);
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

    private sealed class ThrowingQueueCursor(IQueueCacheCursor inner) : IQueueCacheCursor
    {
        public void Dispose() => inner.Dispose();
        public IBatchContainer? GetCurrent(out Exception? exception) => inner.GetCurrent(out exception);
        public bool MoveNext() => throw new InvalidOperationException("Injected cursor read failure");
        public QueueCacheCursorMoveResult MoveNextWithResult() => throw new InvalidOperationException("Injected cursor read failure");
        public void Refresh(StreamSequenceToken token) => inner.Refresh(token);
        public void RecordDeliveryFailure() => inner.RecordDeliveryFailure();
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
        CheckpointStore store)
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
            cacheFactory: (partition, checkpointer, _) => new EventHubQueueCache(
                partition,
                EventHubAdapterReceiver.MaxMessagesPerRead,
                new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(1024 * 1024)),
                adapter,
                new ChronologicalEvictionStrategy(
                    NullLogger.Instance,
                    new TimePurgePredicate(TimeSpan.FromHours(1), TimeSpan.FromHours(1)),
                    null!,
                    null),
                checkpointer,
                NullLogger.Instance,
                null!,
                null,
                null),
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
            eventHubReceiverFactory: (_, offset, _) => transport = new PartitionTransport(events, offset));

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

    private sealed class PartitionTransport(EventData[] events, string offset) : IEventHubReceiver
    {
        // Match EventHubReceiverProxy's inclusive EventPosition.FromOffset(offset, true).
        private readonly Queue<EventData> _remaining = new(events.Where(message =>
            offset == EventHubConstants.StartOfStream
            || long.Parse(message.OffsetString, CultureInfo.InvariantCulture) >= long.Parse(offset, CultureInfo.InvariantCulture)));

        public string RequestedOffset { get; } = offset;
        public List<long> ReceivedSequences { get; } = [];
        public bool Closed { get; private set; }

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

        public Task<StreamHandshakeToken?> DeliverBatch(
            GuidId subscriptionId, QualifiedStreamId streamId, IBatchContainer item,
            StreamHandshakeToken? handshakeToken, CancellationToken cancellationToken)
        {
            Events.AddRange(item.GetEvents<int>().Select(message => message.Item1));
            return Task.FromResult<StreamHandshakeToken?>(null);
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
