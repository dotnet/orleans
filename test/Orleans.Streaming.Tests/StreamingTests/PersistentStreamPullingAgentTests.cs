using System.Collections.Generic;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Internal;
using Orleans.Providers.Streams.Common;
using Orleans.Providers.Streams.Generator;
using Orleans.Runtime;
using Orleans.Runtime.Scheduler;
using Orleans.Streams;
using Orleans.Streams.Filtering;
using Orleans.Timers;
using TestExtensions;
using Xunit;

namespace UnitTests.StreamingTests
{
    public class PersistentStreamPullingAgentTests
    {
        [Fact]
        public void InitialDeliveryProgressIncludesOnlyAcknowledgedDeliveryToken()
        {
            var token = new EventSequenceTokenV2(1);

            Assert.Null(PersistentStreamPullingAgent.GetInitialDeliveryProgress(null));
            Assert.Null(PersistentStreamPullingAgent.GetInitialDeliveryProgress(StreamHandshakeToken.CreateStartToken(token)));
            Assert.Null(PersistentStreamPullingAgent.GetInitialDeliveryProgress(
                StreamHandshakeToken.CreateStartPositionToken(StreamSubscriptionStartPosition.EarliestAvailable)));
            Assert.Equal(
                token,
                PersistentStreamPullingAgent.GetInitialDeliveryProgress(StreamHandshakeToken.CreateDeliveyToken(token)));
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task ReadFromQueue_DoesNotWaitForColdStreamRegistration()
        {
            var registration = new TaskCompletionSource<ISet<PubSubSubscriptionState>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default)
                .ReturnsForAnyArgs(_ => registration.Task);

            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var streamId = StreamId.Create("namespace", Guid.NewGuid());
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            // Use Arg.Any<int>() to match regardless of the maxCacheAddCount value.
            receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IList<IBatchContainer>>(
                [
                    new GeneratedBatchContainer(streamId, 1, new EventSequenceTokenV2(1)),
                ]));

            var agent = CreateAgent(pubSub, queueId);
            var testAccessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);

            var readTask = testAccessor.ReadFromQueue(queueId, receiver, 1);

            // ReadFromQueue adds the stream entry synchronously and tracks the in-flight
            // background registration task for the cold stream.
            var cache = await testAccessor.GetPubSubCache();
            Assert.Single(cache);

            var (_, streamData) = cache.Single();
            var registrationTask = streamData.RegistrationTask;
            Assert.NotNull(registrationTask);
            Assert.False(registrationTask.IsCompleted, "Registration should still be in progress");

            Assert.True(await readTask, "ReadFromQueue should return true indicating data was read");

            Assert.False(await testAccessor.ReadFromQueue(queueId, receiver, 1));
            await receiver.Received(1).GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());

            // Completing registration should resolve the tracked task and clear it.
            registration.SetResult(new HashSet<PubSubSubscriptionState>());
            await registrationTask;
            Assert.True(await testAccessor.ReadFromQueue(queueId, receiver, 1));
            Assert.Null(streamData.RegistrationTask);
            await receiver.Received(2).GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task ReadFromQueue_ClearsRegistrationTaskWhenColdStreamRegistrationCompletesSynchronously()
        {
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default)
                .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));

            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var streamId = StreamId.Create("namespace", Guid.NewGuid());
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IList<IBatchContainer>>(
                [
                    new GeneratedBatchContainer(streamId, 1, new EventSequenceTokenV2(1)),
                ]));

            var agent = CreateAgent(pubSub, queueId);
            var testAccessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);

            var readResult = await testAccessor.ReadFromQueue(queueId, receiver, 1);
            Assert.True(readResult, "ReadFromQueue should return true indicating data was read");

            var cache = await testAccessor.GetPubSubCache();
            Assert.Single(cache);

            var (_, streamData) = cache.Single();
            var registrationTask = streamData.RegistrationTask;
            if (registrationTask is not null)
            {
                await registrationTask;
                Assert.Null(streamData.RegistrationTask);
            }

            Assert.True(streamData.StreamRegistered);
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task ReadFromQueue_DoesNotStartQueueReadAfterShutdownStarts()
        {
            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            var agent = CreateAgent(pubSub: null, queueId);
            var testAccessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);

            await testAccessor.Shutdown();

            var readResult = await testAccessor.ReadFromQueue(queueId, receiver, 1);

            Assert.False(readResult);
            Assert.Empty(receiver.ReceivedCalls());
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task ReadFromQueue_TreatsNullReceiverResultAsEmpty()
        {
            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            // Simulate a receiver binary compiled before the return value was annotated as non-null.
            receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IList<IBatchContainer>>(null!));
            var agent = CreateAgent(pubSub: null, queueId);
            var testAccessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);

            var readResult = await testAccessor.ReadFromQueue(queueId, receiver, 1);

            Assert.False(readResult);
            await receiver.Received(1).GetQueueMessagesAsync(1, CancellationToken.None);
            Assert.Empty(await testAccessor.GetPubSubCache());
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task RegisterStream_RemovesCacheEntryWhenProducerRegistrationTerminates()
        {
            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var streamId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            var agent = CreateAgent(pubSub: null, queueId);
            var testAccessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);

            await testAccessor.RegisterStream(streamId, new EventSequenceTokenV2(1), DateTime.UtcNow);

            Assert.Empty(await testAccessor.GetPubSubCache());
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task RegisterStream_DoesNotRegisterProducerAfterShutdownStarts()
        {
            var pubSub = Substitute.For<IStreamPubSub>();
            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var streamId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            var agent = CreateAgent(pubSub, queueId);
            var testAccessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);

            await testAccessor.Shutdown();
            await testAccessor.RegisterStream(streamId, new EventSequenceTokenV2(1), DateTime.UtcNow);

            Assert.Empty(await testAccessor.GetPubSubCache());
            Assert.Empty(pubSub.ReceivedCalls());
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task ReadFromQueue_CleansInactiveStreamsUsingTimeProvider()
        {
            var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default)
                .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));

            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IList<IBatchContainer>>(new List<IBatchContainer>()));

            var streamId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            using var diagnostics = StreamingDiagnosticObserver.Create(
                SiloAddress.New(IPAddress.Loopback, 11111, 1));
            var inactive = diagnostics.WaitForStreamInactiveAsync(streamId.StreamId, "provider", CancellationToken.None);

            var agent = CreateAgent(pubSub, queueId, receiver: receiver, timeProvider: timeProvider);
            var testAccessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            await testAccessor.RegisterStream(streamId, new EventSequenceTokenV2(1), timeProvider.GetUtcNow().UtcDateTime);
            Assert.Single(await testAccessor.GetPubSubCache());

            timeProvider.Advance(new StreamPullingAgentOptions().StreamInactivityPeriod + TimeSpan.FromTicks(1));
            await testAccessor.ReadFromQueue(queueId, receiver, 1);

            await inactive.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Empty(await testAccessor.GetPubSubCache());
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task ReadFromQueue_DoesNotAcknowledgeBatchedMessagesDuringConsumerDelivery()
        {
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default)
                .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));

            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var streamId = StreamId.Create("namespace", Guid.NewGuid());
            var qualifiedStreamId = new QualifiedStreamId("provider", streamId);
            var firstToken = new EventSequenceTokenV2(1);
            var secondToken = new EventSequenceTokenV2(2);
            var messages = new List<IBatchContainer>
            {
                new TestBatchContainer(streamId, firstToken),
                new TestBatchContainer(streamId, secondToken),
            };

            var receiver = Substitute.For<IQueueAdapterReceiver>();
            receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(
                    Task.FromResult<IList<IBatchContainer>>(messages),
                    Task.FromResult<IList<IBatchContainer>>([]),
                    Task.FromResult<IList<IBatchContainer>>([]));
            receiver.MessagesDeliveredAsync(Arg.Any<IList<IBatchContainer>>(), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);

            var queueCache = new SimpleQueueCache(cacheSize: 10, NullLogger.Instance);
            var queueAdapterCache = Substitute.For<IQueueAdapterCache>();
            queueAdapterCache.CreateQueueCache(Arg.Any<QueueId>()).Returns(queueCache);
            var options = new StreamPullingAgentOptions { BatchContainerBatchSize = 2 };
            var agent = CreateAgent(pubSub, queueId, receiver, queueAdapterCache, options: options);
            var testAccessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            await testAccessor.RegisterStream(qualifiedStreamId, firstToken, DateTime.UtcNow);

            var streamData = (await testAccessor.GetPubSubCache()).Single().Value;
            var firstConsumer = new RecordingConsumer();
            var firstConsumerData = streamData.AddConsumer(
                GuidId.GetGuidId(Guid.NewGuid()),
                qualifiedStreamId,
                firstConsumer,
                filterData: null,
                now: DateTime.UtcNow);
            firstConsumerData.IsRegistered = true;
            firstConsumerData.Cursor = queueCache.GetCacheCursor(streamId, firstToken);
            var secondConsumer = new RecordingConsumer();
            var secondConsumerData = streamData.AddConsumer(
                GuidId.GetGuidId(Guid.NewGuid()),
                qualifiedStreamId,
                secondConsumer,
                filterData: null,
                now: DateTime.UtcNow);
            secondConsumerData.IsRegistered = true;
            secondConsumerData.Cursor = queueCache.GetCacheCursor(streamId, firstToken);

            Assert.True(await testAccessor.ReadFromQueue(queueId, receiver, 10));
            await Task.WhenAll(firstConsumer.Delivered.Task, secondConsumer.Delivered.Task)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.False(await testAccessor.ReadFromQueue(queueId, receiver, 10));
            await receiver.DidNotReceive().MessagesDeliveredAsync(
                Arg.Any<IList<IBatchContainer>>(),
                Arg.Any<CancellationToken>());

            firstConsumer.ReleaseDelivery();
            await WaitForInactive(firstConsumerData, TestContext.Current.CancellationToken);
            Assert.False(await testAccessor.ReadFromQueue(queueId, receiver, 10));
            await receiver.DidNotReceive().MessagesDeliveredAsync(
                Arg.Any<IList<IBatchContainer>>(),
                Arg.Any<CancellationToken>());

            secondConsumer.ReleaseDelivery();
            await WaitForInactive(secondConsumerData, TestContext.Current.CancellationToken);
            Assert.False(await testAccessor.ReadFromQueue(queueId, receiver, 10));
            await receiver.Received(1).MessagesDeliveredAsync(
                Arg.Is<IList<IBatchContainer>>(items => items.Count == messages.Count),
                Arg.Any<CancellationToken>());

            static async Task WaitForInactive(
                StreamConsumerData consumerData,
                CancellationToken cancellationToken)
            {
                var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                while (consumerData.State != StreamConsumerDataState.Inactive && DateTime.UtcNow < timeout)
                {
                    await Task.Delay(10, cancellationToken);
                }

                Assert.Equal(StreamConsumerDataState.Inactive, consumerData.State);
            }
        }

        private static PersistentStreamPullingAgent CreateAgent(
            IStreamPubSub? pubSub,
            QueueId queueId,
            IQueueAdapterReceiver? receiver = null,
            IQueueAdapterCache? queueAdapterCache = null,
            TimeProvider? timeProvider = null,
            StreamPullingAgentOptions? options = null)
        {
            var siloAddress = SiloAddress.New(IPAddress.Loopback, 11111, 1);
            var localSiloDetails = Substitute.For<ILocalSiloDetails>();
            localSiloDetails.SiloAddress.Returns(siloAddress);
            var timerRegistry = Substitute.For<ITimerRegistry>();
            timerRegistry.RegisterGrainTimer(
                    Arg.Any<IGrainContext>(),
                    Arg.Any<Func<QueueId, CancellationToken, Task>>(),
                    Arg.Any<QueueId>(),
                    Arg.Any<GrainTimerCreationOptions>())
                .Returns(Substitute.For<IGrainTimer>());

            var shared = new SystemTargetShared(
                runtimeClient: null!,
                localSiloDetails,
                NullLoggerFactory.Instance,
                Options.Create(new SchedulingOptions()),
                grainReferenceActivator: null!,
                timerRegistry: timerRegistry,
                activations: new ActivationDirectory(CreateCatalogInstruments()),
                schedulerInstruments: CreateSchedulerInstruments(),
                grainInstruments: CreateGrainInstruments(),
                messagingInstruments: CreateMessagingInstruments(),
                messagingProcessingInstruments: CreateMessagingProcessingInstruments());

            receiver ??= Substitute.For<IQueueAdapterReceiver>();
            receiver.Initialize(Arg.Any<TimeSpan>()).Returns(Task.CompletedTask);

            var queueAdapter = Substitute.For<IQueueAdapter>();
            queueAdapter.Name.Returns("provider");
            queueAdapter.CreateReceiver(Arg.Any<QueueId>()).Returns(receiver);

            return new PersistentStreamPullingAgent(
                SystemTargetGrainId.Create(SystemTargetGrainId.CreateGrainType("persistent-stream-pulling-agent-test"), siloAddress),
                "provider",
                pubSub!,
                new NoOpStreamFilter(),
                queueId,
                options ?? new StreamPullingAgentOptions(),
                queueAdapter,
                queueAdapterCache!,
                new NoOpStreamDeliveryFailureHandler(),
                new FixedBackoff(TimeSpan.FromMilliseconds(1)),
                new FixedBackoff(TimeSpan.FromMilliseconds(1)),
                timeProvider ?? TimeProvider.System,
                shared);
        }

        private sealed class RecordingQueueCache : IQueueCache
        {
            public int DeliveryProgressCallCount { get; private set; }
            public List<StreamSequenceToken?> DeliveryProgressTokens { get; } = new();

            public int GetMaxAddCount() => 1000;

            public void AddToCache(IList<IBatchContainer> messages)
            {
            }

            public bool TryPurgeFromCache(out IList<IBatchContainer> purgedItems)
            {
                purgedItems = null!;
                return false;
            }

            public IQueueCacheCursor GetCacheCursor(StreamId streamId, StreamSequenceToken? token)
            {
                return Substitute.For<IQueueCacheCursor>();
            }

            public bool IsUnderPressure() => false;

            public void UpdateDeliveryProgress(StreamSequenceToken? earliestSubscriptionToken, DateTime utcNow)
            {
                DeliveryProgressCallCount++;
                DeliveryProgressTokens.Add(earliestSubscriptionToken);
            }

            public void ClearDeliveryProgress()
            {
                DeliveryProgressCallCount = 0;
                DeliveryProgressTokens.Clear();
            }
        }

        private sealed class ScriptedQueueCache : IQueueCache
        {
            private readonly List<IBatchContainer> messages = new();

            public int DeliveryProgressCallCount { get; private set; }
            public List<StreamSequenceToken?> DeliveryProgressTokens { get; } = new();

            public int GetMaxAddCount() => 1000;

            public void AddToCache(IList<IBatchContainer> messages)
            {
                this.messages.AddRange(messages);
            }

            public bool TryPurgeFromCache(out IList<IBatchContainer> purgedItems)
            {
                purgedItems = null!;
                return false;
            }

            public IQueueCacheCursor GetCacheCursor(StreamId streamId, StreamSequenceToken? token)
            {
                return new ScriptedQueueCursor(messages, streamId, token);
            }

            public bool IsUnderPressure() => false;

            public void UpdateDeliveryProgress(StreamSequenceToken? earliestSubscriptionToken, DateTime utcNow)
            {
                DeliveryProgressCallCount++;
                DeliveryProgressTokens.Add(earliestSubscriptionToken);
            }

            public void ClearDeliveryProgress()
            {
                DeliveryProgressCallCount = 0;
                DeliveryProgressTokens.Clear();
            }
        }

        private sealed class ScriptedQueueCursor(List<IBatchContainer> messages, StreamId streamId, StreamSequenceToken? token) : IQueueCacheCursor
        {
            private int index = -1;
            private IBatchContainer? current;

            public void Dispose()
            {
            }

            public IBatchContainer GetCurrent(out Exception exception)
            {
                exception = null!;
                return current!;
            }

            public bool MoveNext()
            {
                for (index++; index < messages.Count; index++)
                {
                    var candidate = messages[index];
                    if (candidate.StreamId.Equals(streamId) && (token is null || candidate.SequenceToken.Newer(token)))
                    {
                        current = candidate;
                        return true;
                    }
                }

                current = null;
                return false;
            }

            public void Refresh(StreamSequenceToken token)
            {
            }

            public void RecordDeliveryFailure()
            {
            }
        }

        private sealed class PurgeablePooledQueueCache : IQueueCache
        {
            private readonly PooledQueueCache cache = new(new CacheDataAdapter(), NullLogger.Instance, null, null);

            public int GetMaxAddCount() => 1000;

            public void AddToCache(IList<IBatchContainer> messages)
            {
                var now = DateTime.UtcNow;
                cache.Add(
                    messages.Select(message => new CachedMessage
                    {
                        StreamId = message.StreamId,
                        SequenceNumber = message.SequenceToken.SequenceNumber,
                        EventIndex = message.SequenceToken.EventIndex,
                        EnqueueTimeUtc = now,
                        DequeueTimeUtc = now,
                    }).ToList(),
                    now);
            }

            public bool TryPurgeFromCache(out IList<IBatchContainer> purgedItems)
            {
                purgedItems = null!;
                return false;
            }

            public IQueueCacheCursor GetCacheCursor(StreamId streamId, StreamSequenceToken? token)
                => new Cursor(cache, cache.GetCursor(streamId, token));

            public IQueueCacheCursor GetCacheCursorAtPosition(StreamId streamId, StreamSubscriptionStartPosition startPosition)
                => new Cursor(cache, cache.GetCursorAtPosition(streamId, startPosition));

            public bool IsUnderPressure() => false;

            public void UpdateDeliveryProgress(StreamSequenceToken? earliestSubscriptionToken, DateTime utcNow)
            {
            }

            public void Purge()
            {
                while (!cache.IsEmpty)
                {
                    cache.RemoveOldestMessage();
                }
            }

            private sealed class CacheDataAdapter : ICacheDataAdapter
            {
                public IBatchContainer GetBatchContainer(ref CachedMessage cachedMessage)
                    => new TestBatchContainer(cachedMessage.StreamId, GetSequenceToken(ref cachedMessage));

                public StreamSequenceToken GetSequenceToken(ref CachedMessage cachedMessage)
                    => new EventSequenceTokenV2(cachedMessage.SequenceNumber, cachedMessage.EventIndex);
            }

            private sealed class Cursor(PooledQueueCache cache, object cursor) : IQueueCacheCursor
            {
                private IBatchContainer? current;

                public void Dispose()
                {
                }

                public IBatchContainer GetCurrent(out Exception exception)
                {
                    exception = null!;
                    return current!;
                }

                public bool MoveNext() => cache.TryGetNextMessage(cursor, out current);

                public void Refresh(StreamSequenceToken token) => cache.Refresh(cursor, token);

                public void RecordDeliveryFailure()
                {
                }
            }
        }

        private sealed class TestBatchContainer(StreamId streamId, StreamSequenceToken token) : IBatchContainer
        {
            public StreamId StreamId { get; } = streamId;
            public StreamSequenceToken SequenceToken { get; } = token;
            public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>() => [];
            public bool ImportRequestContext() => false;
        }

        private sealed class RecordingConsumer(StreamHandshakeToken? requestedToken = null) : IStreamConsumerExtension
        {
            private readonly TaskCompletionSource<bool> releaseDelivery = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public TaskCompletionSource<bool> Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public List<StreamSequenceToken> DeliveredTokens { get; } = new();
            public List<Exception> Errors { get; } = new();

            public Task<StreamHandshakeToken?> DeliverImmutable(GuidId subscriptionId, QualifiedStreamId streamId, object item, StreamSequenceToken currentToken, StreamHandshakeToken? handshakeToken)
                => throw new NotSupportedException();

            public Task<StreamHandshakeToken?> DeliverMutable(GuidId subscriptionId, QualifiedStreamId streamId, object item, StreamSequenceToken currentToken, StreamHandshakeToken? handshakeToken)
                => throw new NotSupportedException();

            public async Task<StreamHandshakeToken?> DeliverBatch(GuidId subscriptionId, QualifiedStreamId streamId, IBatchContainer item, StreamHandshakeToken? handshakeToken)
            {
                DeliveredTokens.Add(item.SequenceToken);
                Delivered.TrySetResult(true);
                await releaseDelivery.Task;
                return null;
            }

            public Task CompleteStream(GuidId subscriptionId) => Task.CompletedTask;

            public Task ErrorInStream(GuidId subscriptionId, Exception exc)
            {
                Errors.Add(exc);
                return Task.CompletedTask;
            }

            public Task<StreamHandshakeToken?> GetSequenceToken(GuidId subscriptionId) => Task.FromResult(requestedToken);

            public void ReleaseDelivery() => releaseDelivery.TrySetResult(true);
        }

        private sealed class RenegotiatingEarliestConsumer : IStreamConsumerExtension
        {
            private readonly StreamHandshakeToken startPositionToken =
                StreamHandshakeToken.CreateStartPositionToken(StreamSubscriptionStartPosition.EarliestAvailable)!;

            public List<StreamSequenceToken> DeliveredTokens { get; } = new();
            public List<Exception> Errors { get; } = new();

            public Task<StreamHandshakeToken?> DeliverImmutable(GuidId subscriptionId, QualifiedStreamId streamId, object item, StreamSequenceToken currentToken, StreamHandshakeToken? handshakeToken)
                => throw new NotSupportedException();

            public Task<StreamHandshakeToken?> DeliverMutable(GuidId subscriptionId, QualifiedStreamId streamId, object item, StreamSequenceToken currentToken, StreamHandshakeToken? handshakeToken)
                => throw new NotSupportedException();

            public Task<StreamHandshakeToken?> DeliverBatch(GuidId subscriptionId, QualifiedStreamId streamId, IBatchContainer item, StreamHandshakeToken? handshakeToken)
            {
                if (handshakeToken is not StartPositionToken)
                {
                    return Task.FromResult<StreamHandshakeToken?>(startPositionToken);
                }

                DeliveredTokens.Add(item.SequenceToken);
                return Task.FromResult<StreamHandshakeToken?>(null);
            }

            public Task CompleteStream(GuidId subscriptionId) => Task.CompletedTask;

            public Task ErrorInStream(GuidId subscriptionId, Exception exc)
            {
                Errors.Add(exc);
                return Task.CompletedTask;
            }

            public Task<StreamHandshakeToken?> GetSequenceToken(GuidId subscriptionId)
                => Task.FromResult<StreamHandshakeToken?>(null);
        }

        private sealed class RenegotiatingStartTokenConsumer(StreamSequenceToken token) : IStreamConsumerExtension
        {
            private readonly StreamHandshakeToken startToken = StreamHandshakeToken.CreateStartToken(token)!;

            public List<StreamSequenceToken> DeliveredTokens { get; } = new();

            public Task<StreamHandshakeToken?> DeliverImmutable(GuidId subscriptionId, QualifiedStreamId streamId, object item, StreamSequenceToken currentToken, StreamHandshakeToken? handshakeToken)
                => throw new NotSupportedException();

            public Task<StreamHandshakeToken?> DeliverMutable(GuidId subscriptionId, QualifiedStreamId streamId, object item, StreamSequenceToken currentToken, StreamHandshakeToken? handshakeToken)
                => throw new NotSupportedException();

            public Task<StreamHandshakeToken?> DeliverBatch(GuidId subscriptionId, QualifiedStreamId streamId, IBatchContainer item, StreamHandshakeToken? handshakeToken)
            {
                if (handshakeToken is not StartToken)
                {
                    return Task.FromResult<StreamHandshakeToken?>(startToken);
                }

                DeliveredTokens.Add(item.SequenceToken);
                return Task.FromResult<StreamHandshakeToken?>(null);
            }

            public Task CompleteStream(GuidId subscriptionId) => Task.CompletedTask;

            public Task ErrorInStream(GuidId subscriptionId, Exception exc) => Task.CompletedTask;

            public Task<StreamHandshakeToken?> GetSequenceToken(GuidId subscriptionId)
                => Task.FromResult<StreamHandshakeToken?>(null);
        }

        private sealed class UnknownHandshakeToken : StreamHandshakeToken;

        private sealed class UnknownHandshakeConsumer(bool returnDuringInitialHandshake) : IStreamConsumerExtension
        {
            private readonly StreamHandshakeToken unknownToken = new UnknownHandshakeToken();

            public List<Exception> Errors { get; } = new();

            public Task<StreamHandshakeToken?> DeliverImmutable(GuidId subscriptionId, QualifiedStreamId streamId, object item, StreamSequenceToken currentToken, StreamHandshakeToken? handshakeToken)
                => throw new NotSupportedException();

            public Task<StreamHandshakeToken?> DeliverMutable(GuidId subscriptionId, QualifiedStreamId streamId, object item, StreamSequenceToken currentToken, StreamHandshakeToken? handshakeToken)
                => throw new NotSupportedException();

            public Task<StreamHandshakeToken?> DeliverBatch(GuidId subscriptionId, QualifiedStreamId streamId, IBatchContainer item, StreamHandshakeToken? handshakeToken)
                => Task.FromResult<StreamHandshakeToken?>(unknownToken);

            public Task CompleteStream(GuidId subscriptionId) => Task.CompletedTask;

            public Task ErrorInStream(GuidId subscriptionId, Exception exc)
            {
                Errors.Add(exc);
                return Task.CompletedTask;
            }

            public Task<StreamHandshakeToken?> GetSequenceToken(GuidId subscriptionId)
                => Task.FromResult<StreamHandshakeToken?>(returnDuringInitialHandshake ? unknownToken : null);
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task EarliestAvailableLateSubscriberReceivesRetainedMessage()
        {
            var streamId = StreamId.Create("namespace", Guid.NewGuid());
            var qualifiedStreamId = new QualifiedStreamId("provider", streamId);
            var retainedToken = new EventSequenceTokenV2(1);
            var queueCache = new PurgeablePooledQueueCache();
            queueCache.AddToCache([new TestBatchContainer(streamId, retainedToken)]);
            var queueAdapterCache = Substitute.For<IQueueAdapterCache>();
            queueAdapterCache.CreateQueueCache(Arg.Any<QueueId>()).Returns(queueCache);
            var agent = CreateAgent(pubSub: Substitute.For<IStreamPubSub>(), QueueId.GetQueueId("queue", 0u, 0u), queueAdapterCache: queueAdapterCache);
            var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);

            var firstConsumer = new RecordingConsumer(StreamHandshakeToken.CreateStartToken(retainedToken));
            firstConsumer.ReleaseDelivery();
            var firstData = CreateConsumerData(firstConsumer);
            Assert.True(await accessor.DoHandshakeWithConsumer(firstData, cacheToken: null));
            await accessor.RunConsumerCursor(firstData);
            Assert.Equal(retainedToken, Assert.Single(firstConsumer.DeliveredTokens));

            var lateConsumer = new RecordingConsumer(
                StreamHandshakeToken.CreateStartPositionToken(StreamSubscriptionStartPosition.EarliestAvailable));
            lateConsumer.ReleaseDelivery();
            var lateData = CreateConsumerData(lateConsumer);
            Assert.True(await accessor.DoHandshakeWithConsumer(lateData, cacheToken: null));
            await accessor.RunConsumerCursor(lateData);

            Assert.Equal(retainedToken, Assert.Single(lateConsumer.DeliveredTokens));

            StreamConsumerData CreateConsumerData(IStreamConsumerExtension consumer)
            {
                return new StreamConsumerData(
                    GuidId.GetGuidId(Guid.NewGuid()),
                    qualifiedStreamId,
                    consumer,
                    filterData: null);
            }
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task LatestLateSubscriberReceivesOnlyFutureMessage()
        {
            var streamId = StreamId.Create("namespace", Guid.NewGuid());
            var qualifiedStreamId = new QualifiedStreamId("provider", streamId);
            var queueCache = new PurgeablePooledQueueCache();
            queueCache.AddToCache([new TestBatchContainer(streamId, new EventSequenceTokenV2(1))]);
            var queueAdapterCache = Substitute.For<IQueueAdapterCache>();
            queueAdapterCache.CreateQueueCache(Arg.Any<QueueId>()).Returns(queueCache);
            var agent = CreateAgent(pubSub: Substitute.For<IStreamPubSub>(), QueueId.GetQueueId("queue", 0u, 0u), queueAdapterCache: queueAdapterCache);
            var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            var consumer = new RecordingConsumer();
            consumer.ReleaseDelivery();
            var consumerData = new StreamConsumerData(
                GuidId.GetGuidId(Guid.NewGuid()),
                qualifiedStreamId,
                consumer,
                filterData: null);

            Assert.True(await accessor.DoHandshakeWithConsumer(consumerData, cacheToken: null));
            await accessor.RunConsumerCursor(consumerData);
            Assert.Empty(consumer.DeliveredTokens);

            var futureToken = new EventSequenceTokenV2(2);
            queueCache.AddToCache([new TestBatchContainer(streamId, futureToken)]);
            consumerData.Cursor!.Refresh(futureToken);
            await accessor.RunConsumerCursor(consumerData);

            Assert.Equal(futureToken, Assert.Single(consumer.DeliveredTokens));
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task EarliestAvailableWaitsWhenTargetStreamIsNotCached()
        {
            var targetStreamId = StreamId.Create("namespace", Guid.NewGuid());
            var otherStreamId = StreamId.Create("namespace", Guid.NewGuid());
            var qualifiedStreamId = new QualifiedStreamId("provider", targetStreamId);
            var queueCache = new PurgeablePooledQueueCache();
            queueCache.AddToCache([new TestBatchContainer(otherStreamId, new EventSequenceTokenV2(1))]);
            var queueAdapterCache = Substitute.For<IQueueAdapterCache>();
            queueAdapterCache.CreateQueueCache(Arg.Any<QueueId>()).Returns(queueCache);
            var agent = CreateAgent(pubSub: Substitute.For<IStreamPubSub>(), QueueId.GetQueueId("queue", 0u, 0u), queueAdapterCache: queueAdapterCache);
            var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            var consumer = new RecordingConsumer(
                StreamHandshakeToken.CreateStartPositionToken(StreamSubscriptionStartPosition.EarliestAvailable));
            consumer.ReleaseDelivery();
            var consumerData = new StreamConsumerData(
                GuidId.GetGuidId(Guid.NewGuid()),
                qualifiedStreamId,
                consumer,
                filterData: null);

            Assert.True(await accessor.DoHandshakeWithConsumer(consumerData, cacheToken: null));
            await accessor.RunConsumerCursor(consumerData);
            Assert.Empty(consumer.DeliveredTokens);

            var futureToken = new EventSequenceTokenV2(2);
            queueCache.AddToCache([new TestBatchContainer(targetStreamId, futureToken)]);
            consumerData.Cursor!.Refresh(futureToken);
            await accessor.RunConsumerCursor(consumerData);

            Assert.Equal(futureToken, Assert.Single(consumer.DeliveredTokens));
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task EarliestAvailableIsPreservedDuringDeliveryHandshakeRenegotiation()
        {
            var streamId = StreamId.Create("namespace", Guid.NewGuid());
            var qualifiedStreamId = new QualifiedStreamId("provider", streamId);
            var retainedToken = new EventSequenceTokenV2(1);
            var queueCache = new PurgeablePooledQueueCache();
            queueCache.AddToCache([new TestBatchContainer(streamId, retainedToken)]);
            var queueAdapterCache = Substitute.For<IQueueAdapterCache>();
            queueAdapterCache.CreateQueueCache(Arg.Any<QueueId>()).Returns(queueCache);
            var agent = CreateAgent(pubSub: Substitute.For<IStreamPubSub>(), QueueId.GetQueueId("queue", 0u, 0u), queueAdapterCache: queueAdapterCache);
            var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            var consumer = new RenegotiatingEarliestConsumer();
            var consumerData = new StreamConsumerData(
                GuidId.GetGuidId(Guid.NewGuid()),
                qualifiedStreamId,
                consumer,
                filterData: null)
            {
                Cursor = queueCache.GetCacheCursor(streamId, retainedToken),
            };

            await accessor.RunConsumerCursor(consumerData);

            Assert.Equal(retainedToken, Assert.Single(consumer.DeliveredTokens));
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task StartTokenRemainsInclusiveDuringDeliveryHandshakeRenegotiation()
        {
            var streamId = StreamId.Create("namespace", Guid.NewGuid());
            var qualifiedStreamId = new QualifiedStreamId("provider", streamId);
            var startToken = new EventSequenceTokenV2(1);
            var queueCache = new PurgeablePooledQueueCache();
            queueCache.AddToCache([new TestBatchContainer(streamId, startToken)]);
            var queueAdapterCache = Substitute.For<IQueueAdapterCache>();
            queueAdapterCache.CreateQueueCache(Arg.Any<QueueId>()).Returns(queueCache);
            var agent = CreateAgent(pubSub: Substitute.For<IStreamPubSub>(), QueueId.GetQueueId("queue", 0u, 0u), queueAdapterCache: queueAdapterCache);
            var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            var consumer = new RenegotiatingStartTokenConsumer(startToken);
            var consumerData = new StreamConsumerData(
                GuidId.GetGuidId(Guid.NewGuid()),
                qualifiedStreamId,
                consumer,
                filterData: null)
            {
                Cursor = queueCache.GetCacheCursor(streamId, startToken),
            };

            await accessor.RunConsumerCursor(consumerData);

            Assert.Equal(startToken, Assert.Single(consumer.DeliveredTokens));
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task UnknownTokenFaultsInitialHandshake()
        {
            var (accessor, pubSub, streamData, consumerData, consumer) = await CreateUnknownTokenTest(returnDuringInitialHandshake: true);

            Assert.False(await accessor.DoHandshakeWithConsumer(consumerData, cacheToken: null));

            Assert.IsType<InvalidOperationException>(consumer.Errors[0]);
            Assert.IsType<FaultedSubscriptionException>(consumer.Errors[1]);
            Assert.Empty(streamData.AllConsumers());
            await pubSub.Received(1).FaultSubscription(consumerData.StreamId, consumerData.SubscriptionId);
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task UnknownTokenFaultsDeliveryHandshake()
        {
            var (accessor, pubSub, streamData, consumerData, consumer) = await CreateUnknownTokenTest(returnDuringInitialHandshake: false);

            await accessor.RunConsumerCursor(consumerData);

            Assert.IsType<InvalidOperationException>(consumer.Errors[0]);
            Assert.IsType<FaultedSubscriptionException>(consumer.Errors[1]);
            Assert.Empty(streamData.AllConsumers());
            await pubSub.Received(1).FaultSubscription(consumerData.StreamId, consumerData.SubscriptionId);
        }

        private static async Task<(
            PersistentStreamPullingAgent.ITestAccessor Accessor,
            IStreamPubSub PubSub,
            StreamConsumerCollection StreamData,
            StreamConsumerData ConsumerData,
            UnknownHandshakeConsumer Consumer)> CreateUnknownTokenTest(bool returnDuringInitialHandshake)
        {
            var streamId = StreamId.Create("namespace", Guid.NewGuid());
            var qualifiedStreamId = new QualifiedStreamId("provider", streamId);
            var token = new EventSequenceTokenV2(1);
            var queueCache = new PurgeablePooledQueueCache();
            queueCache.AddToCache([new TestBatchContainer(streamId, token)]);
            var queueAdapterCache = Substitute.For<IQueueAdapterCache>();
            queueAdapterCache.CreateQueueCache(Arg.Any<QueueId>()).Returns(queueCache);
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default)
                .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));
            var agent = CreateAgent(pubSub, QueueId.GetQueueId("queue", 0u, 0u), queueAdapterCache: queueAdapterCache);
            var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            await accessor.RegisterStream(qualifiedStreamId, token, DateTime.UtcNow);
            var streamData = (await accessor.GetPubSubCache()).Single().Value;
            var consumer = new UnknownHandshakeConsumer(returnDuringInitialHandshake);
            var consumerData = streamData.AddConsumer(
                GuidId.GetGuidId(SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid())),
                qualifiedStreamId,
                consumer,
                filterData: null,
                now: DateTime.UtcNow);
            consumerData.Cursor = queueCache.GetCacheCursor(streamId, token);
            return (accessor, pubSub, streamData, consumerData, consumer);
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task UnsupportedCacheFaultsDeliveryHandshakeRenegotiation()
        {
            var streamId = StreamId.Create("namespace", Guid.NewGuid());
            var qualifiedStreamId = new QualifiedStreamId("provider", streamId);
            var retainedToken = new EventSequenceTokenV2(1);
            var queueCache = new ScriptedQueueCache();
            queueCache.AddToCache([new TestBatchContainer(streamId, retainedToken)]);
            var queueAdapterCache = Substitute.For<IQueueAdapterCache>();
            queueAdapterCache.CreateQueueCache(Arg.Any<QueueId>()).Returns(queueCache);
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default)
                .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));
            var agent = CreateAgent(pubSub, QueueId.GetQueueId("queue", 0u, 0u), queueAdapterCache: queueAdapterCache);
            var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            await accessor.RegisterStream(qualifiedStreamId, retainedToken, DateTime.UtcNow);
            var streamData = (await accessor.GetPubSubCache()).Single().Value;
            var consumer = new RenegotiatingEarliestConsumer();
            var consumerData = streamData.AddConsumer(
                GuidId.GetGuidId(SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid())),
                qualifiedStreamId,
                consumer,
                filterData: null,
                now: DateTime.UtcNow);
            consumerData.Cursor = queueCache.GetCacheCursor(streamId, null);

            await accessor.RunConsumerCursor(consumerData);

            Assert.IsType<NotSupportedException>(consumer.Errors[0]);
            Assert.IsType<FaultedSubscriptionException>(consumer.Errors[1]);
            Assert.Empty(streamData.AllConsumers());
            await pubSub.Received(1).FaultSubscription(qualifiedStreamId, consumerData.SubscriptionId);
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task EarliestAvailableReportsUnsupportedCustomCache()
        {
            var streamId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            var queueCache = new RecordingQueueCache();
            var queueAdapterCache = Substitute.For<IQueueAdapterCache>();
            queueAdapterCache.CreateQueueCache(Arg.Any<QueueId>()).Returns(queueCache);
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default)
                .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));
            var agent = CreateAgent(pubSub, QueueId.GetQueueId("queue", 0u, 0u), queueAdapterCache: queueAdapterCache);
            var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            await accessor.RegisterStream(streamId, new EventSequenceTokenV2(1), DateTime.UtcNow);
            var consumer = new RecordingConsumer(
                StreamHandshakeToken.CreateStartPositionToken(StreamSubscriptionStartPosition.EarliestAvailable));
            var streamData = (await accessor.GetPubSubCache()).Single().Value;
            var consumerData = streamData.AddConsumer(
                GuidId.GetGuidId(SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid())),
                streamId,
                consumer,
                filterData: null,
                now: DateTime.UtcNow);

            Assert.False(await accessor.DoHandshakeWithConsumer(consumerData, cacheToken: null));

            var error = Assert.IsType<NotSupportedException>(consumer.Errors[0]);
            Assert.Contains(nameof(StreamSubscriptionStartPosition.EarliestAvailable), error.Message);
            Assert.IsType<FaultedSubscriptionException>(consumer.Errors[1]);
            Assert.Null(consumerData.Cursor);
            Assert.Empty(streamData.AllConsumers());
            await pubSub.Received(1).FaultSubscription(streamId, consumerData.SubscriptionId);
        }

        private sealed class RewindConsumer(StreamHandshakeToken rewindToken) : IStreamConsumerExtension
        {
            public TaskCompletionSource<bool> Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task<StreamHandshakeToken?> DeliverImmutable(GuidId subscriptionId, QualifiedStreamId streamId, object item, StreamSequenceToken currentToken, StreamHandshakeToken? handshakeToken)
            {
                throw new NotSupportedException();
            }

            public Task<StreamHandshakeToken?> DeliverMutable(GuidId subscriptionId, QualifiedStreamId streamId, object item, StreamSequenceToken currentToken, StreamHandshakeToken? handshakeToken)
            {
                throw new NotSupportedException();
            }

            public Task<StreamHandshakeToken?> DeliverBatch(GuidId subscriptionId, QualifiedStreamId streamId, IBatchContainer item, StreamHandshakeToken? handshakeToken)
            {
                Delivered.TrySetResult(true);
                return Task.FromResult<StreamHandshakeToken?>(rewindToken);
            }

            public Task CompleteStream(GuidId subscriptionId) => Task.CompletedTask;

            public Task ErrorInStream(GuidId subscriptionId, Exception exc) => Task.CompletedTask;

            public Task<StreamHandshakeToken?> GetSequenceToken(GuidId subscriptionId) => Task.FromResult<StreamHandshakeToken?>(rewindToken);
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task ReadFromQueue_RefreshesIdleCursorAfterItsTokenMetadataIsPurged()
        {
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default)
                .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));

            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var streamId = StreamId.Create("namespace", Guid.NewGuid());
            var qualifiedStreamId = new QualifiedStreamId("provider", streamId);
            var oldToken = new EventSequenceTokenV2(1);
            var newToken = new EventSequenceTokenV2(2);
            var queueCache = new PurgeablePooledQueueCache();
            queueCache.AddToCache([new TestBatchContainer(streamId, oldToken)]);
            var cursor = queueCache.GetCacheCursor(streamId, oldToken);
            Assert.True(cursor.MoveNext());
            Assert.False(cursor.MoveNext());

            var receiver = Substitute.For<IQueueAdapterReceiver>();
            receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IList<IBatchContainer>>([new TestBatchContainer(streamId, newToken)]));
            var queueAdapterCache = Substitute.For<IQueueAdapterCache>();
            queueAdapterCache.CreateQueueCache(Arg.Any<QueueId>()).Returns(queueCache);
            var agent = CreateAgent(pubSub, queueId, receiver, queueAdapterCache);
            var testAccessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            await testAccessor.RegisterStream(qualifiedStreamId, oldToken, DateTime.UtcNow);

            var streamData = (await testAccessor.GetPubSubCache()).Single().Value;
            var consumer = new RecordingConsumer();
            var consumerData = streamData.AddConsumer(
                GuidId.GetGuidId(Guid.NewGuid()),
                qualifiedStreamId,
                consumer,
                filterData: null,
                now: DateTime.UtcNow);
            consumerData.IsRegistered = true;
            consumerData.Cursor = cursor;
            queueCache.Purge();

            Assert.True(await testAccessor.ReadFromQueue(queueId, receiver, 1));
            await consumer.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            consumer.ReleaseDelivery();

            var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (consumerData.State != StreamConsumerDataState.Inactive && DateTime.UtcNow < timeout)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            Assert.Equal(StreamConsumerDataState.Inactive, consumerData.State);
            Assert.Equal(newToken, Assert.Single(consumer.DeliveredTokens));
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task Shutdown_UsesReturnedHandshakeTokenForDeliveryProgress()
        {
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default)
                .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));

            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var streamId = StreamId.Create("namespace", Guid.NewGuid());
            var qualifiedStreamId = new QualifiedStreamId("provider", streamId);
            var previousToken = new EventSequenceTokenV2(1);
            var attemptedToken = new EventSequenceTokenV2(2);
            var rewindToken = StreamHandshakeToken.CreateDeliveyToken(previousToken);
            Assert.NotNull(rewindToken);
            var consumer = new RewindConsumer(rewindToken);

            var receiver = Substitute.For<IQueueAdapterReceiver>();
            receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(
                    Task.FromResult<IList<IBatchContainer>>([new TestBatchContainer(streamId, attemptedToken)]),
                    Task.FromResult<IList<IBatchContainer>>(new List<IBatchContainer>()));
            receiver.Shutdown(Arg.Any<TimeSpan>()).Returns(Task.CompletedTask);

            var queueCache = new ScriptedQueueCache();
            var queueAdapterCache = Substitute.For<IQueueAdapterCache>();
            queueAdapterCache.CreateQueueCache(Arg.Any<QueueId>()).Returns(queueCache);

            var agent = CreateAgent(pubSub, queueId, receiver, queueAdapterCache);
            var testAccessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            await testAccessor.RegisterStream(qualifiedStreamId, previousToken, DateTime.UtcNow);

            var streamData = (await testAccessor.GetPubSubCache()).Single().Value;
            var consumerData = streamData.AddConsumer(
                GuidId.GetGuidId(Guid.NewGuid()),
                qualifiedStreamId,
                consumer,
                filterData: null,
                now: DateTime.UtcNow);
            consumerData.IsRegistered = true;
            consumerData.LastToken = rewindToken;
            consumerData.LastProcessedToken = previousToken;
            consumerData.Cursor = queueCache.GetCacheCursor(qualifiedStreamId, previousToken);

            queueCache.ClearDeliveryProgress();
            await testAccessor.ReadFromQueue(queueId, receiver, maxCacheAddCount: 1);
            await consumer.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            queueCache.ClearDeliveryProgress();
            await testAccessor.Shutdown();

            Assert.Equal(previousToken, Assert.Single(queueCache.DeliveryProgressTokens));
        }

        private static Task InitializeAgent(PersistentStreamPullingAgent agent) => agent.RunOrQueueTask(() => agent.Initialize());

        private static SchedulerInstruments CreateSchedulerInstruments()
        {
            var services = new ServiceCollection();
            services.AddMetrics();
            services.AddSingleton<OrleansInstruments>();
            services.AddSingleton<SchedulerInstruments>();
            return services.BuildServiceProvider().GetRequiredService<SchedulerInstruments>();
        }

        private static CatalogInstruments CreateCatalogInstruments()
        {
            var services = new ServiceCollection();
            services.AddMetrics();
            services.AddSingleton<OrleansInstruments>();
            services.AddSingleton<CatalogInstruments>();
            return services.BuildServiceProvider().GetRequiredService<CatalogInstruments>();
        }

        private static GrainInstruments CreateGrainInstruments()
        {
            var services = new ServiceCollection();
            services.AddMetrics();
            services.AddSingleton<OrleansInstruments>();
            services.AddSingleton<GrainInstruments>();
            return services.BuildServiceProvider().GetRequiredService<GrainInstruments>();
        }

        private static MessagingInstruments CreateMessagingInstruments()
        {
            var services = new ServiceCollection();
            services.AddMetrics();
            services.AddSingleton<OrleansInstruments>();
            services.AddSingleton<MessagingInstruments>();
            return services.BuildServiceProvider().GetRequiredService<MessagingInstruments>();
        }

        private static MessagingProcessingInstruments CreateMessagingProcessingInstruments()
        {
            var services = new ServiceCollection();
            services.AddMetrics();
            services.AddSingleton<OrleansInstruments>();
            services.AddSingleton<MessagingProcessingInstruments>();
            return services.BuildServiceProvider().GetRequiredService<MessagingProcessingInstruments>();
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task RegisterStream_KeepsCacheEntryWhenSubscriberHandshakeFails()
        {
            // A subscriber whose grain reference cannot be resolved (RuntimeClient is null in test setup)
            // simulates a handshake failure.  The stream entry must survive.
            var subscriptionId = GuidId.GetGuidId(Guid.NewGuid());
            var streamId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            var consumerGrainId = GrainId.Create("test", Guid.NewGuid().ToString());

            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default)
                .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(
                    new HashSet<PubSubSubscriptionState>
                    {
                        new PubSubSubscriptionState(subscriptionId, streamId, consumerGrainId),
                    }));

            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var agent = CreateAgent(pubSub, queueId);
            var testAccessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);

            // RegisterStream should complete without throwing even though the subscriber
            // handshake will fault (NullReferenceException from the null RuntimeClient).
            await testAccessor.RegisterStream(streamId, new EventSequenceTokenV2(1), DateTime.UtcNow);

            var cache = await testAccessor.GetPubSubCache();
            Assert.True(cache.ContainsKey(streamId), "Stream entry must remain in pubsub cache after a subscriber-handshake failure.");
            Assert.True(cache[streamId].StreamRegistered, "StreamRegistered must be true once producer registration succeeds.");
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task Shutdown_WaitsForInFlightPumpWork()
        {
            var queueReadStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var queueReadReleased = new TaskCompletionSource<IList<IBatchContainer>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(async _ =>
                {
                    queueReadStarted.TrySetResult(true);
                    return await queueReadReleased.Task;
                });
            receiver.Shutdown(Arg.Any<TimeSpan>()).Returns(Task.CompletedTask);

            var agent = CreateAgent(pubSub: null, queueId, receiver);
            var testAccessor = (PersistentStreamPullingAgent.ITestAccessor)agent;

            await InitializeAgent(agent);

            var pumpTask = testAccessor.RunQueuePump(queueId, CancellationToken.None);
            await queueReadStarted.Task;

            var shutdownTask = testAccessor.Shutdown();
            Assert.False(shutdownTask.IsCompleted);

            queueReadReleased.SetResult(new List<IBatchContainer>());

            await shutdownTask;
            await pumpTask;
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task Shutdown_IsIdempotent()
        {
            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            receiver.Shutdown(Arg.Any<TimeSpan>()).Returns(Task.CompletedTask);
            var agent = CreateAgent(pubSub: null, queueId, receiver);
            var testAccessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);

            await testAccessor.Shutdown();
            await testAccessor.Shutdown();

            await receiver.Received(1).Shutdown(Arg.Any<TimeSpan>());
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task RunQueuePump_ReadsAfterReinitialize()
        {
            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IList<IBatchContainer>>(new List<IBatchContainer>()));
            receiver.Shutdown(Arg.Any<TimeSpan>()).Returns(Task.CompletedTask);

            var agent = CreateAgent(pubSub: null, queueId, receiver);
            var testAccessor = (PersistentStreamPullingAgent.ITestAccessor)agent;

            await InitializeAgent(agent);
            await testAccessor.Shutdown();
            await InitializeAgent(agent);

            await testAccessor.RunQueuePump(queueId, CancellationToken.None);

            await receiver.Received(1).GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task Shutdown_PushesEarliestDeliveryProgressTokenToCache()
        {
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default)
                .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));

            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IList<IBatchContainer>>(new List<IBatchContainer>()));
            receiver.Shutdown(Arg.Any<TimeSpan>()).Returns(Task.CompletedTask);

            var queueCache = new RecordingQueueCache();
            var queueAdapterCache = Substitute.For<IQueueAdapterCache>();
            queueAdapterCache.CreateQueueCache(Arg.Any<QueueId>()).Returns(queueCache);

            var streamId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            var agent = CreateAgent(pubSub, queueId, receiver, queueAdapterCache);
            var testAccessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            await testAccessor.RegisterStream(streamId, new EventSequenceTokenV2(1), DateTime.UtcNow);

            var streamData = (await testAccessor.GetPubSubCache()).Single().Value;
            Assert.Null(streamData.RegistrationTask);
            queueCache.ClearDeliveryProgress();

            var newestConsumer = streamData.AddConsumer(
                GuidId.GetGuidId(Guid.NewGuid()),
                streamId,
                streamConsumer: null!,
                filterData: null,
                now: DateTime.UtcNow);
            newestConsumer.IsRegistered = true;
            newestConsumer.LastProcessedToken = new EventSequenceTokenV2(200);

            var earliestConsumer = streamData.AddConsumer(
                GuidId.GetGuidId(Guid.NewGuid()),
                streamId,
                streamConsumer: null!,
                filterData: null,
                now: DateTime.UtcNow);
            earliestConsumer.IsRegistered = true;
            earliestConsumer.LastProcessedToken = new EventSequenceTokenV2(95);

            await testAccessor.Shutdown();

            Assert.Equal(earliestConsumer.LastProcessedToken, Assert.Single(queueCache.DeliveryProgressTokens));
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task Shutdown_PushesEarliestDeliveryProgressUsingBaseTokenPosition()
        {
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default)
                .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));

            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IList<IBatchContainer>>(new List<IBatchContainer>()));
            receiver.Shutdown(Arg.Any<TimeSpan>()).Returns(Task.CompletedTask);

            var queueCache = new RecordingQueueCache();
            var queueAdapterCache = Substitute.For<IQueueAdapterCache>();
            queueAdapterCache.CreateQueueCache(Arg.Any<QueueId>()).Returns(queueCache);

            var streamId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            var agent = CreateAgent(pubSub, queueId, receiver, queueAdapterCache);
            var testAccessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            await testAccessor.RegisterStream(streamId, new EventSequenceTokenV2(1), DateTime.UtcNow);

            var streamData = (await testAccessor.GetPubSubCache()).Single().Value;
            Assert.Null(streamData.RegistrationTask);
            queueCache.ClearDeliveryProgress();

            var newestConsumer = streamData.AddConsumer(
                GuidId.GetGuidId(Guid.NewGuid()),
                streamId,
                streamConsumer: null!,
                filterData: null,
                now: DateTime.UtcNow);
            newestConsumer.IsRegistered = true;
            newestConsumer.LastProcessedToken = new EventSequenceToken(200);

            var earliestConsumer = streamData.AddConsumer(
                GuidId.GetGuidId(Guid.NewGuid()),
                streamId,
                streamConsumer: null!,
                filterData: null,
                now: DateTime.UtcNow);
            earliestConsumer.IsRegistered = true;
            earliestConsumer.LastProcessedToken = new EventSequenceTokenV2(95);

            await testAccessor.Shutdown();

            Assert.Equal(earliestConsumer.LastProcessedToken, Assert.Single(queueCache.DeliveryProgressTokens));
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task Shutdown_SkipsDeliveryProgressForPendingRegistrations()
        {
            var registration = new TaskCompletionSource<ISet<PubSubSubscriptionState>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default)
                .ReturnsForAnyArgs(_ => registration.Task);

            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var streamId = StreamId.Create("namespace", Guid.NewGuid());
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            var receiverShutdownStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(
                    Task.FromResult<IList<IBatchContainer>>([
                        new GeneratedBatchContainer(streamId, 1, new EventSequenceTokenV2(1)),
                    ]),
                    Task.FromResult<IList<IBatchContainer>>(new List<IBatchContainer>()));
            receiver.Shutdown(Arg.Any<TimeSpan>()).Returns(_ =>
            {
                receiverShutdownStarted.SetResult(true);
                return Task.CompletedTask;
            });

            var queueCache = new RecordingQueueCache();
            var queueAdapterCache = Substitute.For<IQueueAdapterCache>();
            queueAdapterCache.CreateQueueCache(Arg.Any<QueueId>()).Returns(queueCache);

            var agent = CreateAgent(pubSub, queueId, receiver, queueAdapterCache);
            var testAccessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);

            // First tick: pump reads messages and kicks off a cold stream registration.
            await testAccessor.RunQueuePump(queueId, CancellationToken.None);

            // Verify the cache has the pending stream registered.
            var cache = await testAccessor.GetPubSubCache();
            Assert.Single(cache);
            var (_, streamData) = cache.Single();
            Assert.NotNull(streamData.RegistrationTask);
            Assert.False(streamData.RegistrationTask.IsCompleted, "Registration should still be in progress");

            queueCache.ClearDeliveryProgress();
            var shutdownTask = testAccessor.Shutdown();
            await receiverShutdownStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.Empty(queueCache.DeliveryProgressTokens);
            Assert.Equal(0, queueCache.DeliveryProgressCallCount);

            // Complete registration so shutdown can proceed cleanly.
            registration.SetResult(new HashSet<PubSubSubscriptionState>());
            await shutdownTask;
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task Shutdown_SkipsDeliveryProgressForUnregisteredConsumer()
        {
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default)
                .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));

            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IList<IBatchContainer>>(new List<IBatchContainer>()));
            receiver.Shutdown(Arg.Any<TimeSpan>()).Returns(Task.CompletedTask);

            var queueCache = new RecordingQueueCache();
            var queueAdapterCache = Substitute.For<IQueueAdapterCache>();
            queueAdapterCache.CreateQueueCache(Arg.Any<QueueId>()).Returns(queueCache);

            var streamId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            var agent = CreateAgent(pubSub, queueId, receiver, queueAdapterCache);
            var testAccessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            await testAccessor.RegisterStream(streamId, new EventSequenceTokenV2(1), DateTime.UtcNow);

            var streamData = (await testAccessor.GetPubSubCache()).Single().Value;
            Assert.Null(streamData.RegistrationTask);
            queueCache.ClearDeliveryProgress();

            var registeredConsumer = streamData.AddConsumer(
                GuidId.GetGuidId(Guid.NewGuid()),
                streamId,
                streamConsumer: null!,
                filterData: null,
                now: DateTime.UtcNow);
            registeredConsumer.IsRegistered = true;
            registeredConsumer.LastProcessedToken = new EventSequenceTokenV2(200);

            var unregisteredConsumer = streamData.AddConsumer(
                GuidId.GetGuidId(Guid.NewGuid()),
                streamId,
                streamConsumer: null!,
                filterData: null,
                now: DateTime.UtcNow);
            unregisteredConsumer.PendingStartToken = new EventSequenceTokenV2(50);

            await testAccessor.Shutdown();

            Assert.Empty(queueCache.DeliveryProgressTokens);
            Assert.Equal(0, queueCache.DeliveryProgressCallCount);
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task Shutdown_PushesFinalDeliveryProgress()
        {
            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            receiver.Shutdown(Arg.Any<TimeSpan>()).Returns(Task.CompletedTask);

            var queueCache = new RecordingQueueCache();
            var queueAdapterCache = Substitute.For<IQueueAdapterCache>();
            queueAdapterCache.CreateQueueCache(Arg.Any<QueueId>()).Returns(queueCache);

            var agent = CreateAgent(pubSub: null, queueId, receiver, queueAdapterCache);
            var testAccessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);

            await testAccessor.Shutdown();

            // Shutdown should push a final delivery progress snapshot before tearing down.
            Assert.Single(queueCache.DeliveryProgressTokens);
        }
    }
}
