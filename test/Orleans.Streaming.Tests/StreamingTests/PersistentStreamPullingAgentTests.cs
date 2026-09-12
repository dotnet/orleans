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

#pragma warning disable CS0618 // Test doubles and compatibility scenarios exercise legacy cursor APIs.

namespace UnitTests.StreamingTests
{
    public class PersistentStreamPullingAgentTests
    {
        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public void InitialDeliveryProgressIncludesOnlyAcknowledgedDeliveryToken()
        {
            var token = new EventSequenceTokenV2(1);
            var currentProgress = new EventSequenceTokenV2(2);

            Assert.Null(PersistentStreamPullingAgent.GetInitialDeliveryProgress(null));
            Assert.Equal(currentProgress, PersistentStreamPullingAgent.GetInitialDeliveryProgress(null, currentProgress));
            Assert.Null(PersistentStreamPullingAgent.GetInitialDeliveryProgress(StreamHandshakeToken.CreateStartToken(token)));
            Assert.Equal(
                currentProgress,
                PersistentStreamPullingAgent.GetInitialDeliveryProgress(
                    StreamHandshakeToken.CreateStartPositionToken(StreamSubscriptionStartPosition.EarliestAvailable),
                    currentProgress));
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
            pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken)
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
            pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken)
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
            await receiver.Received(1).GetQueueMessagesAsync(
                1,
                Arg.Is<CancellationToken>(static token => !token.CanBeCanceled));
            Assert.Empty(await testAccessor.GetPubSubCache());
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task ReadFromQueue_CachesDequeuedMessagesBeforeObservingCancellation()
        {
            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var streamId = StreamId.Create("namespace", Guid.NewGuid());
            var qualifiedStreamId = new QualifiedStreamId("provider", streamId);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var registration = new TaskCompletionSource<ISet<PubSubSubscriptionState>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken)
                .ReturnsForAnyArgs(_ => registration.Task);
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    cancellation.Cancel();
                    return Task.FromResult<IList<IBatchContainer>>(
                    [
                        new GeneratedBatchContainer(streamId, 1, new EventSequenceTokenV2(1)),
                    ]);
                });
            var queueCache = Substitute.For<IQueueCache>();
            queueCache.TryGetCacheCursor(Arg.Any<StreamId>(), Arg.Any<StreamSequenceToken?>())
                .Returns(QueueCacheCursorResult<IQueueCacheCursor>.FromCursor(new EmptyQueueCacheCursor()));
            var queueAdapterCache = Substitute.For<IQueueAdapterCache>();
            queueAdapterCache.CreateQueueCache(queueId).Returns(queueCache);
            var agent = CreateAgent(pubSub, queueId, receiver, queueAdapterCache);
            var testAccessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);

            var readResult = await testAccessor.ReadFromQueueWithCancellation(queueId, receiver, 1, cancellation.Token);

            Assert.False(readResult);
            queueCache.Received(1).AddToCache(Arg.Is<IList<IBatchContainer>>(batches => batches.Count == 1));
            var pubSubCache = await testAccessor.GetPubSubCache();
            Assert.True(pubSubCache.TryGetValue(qualifiedStreamId, out var streamData));
            var registrationTask = streamData.RegistrationTask;
            Assert.NotNull(registrationTask);
            registration.SetResult(new HashSet<PubSubSubscriptionState>());
            await registrationTask;
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task RegisterStream_RetainsCacheEntryWhenProducerRegistrationTerminates()
        {
            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var streamId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            var agent = CreateAgent(pubSub: null, queueId);
            var testAccessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);

            await testAccessor.RegisterStream(streamId, new EventSequenceTokenV2(1), DateTime.UtcNow);

            var stream = Assert.Single(await testAccessor.GetPubSubCache());
            Assert.Equal(streamId, stream.Key);
            Assert.False(stream.Value.StreamRegistered);
            Assert.NotNull(stream.Value.RegistrationTask);
            Assert.True(stream.Value.RegistrationTask.IsCompleted);
            Assert.Equal(1, Assert.IsType<EventSequenceTokenV2>(stream.Value.RegistrationStartToken).SequenceNumber);
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
            pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken)
                .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));

            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IList<IBatchContainer>>(new List<IBatchContainer>()));

            var streamId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            using var diagnostics = StreamingDiagnosticObserver.Create(
                SiloAddress.New(IPAddress.Loopback, 11111, 1));
            var inactive = diagnostics.WaitForStreamInactiveAsync(streamId.StreamId, "provider", TestContext.Current.CancellationToken);

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
            pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken)
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
            StreamPullingAgentOptions? options = null,
            IStreamFilter? streamFilter = null,
            IBackoffProvider? deliveryBackoff = null,
            IStreamFailureHandler? failureHandler = null)
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
                streamFilter ?? new NoOpStreamFilter(),
                queueId,
                options ?? new StreamPullingAgentOptions(),
                queueAdapter,
                queueAdapterCache!,
                failureHandler ?? new NoOpStreamDeliveryFailureHandler(),
                deliveryBackoff ?? new FixedBackoff(TimeSpan.Zero),
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
                return new EmptyQueueCacheCursor();
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

        private sealed class InvalidPositionQueueCache : IQueueCache
        {
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
                => new EmptyQueueCacheCursor();

            public QueueCacheCursorResult<IQueueCacheCursor> TryGetCacheCursorAtPosition(
                StreamId streamId,
                StreamSubscriptionStartPosition startPosition)
                => default;

            public bool IsUnderPressure() => false;
        }

        private sealed class InvalidMoveQueueCache : IQueueCache
        {
            public InvalidMoveCursor Cursor { get; } = new();
            public int CursorAcquisitionCount { get; private set; }

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
                CursorAcquisitionCount++;
                return new EmptyQueueCacheCursor();
            }

            public QueueCacheCursorResult<IQueueCacheCursor> TryGetCacheCursor(
                StreamId streamId,
                StreamSequenceToken? token)
            {
                CursorAcquisitionCount++;
                return QueueCacheCursorResult<IQueueCacheCursor>.FromCursor(new EmptyQueueCacheCursor());
            }

            public bool IsUnderPressure() => false;

            public void ResetAcquisitionCount() => CursorAcquisitionCount = 0;
        }

        private sealed class InvalidMoveCursor : IQueueCacheCursor
        {
            public bool IsDisposed { get; private set; }

            public void Dispose() => IsDisposed = true;

            public IBatchContainer? GetCurrent(out Exception? exception)
            {
                exception = null;
                return null;
            }

            public bool MoveNext() => false;

            public QueueCacheCursorMoveResult MoveNextWithResult() => default;

            public void Refresh(StreamSequenceToken token)
            {
            }

            public void RecordDeliveryFailure()
            {
            }
        }

        private sealed class EmptyQueueCacheCursor : IQueueCacheCursor
        {
            public void Dispose()
            {
            }

            public IBatchContainer? GetCurrent(out Exception? exception)
            {
                exception = null;
                return null;
            }

            public bool MoveNext() => false;

            public QueueCacheCursorMoveResult MoveNextWithResult() => QueueCacheCursorMoveResult.NoData;

            public void Refresh(StreamSequenceToken token)
            {
            }

            public void RecordDeliveryFailure()
            {
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

        private sealed class CacheMissQueueCursor : IQueueCacheCursor
        {
            public void Dispose()
            {
            }

            public IBatchContainer GetCurrent(out Exception exception)
            {
                exception = null!;
                return null!;
            }

            public bool MoveNext() => throw new QueueCacheMissException("The cache entry was purged.");

            public QueueCacheCursorMoveResult MoveNextWithResult()
                => QueueCacheCursorMoveResult.FromCacheMiss(new("requested", "low", "high"));

            public void Refresh(StreamSequenceToken token)
            {
            }

            public void RecordDeliveryFailure()
            {
            }
        }

        private class RecoverableCacheMissQueueCache : IQueueCache
        {
            private readonly QueueCacheMissException cacheMissException;
            private readonly IReadOnlyList<IBatchContainer> retainedBatches;
            private TaskCompletionSource<StreamSequenceToken?> cursorRequested = CreateCursorRequestedSource();
            private TaskCompletionSource<StreamSubscriptionStartPosition> startPositionRequested = CreateStartPositionRequestedSource();
            private LegacyBatchCursor? tokenCursor;
            private LegacyBatchCursor? earliestCursor;
            private CacheMissCursor? cacheMissCursor;

            public RecoverableCacheMissQueueCache(
                QueueCacheMissException cacheMissException,
                IReadOnlyList<IBatchContainer>? retainedBatches = null)
            {
                this.cacheMissException = cacheMissException;
                this.retainedBatches = retainedBatches ?? [];
            }

            public static RecoverableCacheMissQueueCache Create(
                QueueCacheMissException cacheMissException,
                bool supportsEarliestAvailable,
                IReadOnlyList<IBatchContainer>? retainedBatches = null)
                => supportsEarliestAvailable
                    ? new EarliestAvailableRecoveryQueueCache(cacheMissException, retainedBatches)
                    : new RecoverableCacheMissQueueCache(cacheMissException, retainedBatches);

            public Task<StreamSequenceToken?> CursorRequested => cursorRequested.Task;
            public Task<StreamSubscriptionStartPosition> StartPositionRequested => startPositionRequested.Task;
            public List<(
                StreamId StreamId,
                StreamSequenceToken? Token,
                StreamSubscriptionStartPosition? Position)> Requests
            { get; } = [];
            public IQueueCacheCursor? OldCursor => cacheMissCursor;
            public IQueueCacheCursor? ReplacementCursor => earliestCursor ?? tokenCursor;
            public int OldCursorMoveNextCount => cacheMissCursor?.MoveNextCount ?? 0;
            public int OldCursorDisposeCount => cacheMissCursor?.DisposeCount ?? 0;
            public int ReplacementMoveNextCount => (earliestCursor ?? tokenCursor)?.MoveNextCount ?? 0;
            public int ReplacementGetCurrentCount => (earliestCursor ?? tokenCursor)?.GetCurrentCount ?? 0;
            public int ReplacementDisposeCount => (earliestCursor ?? tokenCursor)?.DisposeCount ?? 0;

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
                Requests.Add((streamId, token, null));
                cursorRequested.TrySetResult(token);
                return tokenCursor = new LegacyBatchCursor(retainedBatches);
            }

            protected IQueueCacheCursor GetEarliestCursor(StreamId streamId)
            {
                Requests.Add((streamId, null, StreamSubscriptionStartPosition.EarliestAvailable));
                startPositionRequested.TrySetResult(StreamSubscriptionStartPosition.EarliestAvailable);
                return earliestCursor = new LegacyBatchCursor(retainedBatches);
            }

            private sealed class EarliestAvailableRecoveryQueueCache(
                QueueCacheMissException cacheMissException,
                IReadOnlyList<IBatchContainer>? retainedBatches)
                : RecoverableCacheMissQueueCache(cacheMissException, retainedBatches), IQueueCache
            {
                QueueCacheCursorResult<IQueueCacheCursor> IQueueCache.TryGetCacheCursorAtPosition(
                    StreamId streamId,
                    StreamSubscriptionStartPosition startPosition)
                    => startPosition switch
                    {
                        StreamSubscriptionStartPosition.EarliestAvailable
                            => QueueCacheCursorResult<IQueueCacheCursor>.FromCursor(GetEarliestCursor(streamId)),
                        StreamSubscriptionStartPosition.Latest => ((IQueueCache)this).TryGetCacheCursor(streamId, null),
                        _ => throw new ArgumentOutOfRangeException(nameof(startPosition)),
                    };
            }

            public bool IsUnderPressure() => false;

            public IQueueCacheCursor CreateCacheMissCursor()
                => cacheMissCursor = new CacheMissCursor(cacheMissException);

            public void ResetRequests()
            {
                Requests.Clear();
                cursorRequested = CreateCursorRequestedSource();
                startPositionRequested = CreateStartPositionRequestedSource();
            }

            private static TaskCompletionSource<StreamSequenceToken?> CreateCursorRequestedSource() =>
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            private static TaskCompletionSource<StreamSubscriptionStartPosition> CreateStartPositionRequestedSource() =>
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            private sealed class LegacyBatchCursor(IReadOnlyList<IBatchContainer> batches) : IQueueCacheCursor
            {
                private int index = -1;

                public int MoveNextCount { get; private set; }
                public int GetCurrentCount { get; private set; }
                public int DisposeCount { get; private set; }

                public void Dispose() => DisposeCount++;

                public IBatchContainer? GetCurrent(out Exception? exception)
                {
                    GetCurrentCount++;
                    exception = null;
                    return index >= 0 && index < batches.Count
                        ? batches[index]
                        : throw new InvalidOperationException("The cursor does not have a current item.");
                }

                public bool MoveNext()
                {
                    MoveNextCount++;
                    if (DisposeCount != 0)
                    {
                        throw new ObjectDisposedException(nameof(LegacyBatchCursor));
                    }

                    if (index + 1 >= batches.Count)
                    {
                        index = batches.Count;
                        return false;
                    }

                    index++;
                    return true;
                }

                public void Refresh(StreamSequenceToken token)
                {
                }

                public void RecordDeliveryFailure()
                {
                }
            }

            private sealed class CacheMissCursor(QueueCacheMissException cacheMissException) : IQueueCacheCursor
            {
                public int MoveNextCount { get; private set; }
                public int DisposeCount { get; private set; }

                public void Dispose() => DisposeCount++;

                public IBatchContainer GetCurrent(out Exception exception) => throw new InvalidOperationException();

                public bool MoveNext()
                {
                    MoveNextCount++;
                    throw cacheMissException;
                }

                public void Refresh(StreamSequenceToken token)
                {
                }

                public void RecordDeliveryFailure()
                {
                }
            }
        }

        private sealed class PurgeablePooledQueueCache : IQueueCache
        {
            private readonly PooledQueueCache cache;

            public List<StreamSequenceToken?> DeliveryProgressTokens { get; } = [];

            public PurgeablePooledQueueCache(bool retainPurgeMetadata = false)
            {
                cache = new(
                    new CacheDataAdapter(),
                    NullLogger.Instance,
                    null,
                    null,
                    retainPurgeMetadata ? TimeSpan.FromMinutes(1) : null);
            }

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

            public QueueCacheCursorResult<IQueueCacheCursor> TryGetCacheCursor(
                StreamId streamId,
                StreamSequenceToken? token)
            {
                return WrapCursorResult(cache.TryGetCursor(streamId, token));
            }

            public QueueCacheCursorResult<IQueueCacheCursor> TryGetCacheCursorAtPosition(
                StreamId streamId,
                StreamSubscriptionStartPosition startPosition)
            {
                return WrapCursorResult(cache.TryGetCursorAtPosition(streamId, startPosition));
            }

            private QueueCacheCursorResult<IQueueCacheCursor> WrapCursorResult(QueueCacheCursorResult<object> result)
                => result.Kind switch
                {
                    QueueCacheCursorResultKind.Success => QueueCacheCursorResult<IQueueCacheCursor>.FromCursor(new Cursor(cache, result.Cursor!)),
                    QueueCacheCursorResultKind.CacheMiss => QueueCacheCursorResult<IQueueCacheCursor>.FromCacheMiss(result.CacheMiss!.Value),
                    QueueCacheCursorResultKind.NotSupported => QueueCacheCursorResult<IQueueCacheCursor>.NotSupported,
                    _ => throw new InvalidOperationException("The cursor result is not initialized."),
                };

            public bool IsUnderPressure() => false;

            public void UpdateDeliveryProgress(StreamSequenceToken? earliestSubscriptionToken, DateTime utcNow)
            {
                DeliveryProgressTokens.Add(earliestSubscriptionToken);
            }

            public void Purge()
            {
                while (!cache.IsEmpty)
                {
                    cache.RemoveOldestMessage();
                }
            }

            public void PurgeOldest() => cache.RemoveOldestMessage();

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

                public QueueCacheCursorMoveResult MoveNextWithResult()
                    => cache.TryGetNextMessageWithResult(cursor, out current);

                public void Refresh(StreamSequenceToken token) => cache.Refresh(cursor, token);

                public void RecordDeliveryFailure()
                {
                }
            }
        }

        private sealed class FixedQueueCache(IReadOnlyList<IBatchContainer> messages) : IQueueCache
        {
            public int GetMaxAddCount() => 1000;

            public void AddToCache(IList<IBatchContainer> messages) => throw new NotSupportedException();

            public bool TryPurgeFromCache(out IList<IBatchContainer> purgedItems)
            {
                purgedItems = [];
                return false;
            }

            public IQueueCacheCursor GetCacheCursor(StreamId streamId, StreamSequenceToken? token)
                => new Cursor(messages);

            public IQueueCacheCursor GetCacheCursorAtPosition(StreamId streamId, StreamSubscriptionStartPosition startPosition)
                => throw new NotSupportedException();

            public bool IsUnderPressure() => false;

            private sealed class Cursor(IReadOnlyList<IBatchContainer> messages) : IQueueCacheCursor
            {
                private int index = -1;

                public void Dispose()
                {
                }

                public IBatchContainer? GetCurrent(out Exception? exception)
                {
                    exception = null;
                    return messages[index];
                }

                public bool MoveNext() => ++index < messages.Count;

                public void Refresh(StreamSequenceToken token)
                {
                }

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

        private sealed class DerivedEventSequenceTokenV2(long sequenceNumber, int eventIndex)
            : EventSequenceTokenV2(sequenceNumber, eventIndex)
        {
            protected override Type SequenceTokenCompatibilityDomain => typeof(EventSequenceToken);
        }

        private sealed class RecordingConsumer(StreamHandshakeToken? requestedToken = null) : IStreamConsumerExtension
        {
            private readonly TaskCompletionSource<bool> releaseDelivery = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public TaskCompletionSource<bool> Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public List<IBatchContainer> DeliveredBatches { get; } = new();
            public List<StreamSequenceToken> DeliveredTokens { get; } = new();
            public List<StreamHandshakeToken?> DeliveredHandshakeTokens { get; } = new();
            public List<Exception> Errors { get; } = new();
            public Queue<StreamHandshakeToken?> DeliveryResponses { get; } = new();
            public StreamHandshakeToken? HandshakeResponse { get; set; } = requestedToken;
            public Exception? DeliveryException { get; set; }
            public Action? OnDelivery { get; set; }
            public Action? OnHandshake { get; set; }
            public Task<StreamHandshakeToken?>? HandshakeTask { get; set; }
            public int HandshakeCount { get; private set; }

            public Task<StreamHandshakeToken?> DeliverImmutable(GuidId subscriptionId, QualifiedStreamId streamId, object item, StreamSequenceToken currentToken, StreamHandshakeToken? handshakeToken, CancellationToken cancellationToken)
                => throw new NotSupportedException();

            public Task<StreamHandshakeToken?> DeliverMutable(GuidId subscriptionId, QualifiedStreamId streamId, object item, StreamSequenceToken currentToken, StreamHandshakeToken? handshakeToken, CancellationToken cancellationToken)
                => throw new NotSupportedException();

            public async Task<StreamHandshakeToken?> DeliverBatch(GuidId subscriptionId, QualifiedStreamId streamId, IBatchContainer item, StreamHandshakeToken? handshakeToken, CancellationToken cancellationToken)
            {
                DeliveredBatches.Add(item);
                DeliveredTokens.Add(item.SequenceToken);
                DeliveredHandshakeTokens.Add(handshakeToken);
                Delivered.TrySetResult(true);
                await releaseDelivery.Task;
                OnDelivery?.Invoke();
                if (DeliveryException is { } exception)
                {
                    throw exception;
                }

                return DeliveryResponses.TryDequeue(out var response) ? response : null;
            }

            public Task CompleteStream(GuidId subscriptionId, CancellationToken cancellationToken) => Task.CompletedTask;

            public Task ErrorInStream(GuidId subscriptionId, Exception exc, CancellationToken cancellationToken)
            {
                Errors.Add(exc);
                return Task.CompletedTask;
            }

            public Task<StreamHandshakeToken?> GetSequenceToken(GuidId subscriptionId, CancellationToken cancellationToken)
            {
                HandshakeCount++;
                OnHandshake?.Invoke();
                return HandshakeTask ?? Task.FromResult(HandshakeResponse);
            }

            public void ReleaseDelivery() => releaseDelivery.TrySetResult(true);
        }

        private sealed class ImmediateRecordingConsumer : IStreamConsumerExtension
        {
            public List<IBatchContainer> DeliveredBatches { get; } = [];
            public List<StreamSequenceToken> DeliveredTokens { get; } = [];
            public List<Exception> Errors { get; } = [];

            public Task<StreamHandshakeToken?> DeliverImmutable(GuidId subscriptionId, QualifiedStreamId streamId, object item, StreamSequenceToken currentToken, StreamHandshakeToken? handshakeToken, CancellationToken cancellationToken)
                => throw new NotSupportedException();

            public Task<StreamHandshakeToken?> DeliverMutable(GuidId subscriptionId, QualifiedStreamId streamId, object item, StreamSequenceToken currentToken, StreamHandshakeToken? handshakeToken, CancellationToken cancellationToken)
                => throw new NotSupportedException();

            public Task<StreamHandshakeToken?> DeliverBatch(GuidId subscriptionId, QualifiedStreamId streamId, IBatchContainer item, StreamHandshakeToken? handshakeToken, CancellationToken cancellationToken)
            {
                DeliveredBatches.Add(item);
                DeliveredTokens.Add(item.SequenceToken);
                return Task.FromResult<StreamHandshakeToken?>(null);
            }

            public Task CompleteStream(GuidId subscriptionId, CancellationToken cancellationToken) => Task.CompletedTask;

            public Task ErrorInStream(GuidId subscriptionId, Exception exc, CancellationToken cancellationToken)
            {
                Errors.Add(exc);
                return Task.CompletedTask;
            }

            public Task<StreamHandshakeToken?> GetSequenceToken(GuidId subscriptionId, CancellationToken cancellationToken)
                => Task.FromResult<StreamHandshakeToken?>(null);
        }

        private sealed class RenegotiatingEarliestConsumer : IStreamConsumerExtension
        {
            private readonly StreamHandshakeToken startPositionToken =
                StreamHandshakeToken.CreateStartPositionToken(StreamSubscriptionStartPosition.EarliestAvailable)!;

            public List<StreamSequenceToken> DeliveredTokens { get; } = new();
            public List<Exception> Errors { get; } = new();

            public Task<StreamHandshakeToken?> DeliverImmutable(GuidId subscriptionId, QualifiedStreamId streamId, object item, StreamSequenceToken currentToken, StreamHandshakeToken? handshakeToken, CancellationToken cancellationToken)
                => throw new NotSupportedException();

            public Task<StreamHandshakeToken?> DeliverMutable(GuidId subscriptionId, QualifiedStreamId streamId, object item, StreamSequenceToken currentToken, StreamHandshakeToken? handshakeToken, CancellationToken cancellationToken)
                => throw new NotSupportedException();

            public Task<StreamHandshakeToken?> DeliverBatch(GuidId subscriptionId, QualifiedStreamId streamId, IBatchContainer item, StreamHandshakeToken? handshakeToken, CancellationToken cancellationToken)
            {
                if (handshakeToken is not StartPositionToken)
                {
                    return Task.FromResult<StreamHandshakeToken?>(startPositionToken);
                }

                DeliveredTokens.Add(item.SequenceToken);
                return Task.FromResult<StreamHandshakeToken?>(null);
            }

            public Task CompleteStream(GuidId subscriptionId, CancellationToken cancellationToken) => Task.CompletedTask;

            public Task ErrorInStream(GuidId subscriptionId, Exception exc, CancellationToken cancellationToken)
            {
                Errors.Add(exc);
                return Task.CompletedTask;
            }

            public Task<StreamHandshakeToken?> GetSequenceToken(GuidId subscriptionId, CancellationToken cancellationToken)
                => Task.FromResult<StreamHandshakeToken?>(null);
        }

        private sealed class RenegotiatingStartTokenConsumer(StreamSequenceToken token) : IStreamConsumerExtension
        {
            private readonly StreamHandshakeToken startToken = StreamHandshakeToken.CreateStartToken(token)!;

            public List<StreamSequenceToken> DeliveredTokens { get; } = new();

            public Task<StreamHandshakeToken?> DeliverImmutable(GuidId subscriptionId, QualifiedStreamId streamId, object item, StreamSequenceToken currentToken, StreamHandshakeToken? handshakeToken, CancellationToken cancellationToken)
                => throw new NotSupportedException();

            public Task<StreamHandshakeToken?> DeliverMutable(GuidId subscriptionId, QualifiedStreamId streamId, object item, StreamSequenceToken currentToken, StreamHandshakeToken? handshakeToken, CancellationToken cancellationToken)
                => throw new NotSupportedException();

            public Task<StreamHandshakeToken?> DeliverBatch(GuidId subscriptionId, QualifiedStreamId streamId, IBatchContainer item, StreamHandshakeToken? handshakeToken, CancellationToken cancellationToken)
            {
                if (handshakeToken is not StartToken)
                {
                    return Task.FromResult<StreamHandshakeToken?>(startToken);
                }

                DeliveredTokens.Add(item.SequenceToken);
                return Task.FromResult<StreamHandshakeToken?>(null);
            }

            public Task CompleteStream(GuidId subscriptionId, CancellationToken cancellationToken) => Task.CompletedTask;

            public Task ErrorInStream(GuidId subscriptionId, Exception exc, CancellationToken cancellationToken) => Task.CompletedTask;

            public Task<StreamHandshakeToken?> GetSequenceToken(GuidId subscriptionId, CancellationToken cancellationToken)
                => Task.FromResult<StreamHandshakeToken?>(null);
        }

        private sealed class RenegotiatingDeliveryTokenConsumer(StreamSequenceToken token) : IStreamConsumerExtension
        {
            private readonly StreamHandshakeToken deliveryToken = StreamHandshakeToken.CreateDeliveyToken(token)!;

            public List<StreamSequenceToken> DeliveredTokens { get; } = new();

            public Task<StreamHandshakeToken?> DeliverImmutable(GuidId subscriptionId, QualifiedStreamId streamId, object item, StreamSequenceToken currentToken, StreamHandshakeToken? handshakeToken, CancellationToken cancellationToken)
                => throw new NotSupportedException();

            public Task<StreamHandshakeToken?> DeliverMutable(GuidId subscriptionId, QualifiedStreamId streamId, object item, StreamSequenceToken currentToken, StreamHandshakeToken? handshakeToken, CancellationToken cancellationToken)
                => throw new NotSupportedException();

            public Task<StreamHandshakeToken?> DeliverBatch(GuidId subscriptionId, QualifiedStreamId streamId, IBatchContainer item, StreamHandshakeToken? handshakeToken, CancellationToken cancellationToken)
            {
                if (handshakeToken is not DeliveryToken)
                {
                    return Task.FromResult<StreamHandshakeToken?>(deliveryToken);
                }

                DeliveredTokens.Add(item.SequenceToken);
                return Task.FromResult<StreamHandshakeToken?>(null);
            }

            public Task CompleteStream(GuidId subscriptionId, CancellationToken cancellationToken) => Task.CompletedTask;

            public Task ErrorInStream(GuidId subscriptionId, Exception exc, CancellationToken cancellationToken) => Task.CompletedTask;

            public Task<StreamHandshakeToken?> GetSequenceToken(GuidId subscriptionId, CancellationToken cancellationToken)
                => Task.FromResult<StreamHandshakeToken?>(null);
        }

        private sealed class UnknownHandshakeToken : StreamHandshakeToken;

        private sealed class UnknownHandshakeConsumer(bool returnDuringInitialHandshake) : IStreamConsumerExtension
        {
            private readonly StreamHandshakeToken unknownToken = new UnknownHandshakeToken();

            public List<Exception> Errors { get; } = new();

            public Task<StreamHandshakeToken?> DeliverImmutable(GuidId subscriptionId, QualifiedStreamId streamId, object item, StreamSequenceToken currentToken, StreamHandshakeToken? handshakeToken, CancellationToken cancellationToken)
                => throw new NotSupportedException();

            public Task<StreamHandshakeToken?> DeliverMutable(GuidId subscriptionId, QualifiedStreamId streamId, object item, StreamSequenceToken currentToken, StreamHandshakeToken? handshakeToken, CancellationToken cancellationToken)
                => throw new NotSupportedException();

            public Task<StreamHandshakeToken?> DeliverBatch(GuidId subscriptionId, QualifiedStreamId streamId, IBatchContainer item, StreamHandshakeToken? handshakeToken, CancellationToken cancellationToken)
                => Task.FromResult<StreamHandshakeToken?>(unknownToken);

            public Task CompleteStream(GuidId subscriptionId, CancellationToken cancellationToken) => Task.CompletedTask;

            public Task ErrorInStream(GuidId subscriptionId, Exception exc, CancellationToken cancellationToken)
            {
                Errors.Add(exc);
                return Task.CompletedTask;
            }

            public Task<StreamHandshakeToken?> GetSequenceToken(GuidId subscriptionId, CancellationToken cancellationToken)
                => Task.FromResult<StreamHandshakeToken?>(returnDuringInitialHandshake ? unknownToken : null);
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task ExplicitEarliestAvailableOverridesProviderLatest()
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
                    GuidId.GetGuidId(SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid())),
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
                GuidId.GetGuidId(SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid())),
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
        public async Task ProviderDefaultLatestPreservesLegacyLivePosition()
        {
            var streamId = StreamId.Create("namespace", Guid.NewGuid());
            var qualifiedStreamId = new QualifiedStreamId("provider", streamId);
            var retainedToken = new EventSequenceTokenV2(1);
            var futureToken = new EventSequenceTokenV2(2);
            var queueCache = new PurgeablePooledQueueCache();
            queueCache.AddToCache([new TestBatchContainer(streamId, retainedToken)]);
            var (accessor, _, streamData) = await CreateInitializedAgentWithStream(
                qualifiedStreamId,
                retainedToken,
                queueCache,
                new StreamPullingAgentOptions());
            var consumerData = streamData.AddConsumer(
                GuidId.GetGuidId(SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid())),
                qualifiedStreamId,
                new RecordingConsumer(),
                filterData: null,
                now: DateTime.UtcNow);

            Assert.True(await accessor.DoHandshakeWithConsumer(consumerData, cacheToken: null));
            var cursor = Assert.IsAssignableFrom<IQueueCacheCursor>(consumerData.Cursor);
            Assert.False(cursor.MoveNext());

            queueCache.AddToCache([new TestBatchContainer(streamId, futureToken)]);
            cursor.Refresh(futureToken);

            Assert.True(cursor.MoveNext());
            Assert.Equal(futureToken, Assert.IsType<TestBatchContainer>(cursor.GetCurrent(out _)).SequenceToken);
            await accessor.Shutdown();
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task LegacyAcknowledgedTokenSkipsDerivedDuplicateAndDeliversPrefetchedNewerBatch()
        {
            var streamId = StreamId.Create("namespace", Guid.NewGuid());
            var qualifiedStreamId = new QualifiedStreamId("provider", streamId);
            var acknowledgedToken = new EventSequenceTokenV2(1);
            var duplicateToken = new DerivedEventSequenceTokenV2(1, 0);
            var nextToken = new DerivedEventSequenceTokenV2(2, 0);
            var queueCache = new FixedQueueCache(
            [
                new TestBatchContainer(streamId, duplicateToken),
                new TestBatchContainer(streamId, nextToken),
            ]);
            var queueAdapterCache = Substitute.For<IQueueAdapterCache>();
            queueAdapterCache.CreateQueueCache(Arg.Any<QueueId>()).Returns(queueCache);
            var agent = CreateAgent(
                pubSub: Substitute.For<IStreamPubSub>(),
                QueueId.GetQueueId("queue", 0u, 0u),
                queueAdapterCache: queueAdapterCache);
            var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            var consumer = new RecordingConsumer(StreamHandshakeToken.CreateDeliveyToken(acknowledgedToken));
            var consumerData = new StreamConsumerData(
                GuidId.GetGuidId(SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid())),
                qualifiedStreamId,
                consumer,
                filterData: null);

            Assert.True(await accessor.DoHandshakeWithConsumer(consumerData, cacheToken: null));
            Assert.IsType<DeliveryToken>(consumerData.LastToken);
            Assert.NotNull(consumerData.Cursor);

            var deliveryTask = accessor.RunConsumerCursor(consumerData);
            await consumer.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(nextToken, Assert.Single(consumer.DeliveredTokens));
            Assert.Empty(consumer.Errors);

            consumer.ReleaseDelivery();
            await deliveryTask;
            await accessor.Shutdown();
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task ProviderEarliestReplaysRetainedMessageForLegacyTokenlessSubscription()
        {
            var streamId = StreamId.Create("namespace", Guid.NewGuid());
            var qualifiedStreamId = new QualifiedStreamId("provider", streamId);
            var retainedToken = new EventSequenceTokenV2(1);
            var queueCache = new PurgeablePooledQueueCache();
            queueCache.AddToCache([new TestBatchContainer(streamId, retainedToken)]);
            var (accessor, _, streamData) = await CreateInitializedAgentWithStream(
                qualifiedStreamId,
                retainedToken,
                queueCache,
                new StreamPullingAgentOptions
                {
                    InitialSubscriptionStartPosition = StreamSubscriptionStartPosition.EarliestAvailable,
                });
            var consumer = new RecordingConsumer();
            consumer.ReleaseDelivery();
            var consumerData = streamData.AddConsumer(
                GuidId.GetGuidId(SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid())),
                qualifiedStreamId,
                consumer,
                filterData: null,
                now: DateTime.UtcNow);

            Assert.True(await accessor.DoHandshakeWithConsumer(consumerData, cacheToken: null));
            var cursor = Assert.IsAssignableFrom<IQueueCacheCursor>(consumerData.Cursor);
            var startPositionToken = Assert.IsType<StartPositionToken>(consumerData.LastToken);
            Assert.Equal(StreamSubscriptionStartPosition.EarliestAvailable, startPositionToken.StartPosition);

            consumerData.IsRegistered = true;
            Assert.True(await accessor.DoHandshakeWithConsumer(consumerData, cacheToken: null));
            Assert.Same(cursor, consumerData.Cursor);

            await accessor.RunConsumerCursor(consumerData);

            Assert.Equal(retainedToken, Assert.Single(consumer.DeliveredTokens));
            Assert.Null(Assert.Single(consumer.DeliveredHandshakeTokens));
            await accessor.Shutdown();
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task ExplicitLatestOverridesProviderEarliest()
        {
            var streamId = StreamId.Create("namespace", Guid.NewGuid());
            var qualifiedStreamId = new QualifiedStreamId("provider", streamId);
            var retainedToken = new EventSequenceTokenV2(1);
            var futureToken = new EventSequenceTokenV2(2);
            var queueCache = new PurgeablePooledQueueCache();
            queueCache.AddToCache([new TestBatchContainer(streamId, retainedToken)]);
            var (accessor, _, streamData) = await CreateInitializedAgentWithStream(
                qualifiedStreamId,
                retainedToken,
                queueCache,
                new StreamPullingAgentOptions
                {
                    InitialSubscriptionStartPosition = StreamSubscriptionStartPosition.EarliestAvailable,
                });
            var consumerData = streamData.AddConsumer(
                GuidId.GetGuidId(SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid())),
                qualifiedStreamId,
                new RecordingConsumer(
                    StreamHandshakeToken.CreateStartPositionToken(StreamSubscriptionStartPosition.Latest)),
                filterData: null,
                now: DateTime.UtcNow);

            Assert.True(await accessor.DoHandshakeWithConsumer(consumerData, cacheToken: null));
            var startPositionToken = Assert.IsType<StartPositionToken>(consumerData.LastToken);
            Assert.Equal(StreamSubscriptionStartPosition.Latest, startPositionToken.StartPosition);
            Assert.False(Assert.IsAssignableFrom<IQueueCacheCursor>(consumerData.Cursor).MoveNext());

            queueCache.AddToCache([new TestBatchContainer(streamId, futureToken)]);
            var coldStreamConsumerData = streamData.AddConsumer(
                GuidId.GetGuidId(SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid())),
                qualifiedStreamId,
                new RecordingConsumer(
                    StreamHandshakeToken.CreateStartPositionToken(StreamSubscriptionStartPosition.Latest)),
                filterData: null,
                now: DateTime.UtcNow);

            Assert.True(await accessor.DoHandshakeWithConsumer(coldStreamConsumerData, cacheToken: futureToken));
            var coldStreamCursor = Assert.IsAssignableFrom<IQueueCacheCursor>(coldStreamConsumerData.Cursor);
            Assert.True(coldStreamCursor.MoveNext());
            Assert.Equal(futureToken, Assert.IsType<TestBatchContainer>(coldStreamCursor.GetCurrent(out _)).SequenceToken);
            await accessor.Shutdown();
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Theory, TestCategory("BVT"), TestCategory("Streaming")]
        [InlineData(false)]
        [InlineData(true)]
        public async Task EarliestStartRequestsShareCacheMissRecovery(bool explicitStartPosition)
        {
            var streamId = StreamId.Create("namespace", Guid.NewGuid());
            var qualifiedStreamId = new QualifiedStreamId("provider", streamId);
            var retainedToken = new EventSequenceTokenV2(1);
            var queueCache = new PurgeablePooledQueueCache();
            queueCache.AddToCache([new TestBatchContainer(streamId, retainedToken)]);
            var options = new StreamPullingAgentOptions
            {
                InitialSubscriptionStartPosition = explicitStartPosition
                    ? StreamSubscriptionStartPosition.Latest
                    : StreamSubscriptionStartPosition.EarliestAvailable,
            };
            var (accessor, _, streamData) = await CreateInitializedAgentWithStream(
                qualifiedStreamId,
                retainedToken,
                queueCache,
                options);
            var consumer = new RecordingConsumer(
                explicitStartPosition
                    ? StreamHandshakeToken.CreateStartPositionToken(StreamSubscriptionStartPosition.EarliestAvailable)
                    : null);
            consumer.ReleaseDelivery();
            var consumerData = streamData.AddConsumer(
                GuidId.GetGuidId(SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid())),
                qualifiedStreamId,
                consumer,
                filterData: null,
                now: DateTime.UtcNow);

            Assert.True(await accessor.DoHandshakeWithConsumer(consumerData, cacheToken: null));
            var startPositionToken = Assert.IsType<StartPositionToken>(consumerData.LastToken);
            Assert.Equal(StreamSubscriptionStartPosition.EarliestAvailable, startPositionToken.StartPosition);
            consumerData.IsRegistered = true;
            consumerData.Cursor = new CacheMissQueueCursor();

            await accessor.RunConsumerCursor(consumerData);

            Assert.Equal(retainedToken, Assert.Single(consumer.DeliveredTokens));
            var deliveredHandshakeToken = Assert.Single(consumer.DeliveredHandshakeTokens);
            if (explicitStartPosition)
            {
                var deliveredStartPositionToken = Assert.IsType<StartPositionToken>(deliveredHandshakeToken);
                Assert.Equal(StreamSubscriptionStartPosition.EarliestAvailable, deliveredStartPositionToken.StartPosition);
            }
            else
            {
                Assert.Null(deliveredHandshakeToken);
            }
            await accessor.Shutdown();
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task ExplicitSequenceTokenOverridesProviderEarliest()
        {
            var streamId = StreamId.Create("namespace", Guid.NewGuid());
            var qualifiedStreamId = new QualifiedStreamId("provider", streamId);
            var firstToken = new EventSequenceTokenV2(1);
            var explicitToken = new EventSequenceTokenV2(2);
            var queueCache = new PurgeablePooledQueueCache();
            queueCache.AddToCache(
            [
                new TestBatchContainer(streamId, firstToken),
                new TestBatchContainer(streamId, explicitToken),
            ]);
            var (accessor, _, streamData) = await CreateInitializedAgentWithStream(
                qualifiedStreamId,
                firstToken,
                queueCache,
                new StreamPullingAgentOptions
                {
                    InitialSubscriptionStartPosition = StreamSubscriptionStartPosition.EarliestAvailable,
                });
            var consumerData = streamData.AddConsumer(
                GuidId.GetGuidId(SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid())),
                qualifiedStreamId,
                new RecordingConsumer(StreamHandshakeToken.CreateStartToken(explicitToken)),
                filterData: null,
                now: DateTime.UtcNow);

            Assert.True(await accessor.DoHandshakeWithConsumer(consumerData, cacheToken: null));
            var cursor = Assert.IsAssignableFrom<IQueueCacheCursor>(consumerData.Cursor);
            Assert.True(cursor.MoveNext());
            Assert.Equal(explicitToken, Assert.IsType<TestBatchContainer>(cursor.GetCurrent(out _)).SequenceToken);
            await accessor.Shutdown();
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task InitialImplicitRecoveryAdvancesPastAcknowledgedMessage()
        {
            var streamId = StreamId.Create("namespace", Guid.NewGuid());
            var qualifiedStreamId = new QualifiedStreamId("provider", streamId);
            var acknowledgedToken = new EventSequenceTokenV2(1);
            var nextToken = new EventSequenceTokenV2(2);
            var queueCache = new PurgeablePooledQueueCache();
            queueCache.AddToCache(
            [
                new TestBatchContainer(streamId, acknowledgedToken),
                new TestBatchContainer(streamId, nextToken),
            ]);
            var queueAdapterCache = Substitute.For<IQueueAdapterCache>();
            queueAdapterCache.CreateQueueCache(Arg.Any<QueueId>()).Returns(queueCache);
            var agent = CreateAgent(
                pubSub: Substitute.For<IStreamPubSub>(),
                QueueId.GetQueueId("queue", 0u, 0u),
                queueAdapterCache: queueAdapterCache);
            var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            var consumerData = new StreamConsumerData(
                GuidId.GetGuidId(SubscriptionMarker.MarkAsImplictSubscriptionId(Guid.NewGuid())),
                qualifiedStreamId,
                new RecordingConsumer(StreamHandshakeToken.CreateStartToken(acknowledgedToken)),
                filterData: null);

            Assert.True(await accessor.DoHandshakeWithConsumer(consumerData, cacheToken: null));
            var cursor = Assert.IsAssignableFrom<IQueueCacheCursor>(consumerData.Cursor);
            Assert.True(cursor.MoveNext());
            Assert.Equal(nextToken, Assert.IsType<TestBatchContainer>(cursor.GetCurrent(out _)).SequenceToken);
            await accessor.Shutdown();
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task InitialImplicitRecoveryDeliversFirstNewerMessageWhenAcknowledgedMessageWasPurged()
        {
            var streamId = StreamId.Create("namespace", Guid.NewGuid());
            var qualifiedStreamId = new QualifiedStreamId("provider", streamId);
            var acknowledgedToken = new EventSequenceTokenV2(1);
            var nextToken = new EventSequenceTokenV2(2);
            var queueCache = new PurgeablePooledQueueCache(retainPurgeMetadata: true);
            queueCache.AddToCache([new TestBatchContainer(streamId, acknowledgedToken)]);
            queueCache.Purge();
            queueCache.AddToCache([new TestBatchContainer(streamId, nextToken)]);

            var queueAdapterCache = Substitute.For<IQueueAdapterCache>();
            queueAdapterCache.CreateQueueCache(Arg.Any<QueueId>()).Returns(queueCache);
            var agent = CreateAgent(
                pubSub: Substitute.For<IStreamPubSub>(),
                QueueId.GetQueueId("queue", 0u, 0u),
                queueAdapterCache: queueAdapterCache);
            var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            var consumer = new RecordingConsumer(StreamHandshakeToken.CreateStartToken(acknowledgedToken));
            var consumerData = new StreamConsumerData(
                GuidId.GetGuidId(SubscriptionMarker.MarkAsImplictSubscriptionId(Guid.NewGuid())),
                qualifiedStreamId,
                consumer,
                filterData: null);

            Assert.True(await accessor.DoHandshakeWithConsumer(consumerData, cacheToken: nextToken));
            var deliveryTask = accessor.RunConsumerCursor(consumerData);
            await consumer.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(nextToken, Assert.Single(consumer.DeliveredTokens));
            Assert.Empty(consumer.Errors);

            consumer.ReleaseDelivery();
            await deliveryTask;
            await accessor.Shutdown();
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task ReattachmentDeliveryTokenOverridesProviderEarliest()
        {
            var streamId = StreamId.Create("namespace", Guid.NewGuid());
            var qualifiedStreamId = new QualifiedStreamId("provider", streamId);
            var firstToken = new EventSequenceTokenV2(1);
            var deliveryToken = new EventSequenceTokenV2(2);
            var nextToken = new EventSequenceTokenV2(3);
            var queueCache = new PurgeablePooledQueueCache();
            queueCache.AddToCache(
            [
                new TestBatchContainer(streamId, firstToken),
                new TestBatchContainer(streamId, deliveryToken),
                new TestBatchContainer(streamId, nextToken),
            ]);
            var (accessor, _, streamData) = await CreateInitializedAgentWithStream(
                qualifiedStreamId,
                firstToken,
                queueCache,
                new StreamPullingAgentOptions
                {
                    InitialSubscriptionStartPosition = StreamSubscriptionStartPosition.EarliestAvailable,
                });
            var consumerData = streamData.AddConsumer(
                GuidId.GetGuidId(SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid())),
                qualifiedStreamId,
                new RecordingConsumer(StreamHandshakeToken.CreateDeliveyToken(deliveryToken)),
                filterData: null,
                now: DateTime.UtcNow);
            consumerData.IsRegistered = true;

            Assert.True(await accessor.DoHandshakeWithConsumer(consumerData, cacheToken: null));
            var cursor = Assert.IsAssignableFrom<IQueueCacheCursor>(consumerData.Cursor);
            Assert.True(cursor.MoveNext());
            Assert.Equal(nextToken, Assert.IsType<TestBatchContainer>(cursor.GetCurrent(out _)).SequenceToken);
            await accessor.Shutdown();
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task ProviderEarliestUnsupportedCacheFaultsExplicitSubscription()
        {
            var streamId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            var token = new EventSequenceTokenV2(1);
            var (accessor, pubSub, streamData) = await CreateInitializedAgentWithStream(
                streamId,
                token,
                new RecordingQueueCache(),
                new StreamPullingAgentOptions
                {
                    InitialSubscriptionStartPosition = StreamSubscriptionStartPosition.EarliestAvailable,
                });
            var subscriptionId = GuidId.GetGuidId(
                SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid()));
            var consumer = new RecordingConsumer();
            var consumerData = streamData.AddConsumer(
                subscriptionId,
                streamId,
                consumer,
                filterData: null,
                now: DateTime.UtcNow);

            Assert.False(await accessor.DoHandshakeWithConsumer(consumerData, cacheToken: null));
            Assert.IsType<NotSupportedException>(consumer.Errors[0]);
            Assert.IsType<FaultedSubscriptionException>(consumer.Errors[1]);
            Assert.False(streamData.TryGetConsumer(subscriptionId, out _));
            await pubSub.Received(1).FaultSubscription(streamId, subscriptionId, Arg.Any<CancellationToken>());
            await accessor.Shutdown();
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task ProviderEarliestUnsupportedCacheKeepsImplicitSubscriptionLive()
        {
            var streamId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            var token = new EventSequenceTokenV2(1);
            var (accessor, pubSub, streamData) = await CreateInitializedAgentWithStream(
                streamId,
                token,
                new RecordingQueueCache(),
                new StreamPullingAgentOptions
                {
                    InitialSubscriptionStartPosition = StreamSubscriptionStartPosition.EarliestAvailable,
                });
            var subscriptionId = GuidId.GetGuidId(
                SubscriptionMarker.MarkAsImplictSubscriptionId(Guid.NewGuid()));
            var consumer = new RecordingConsumer();
            var consumerData = streamData.AddConsumer(
                subscriptionId,
                streamId,
                consumer,
                filterData: null,
                now: DateTime.UtcNow);

            Assert.True(await accessor.DoHandshakeWithConsumer(consumerData, cacheToken: null));
            Assert.IsType<NotSupportedException>(Assert.Single(consumer.Errors));
            Assert.True(streamData.TryGetConsumer(subscriptionId, out var retainedConsumer));
            Assert.NotNull(retainedConsumer.Cursor);
            await pubSub.DidNotReceive().FaultSubscription(streamId, subscriptionId, Arg.Any<CancellationToken>());
            await accessor.Shutdown();
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
                GuidId.GetGuidId(SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid())),
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
                GuidId.GetGuidId(SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid())),
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
                GuidId.GetGuidId(SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid())),
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
        public async Task DeliveryTokenRenegotiationDeliversFirstNewerMessageWhenAcknowledgedMessageWasPurged()
        {
            var streamId = StreamId.Create("namespace", Guid.NewGuid());
            var qualifiedStreamId = new QualifiedStreamId("provider", streamId);
            var acknowledgedToken = new EventSequenceTokenV2(1);
            var attemptedToken = new EventSequenceTokenV2(2);
            var queueCache = new PurgeablePooledQueueCache(retainPurgeMetadata: true);
            queueCache.AddToCache([new TestBatchContainer(streamId, acknowledgedToken)]);
            queueCache.Purge();
            queueCache.AddToCache([new TestBatchContainer(streamId, attemptedToken)]);
            var queueAdapterCache = Substitute.For<IQueueAdapterCache>();
            queueAdapterCache.CreateQueueCache(Arg.Any<QueueId>()).Returns(queueCache);
            var agent = CreateAgent(pubSub: Substitute.For<IStreamPubSub>(), QueueId.GetQueueId("queue", 0u, 0u), queueAdapterCache: queueAdapterCache);
            var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            var consumer = new RenegotiatingDeliveryTokenConsumer(acknowledgedToken);
            var consumerData = new StreamConsumerData(
                GuidId.GetGuidId(SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid())),
                qualifiedStreamId,
                consumer,
                filterData: null)
            {
                Cursor = queueCache.GetCacheCursor(streamId, attemptedToken),
            };

            await accessor.RunConsumerCursor(consumerData);

            Assert.Equal(attemptedToken, Assert.Single(consumer.DeliveredTokens));
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task ImplicitRecoveryStartTokenAdvancesPastAcknowledgedMessage()
        {
            var streamId = StreamId.Create("namespace", Guid.NewGuid());
            var qualifiedStreamId = new QualifiedStreamId("provider", streamId);
            var acknowledgedToken = new EventSequenceTokenV2(1);
            var nextToken = new EventSequenceTokenV2(2);
            var queueCache = new PurgeablePooledQueueCache();
            queueCache.AddToCache(
            [
                new TestBatchContainer(streamId, acknowledgedToken),
                new TestBatchContainer(streamId, nextToken),
            ]);
            var queueAdapterCache = Substitute.For<IQueueAdapterCache>();
            queueAdapterCache.CreateQueueCache(Arg.Any<QueueId>()).Returns(queueCache);
            var agent = CreateAgent(pubSub: Substitute.For<IStreamPubSub>(), QueueId.GetQueueId("queue", 0u, 0u), queueAdapterCache: queueAdapterCache);
            var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            var consumer = new RenegotiatingStartTokenConsumer(acknowledgedToken);
            var consumerData = new StreamConsumerData(
                GuidId.GetGuidId(SubscriptionMarker.MarkAsImplictSubscriptionId(Guid.NewGuid())),
                qualifiedStreamId,
                consumer,
                filterData: null)
            {
                Cursor = queueCache.GetCacheCursor(streamId, acknowledgedToken),
            };

            await accessor.RunConsumerCursor(consumerData);

            Assert.Equal(nextToken, Assert.Single(consumer.DeliveredTokens));
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
            await pubSub.Received(1).FaultSubscription(
                consumerData.StreamId,
                consumerData.SubscriptionId,
                Arg.Any<CancellationToken>());
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
            await pubSub.Received(1).FaultSubscription(
                consumerData.StreamId,
                consumerData.SubscriptionId,
                Arg.Any<CancellationToken>());
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
            pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken)
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
            pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken)
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
            await pubSub.Received(1).FaultSubscription(
                qualifiedStreamId,
                consumerData.SubscriptionId,
                Arg.Any<CancellationToken>());
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
            pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken)
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
            await pubSub.Received(1).FaultSubscription(
                streamId,
                consumerData.SubscriptionId,
                Arg.Any<CancellationToken>());
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task ProviderDefaultEarliestRejectsInvalidCustomCacheResult()
        {
            var streamId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            var queueCache = new InvalidPositionQueueCache();
            var queueAdapterCache = Substitute.For<IQueueAdapterCache>();
            queueAdapterCache.CreateQueueCache(Arg.Any<QueueId>()).Returns(queueCache);
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken)
                .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));
            var agent = CreateAgent(
                pubSub,
                QueueId.GetQueueId("queue", 0u, 0u),
                options: new StreamPullingAgentOptions
                {
                    InitialSubscriptionStartPosition = StreamSubscriptionStartPosition.EarliestAvailable,
                },
                queueAdapterCache: queueAdapterCache);
            var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            await accessor.RegisterStream(streamId, new EventSequenceTokenV2(1), DateTime.UtcNow);
            var consumer = new RecordingConsumer();
            var streamData = (await accessor.GetPubSubCache()).Single().Value;
            var consumerData = streamData.AddConsumer(
                GuidId.GetGuidId(SubscriptionMarker.MarkAsImplictSubscriptionId(Guid.NewGuid())),
                streamId,
                consumer,
                filterData: null,
                now: DateTime.UtcNow);

            Assert.False(await accessor.DoHandshakeWithConsumer(consumerData, cacheToken: null));

            Assert.IsType<InvalidOperationException>(Assert.Single(consumer.Errors));
            Assert.Null(consumerData.Cursor);
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task RunConsumerCursorStopsAfterInvalidMoveResult()
        {
            var streamId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var queueCache = new InvalidMoveQueueCache();
            var queueAdapterCache = Substitute.For<IQueueAdapterCache>();
            queueAdapterCache.CreateQueueCache(Arg.Any<QueueId>()).Returns(queueCache);
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken)
                .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));
            var agent = CreateAgent(
                pubSub,
                queueId,
                queueAdapterCache: queueAdapterCache);
            var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            await accessor.RegisterStream(streamId, new EventSequenceTokenV2(1), DateTime.UtcNow);
            var streamData = (await accessor.GetPubSubCache()).Single().Value;
            var consumerData = streamData.AddConsumer(
                GuidId.GetGuidId(Guid.NewGuid()),
                streamId,
                new RecordingConsumer(),
                filterData: null,
                now: DateTime.UtcNow);
            consumerData.IsRegistered = true;
            consumerData.Cursor = queueCache.Cursor;
            queueCache.ResetAcquisitionCount();

            var exception = await Assert.ThrowsAnyAsync<InvalidOperationException>(
                () => accessor.RunConsumerCursor(consumerData));

            Assert.Equal("The cursor move result is not initialized.", exception.Message);
            Assert.True(queueCache.Cursor.IsDisposed);
            Assert.Equal(StreamConsumerDataState.Inactive, consumerData.State);
            Assert.Equal(0, queueCache.CursorAcquisitionCount);

            await accessor.RunQueuePump(queueId, TestContext.Current.CancellationToken);

            Assert.Null(consumerData.Cursor);
            Assert.True(consumerData.HasDeliveryProgressError);
            Assert.Null(consumerData.DeliveryRecoveryToken);
            Assert.Equal(0, queueCache.CursorAcquisitionCount);
            await accessor.Shutdown();
        }

        private sealed class RewindConsumer(StreamHandshakeToken rewindToken) : IStreamConsumerExtension
        {
            private bool rewindRequested;

            public TaskCompletionSource<bool> Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task<StreamHandshakeToken?> DeliverImmutable(GuidId subscriptionId, QualifiedStreamId streamId, object item, StreamSequenceToken currentToken, StreamHandshakeToken? handshakeToken, CancellationToken cancellationToken)
            {
                throw new NotSupportedException();
            }

            public Task<StreamHandshakeToken?> DeliverMutable(GuidId subscriptionId, QualifiedStreamId streamId, object item, StreamSequenceToken currentToken, StreamHandshakeToken? handshakeToken, CancellationToken cancellationToken)
            {
                throw new NotSupportedException();
            }

            public Task<StreamHandshakeToken?> DeliverBatch(GuidId subscriptionId, QualifiedStreamId streamId, IBatchContainer item, StreamHandshakeToken? handshakeToken, CancellationToken cancellationToken)
            {
                Delivered.TrySetResult(true);
                if (!rewindRequested)
                {
                    rewindRequested = true;
                    return Task.FromResult<StreamHandshakeToken?>(rewindToken);
                }

                return Task.FromResult<StreamHandshakeToken?>(null);
            }

            public Task CompleteStream(GuidId subscriptionId, CancellationToken cancellationToken) => Task.CompletedTask;

            public Task ErrorInStream(GuidId subscriptionId, Exception exc, CancellationToken cancellationToken) => Task.CompletedTask;

            public Task<StreamHandshakeToken?> GetSequenceToken(GuidId subscriptionId, CancellationToken cancellationToken) => Task.FromResult<StreamHandshakeToken?>(rewindToken);
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task ReadFromQueue_RefreshesIdleCursorAfterItsTokenMetadataIsPurged()
        {
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken)
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
        [Fact]
        public async Task ReadFromQueue_ResumesFromEarliestAvailableAfterCacheMiss()
        {
            var requestedToken = new EventSequenceTokenV2(1, 2);
            var newestToken = new EventSequenceTokenV2(20, 4);
            var exception = new QueueCacheMissException("The cache entry was purged.");

            var queueCache = await RunCacheMissRecovery(exception, requestedToken, newestToken, supportsEarliestAvailable: true);
            var recoveryPosition = await queueCache.StartPositionRequested.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            Assert.Equal(StreamSubscriptionStartPosition.EarliestAvailable, recoveryPosition);
            Assert.False(queueCache.CursorRequested.IsCompleted);
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact]
        public async Task ReadFromQueue_UsesExistingRecoveryWhenCacheDoesNotSupportEarliestAvailable()
        {
            var requestedToken = new EventSequenceTokenV2(1, 2);
            var newestToken = new EventSequenceTokenV2(20, 4);
            var exception = new QueueCacheMissException("Cache miss from a custom queue cache");

            var queueCache = await RunCacheMissRecovery(exception, requestedToken, newestToken, supportsEarliestAvailable: false);
            var recoveryToken = await queueCache.CursorRequested.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            Assert.False(queueCache.StartPositionRequested.IsCompleted);
            Assert.Null(recoveryToken);
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public Task RunConsumerCursor_RecoversThroughLegacyCursorAtEarliestAvailable()
            => VerifyRunConsumerCursorRecoversThroughLegacyCursor(supportsEarliestAvailable: true);

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public Task RunConsumerCursor_RecoversThroughLegacyOnlyCacheWhenEarliestIsUnsupported()
            => VerifyRunConsumerCursorRecoversThroughLegacyCursor(supportsEarliestAvailable: false);

        private static async Task VerifyRunConsumerCursorRecoversThroughLegacyCursor(
            bool supportsEarliestAvailable)
        {
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken)
                .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(
                    new HashSet<PubSubSubscriptionState>()));
            pubSub.UnregisterProducer(default, default, TestContext.Current.CancellationToken)
                .ReturnsForAnyArgs(Task.CompletedTask);

            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var streamId = StreamId.Create("namespace", Guid.NewGuid());
            var qualifiedStreamId = new QualifiedStreamId("provider", streamId);
            var requestedToken = new EventSequenceTokenV2(0);
            var firstRetainedToken = new EventSequenceTokenV2(1);
            var secondRetainedToken = new EventSequenceTokenV2(2);
            var firstRetainedBatch = new TestBatchContainer(streamId, firstRetainedToken);
            var secondRetainedBatch = new TestBatchContainer(streamId, secondRetainedToken);
            var queueCache = RecoverableCacheMissQueueCache.Create(
                new QueueCacheMissException(requestedToken, firstRetainedToken, secondRetainedToken),
                supportsEarliestAvailable,
                [firstRetainedBatch, secondRetainedBatch]);
            var queueAdapterCache = Substitute.For<IQueueAdapterCache>();
            queueAdapterCache.CreateQueueCache(Arg.Any<QueueId>()).Returns(queueCache);
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            receiver.Shutdown(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
            var agent = CreateAgent(pubSub, queueId, receiver, queueAdapterCache);
            var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            var consumer = new ImmediateRecordingConsumer();
            StreamConsumerData? consumerData = null;

            try
            {
                await accessor.RegisterStream(qualifiedStreamId, firstRetainedToken, DateTime.UtcNow);
                var streamData = (await accessor.GetPubSubCache()).Single().Value;
                consumerData = streamData.AddConsumer(
                    GuidId.GetGuidId(Guid.NewGuid()),
                    qualifiedStreamId,
                    consumer,
                    filterData: null,
                    now: DateTime.UtcNow);
                consumerData.IsRegistered = true;
                consumerData.LastProcessedToken = requestedToken;
                consumerData.Cursor = queueCache.CreateCacheMissCursor();
                Assert.Same(queueCache.OldCursor, consumerData.Cursor);
                queueCache.ResetRequests();

                await accessor.RunConsumerCursor(consumerData);

                Assert.Equal(StreamConsumerDataState.Inactive, consumerData.State);
                Assert.Equal(1, queueCache.OldCursorMoveNextCount);
                Assert.Equal(1, queueCache.OldCursorDisposeCount);
                Assert.Same(queueCache.ReplacementCursor, consumerData.Cursor);
                Assert.Equal(0, queueCache.ReplacementDisposeCount);
                Assert.Null(consumerData.PendingBatch);
                Assert.Empty(consumer.Errors);
                Assert.Collection(
                    consumer.DeliveredTokens,
                    token => Assert.Same(firstRetainedToken, token),
                    token => Assert.Same(secondRetainedToken, token));
                Assert.Collection(
                    consumer.DeliveredBatches,
                    batch => Assert.Same(firstRetainedBatch, batch),
                    batch => Assert.Same(secondRetainedBatch, batch));
                Assert.Equal(3, queueCache.ReplacementMoveNextCount);
                Assert.Equal(2, queueCache.ReplacementGetCurrentCount);

                if (supportsEarliestAvailable)
                {
                    Assert.Collection(
                        queueCache.Requests,
                        request =>
                        {
                            Assert.Equal(streamId, request.StreamId);
                            Assert.Null(request.Token);
                            Assert.Equal(StreamSubscriptionStartPosition.EarliestAvailable, request.Position);
                        });
                }
                else
                {
                    Assert.False(queueCache.StartPositionRequested.IsCompleted);
                    Assert.Collection(
                        queueCache.Requests,
                        request =>
                        {
                            Assert.Equal(streamId, request.StreamId);
                            Assert.Same(requestedToken, request.Token);
                            Assert.Null(request.Position);
                        });
                }
            }
            finally
            {
                await accessor.Shutdown();
            }

            Assert.NotNull(consumerData);
            Assert.Null(consumerData.Cursor);
            Assert.Null(consumerData.PendingBatch);
            Assert.Equal(1, queueCache.OldCursorDisposeCount);
            Assert.Equal(1, queueCache.ReplacementDisposeCount);
        }

        private static async Task<RecoverableCacheMissQueueCache> RunCacheMissRecovery(
            QueueCacheMissException exception,
            StreamSequenceToken requestedToken,
            StreamSequenceToken newestToken,
            bool supportsEarliestAvailable)
        {
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default)
                .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));

            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var streamId = StreamId.Create("namespace", Guid.NewGuid());
            var qualifiedStreamId = new QualifiedStreamId("provider", streamId);
            var queueCache = RecoverableCacheMissQueueCache.Create(exception, supportsEarliestAvailable);
            var queueAdapterCache = Substitute.For<IQueueAdapterCache>();
            queueAdapterCache.CreateQueueCache(Arg.Any<QueueId>()).Returns(queueCache);
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IList<IBatchContainer>>([new TestBatchContainer(streamId, newestToken)]));

            var agent = CreateAgent(pubSub, queueId, receiver, queueAdapterCache);
            var testAccessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            await testAccessor.RegisterStream(qualifiedStreamId, requestedToken, DateTime.UtcNow);

            var streamData = (await testAccessor.GetPubSubCache()).Single().Value;
            var consumerData = streamData.AddConsumer(
                GuidId.GetGuidId(Guid.NewGuid()),
                qualifiedStreamId,
                new RecordingConsumer(),
                filterData: null,
                now: DateTime.UtcNow);
            consumerData.IsRegistered = true;
            consumerData.Cursor = queueCache.CreateCacheMissCursor();
            queueCache.ResetRequests();

            Assert.True(await testAccessor.ReadFromQueue(queueId, receiver, 1));

            return queueCache;
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task Shutdown_UsesAcceptedRedeliveryForDeliveryProgress()
        {
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken)
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
            receiver.Shutdown(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

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
            await testAccessor.ReadFromQueue(
                queueId,
                receiver,
                maxCacheAddCount: 1);
            await consumer.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            queueCache.ClearDeliveryProgress();
            await testAccessor.Shutdown();

            Assert.Equal(attemptedToken, Assert.Single(queueCache.DeliveryProgressTokens));
        }

        private static Task InitializeAgent(PersistentStreamPullingAgent agent) =>
            agent.RunOrQueueTask(() => agent.Initialize(TestContext.Current.CancellationToken));

        private static async Task<(
            PersistentStreamPullingAgent.ITestAccessor Accessor,
            IStreamPubSub PubSub,
            StreamConsumerCollection StreamData)> CreateInitializedAgentWithStream(
                QualifiedStreamId streamId,
                StreamSequenceToken registrationToken,
                IQueueCache queueCache,
                StreamPullingAgentOptions options)
        {
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default)
                .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));
            pubSub.FaultSubscription(Arg.Any<QualifiedStreamId>(), Arg.Any<GuidId>())
                .Returns(Task.FromResult(true));
            var queueAdapterCache = Substitute.For<IQueueAdapterCache>();
            queueAdapterCache.CreateQueueCache(Arg.Any<QueueId>()).Returns(queueCache);
            var agent = CreateAgent(
                pubSub,
                QueueId.GetQueueId("queue", 0u, 0u),
                queueAdapterCache: queueAdapterCache,
                options: options);
            var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            await accessor.RegisterStream(streamId, registrationToken, DateTime.UtcNow);
            var streamData = (await accessor.GetPubSubCache()).Single().Value;
            return (accessor, pubSub, streamData);
        }

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
            pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken)
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
            receiver.Shutdown(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

            var agent = CreateAgent(pubSub: null, queueId, receiver);
            var testAccessor = (PersistentStreamPullingAgent.ITestAccessor)agent;

            await InitializeAgent(agent);

            var pumpTask = testAccessor.RunQueuePump(queueId, TestContext.Current.CancellationToken);
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
            receiver.Shutdown(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
            var agent = CreateAgent(pubSub: null, queueId, receiver);
            var testAccessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);

            await testAccessor.Shutdown();
            await testAccessor.Shutdown();

            await receiver.Received(1).Shutdown(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
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
            receiver.Shutdown(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

            var agent = CreateAgent(pubSub: null, queueId, receiver);
            var testAccessor = (PersistentStreamPullingAgent.ITestAccessor)agent;

            await InitializeAgent(agent);
            await testAccessor.Shutdown();
            await InitializeAgent(agent);

            await testAccessor.RunQueuePump(queueId, TestContext.Current.CancellationToken);

            await receiver.Received(1).GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Theory, TestCategory("BVT"), TestCategory("Streaming")]
        [InlineData(false, false, 200)]
        [InlineData(true, false, 0)]
        [InlineData(false, true, 1)]
        public async Task Shutdown_AdvancesPastOnlyDrainedSubscriptions(bool holdFirstDelivery, bool holdNewDelivery, long expectedCheckpoint)
        {
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken)
                .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));
            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var idleId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            var activeId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            var queueCache = new PurgeablePooledQueueCache();
            var adapterCache = Substitute.For<IQueueAdapterCache>();
            adapterCache.CreateQueueCache(queueId).Returns(queueCache);
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            IList<IBatchContainer> messages =
            [
                new TestBatchContainer(idleId.StreamId, new EventSequenceTokenV2(1)),
                new TestBatchContainer(activeId.StreamId, new EventSequenceTokenV2(200))
            ];
            receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult(messages));
            var agent = CreateAgent(pubSub, queueId, receiver, adapterCache);
            var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            await accessor.RegisterStream(idleId, new EventSequenceTokenV2(0), DateTime.UtcNow);
            await accessor.RegisterStream(activeId, new EventSequenceTokenV2(0), DateTime.UtcNow);
            var streams = await accessor.GetPubSubCache();
            var idle = new RecordingConsumer();
            var active = new RecordingConsumer();
            var pending = new RecordingConsumer();
            active.ReleaseDelivery();
            if (!holdFirstDelivery)
            {
                idle.ReleaseDelivery();
            }

            var idleData = AddConsumer(streams[idleId], idleId, idle);
            var activeData = AddConsumer(streams[activeId], activeId, active);
            try
            {
                await accessor.ReadFromQueue(queueId, receiver, 1000);
                await idle.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                await active.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                Assert.Equal(holdFirstDelivery ? 0 : 1, idleData.LastProcessedToken!.SequenceNumber);
                Assert.Equal(200, activeData.LastProcessedToken!.SequenceNumber);
                Assert.Equal(StreamConsumerDataState.Inactive, activeData.State);
                Assert.Equal(
                    holdFirstDelivery ? StreamConsumerDataState.Active : StreamConsumerDataState.Inactive,
                    idleData.State);
                Assert.Empty(idle.Errors);
                Assert.Empty(active.Errors);
                if (holdNewDelivery)
                {
                    idleData.StreamConsumer = pending;
                    messages =
                    [
                        new TestBatchContainer(idleId.StreamId, new EventSequenceTokenV2(201)),
                        new TestBatchContainer(activeId.StreamId, new EventSequenceTokenV2(300))
                    ];
                    await accessor.ReadFromQueue(queueId, receiver, 1000);
                    await pending.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                    Assert.Equal(1, idleData.LastProcessedToken.SequenceNumber);
                    Assert.Equal(300, activeData.LastProcessedToken.SequenceNumber);
                    Assert.Equal(StreamConsumerDataState.Active, idleData.State);
                }

                await accessor.Shutdown();
                var checkpoint = Assert.Single(queueCache.DeliveryProgressTokens);
                Assert.NotNull(checkpoint);
                Assert.Equal(expectedCheckpoint, checkpoint.SequenceNumber);
            }
            finally
            {
                idle.ReleaseDelivery();
                pending.ReleaseDelivery();
            }

            StreamConsumerData AddConsumer(StreamConsumerCollection collection, QualifiedStreamId id, RecordingConsumer consumer)
            {
                var data = collection.AddConsumer(GuidId.GetGuidId(Guid.NewGuid()), id, consumer, null, DateTime.UtcNow);
                data.IsRegistered = true;
                data.LastProcessedToken = new EventSequenceTokenV2(0);
                data.Cursor = queueCache.GetCacheCursor(id.StreamId, null);
                return data;
            }
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Theory, TestCategory("BVT"), TestCategory("Streaming")]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task Shutdown_DoesNotSkipFailedRegistrationOrInitialAttachment(bool subscriberAttachment, bool unknownPosition)
        {
            var failedId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            var otherId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            var subscription = new PubSubSubscriptionState(
                GuidId.GetGuidId(Guid.NewGuid()), failedId, GrainId.Create("test", Guid.NewGuid().ToString()));
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken)
                .ReturnsForAnyArgs(call => !subscriberAttachment
                    ? Task.FromException<ISet<PubSubSubscriptionState>>(new InvalidOperationException("Injected registration failure."))
                    : Task.FromResult<ISet<PubSubSubscriptionState>>(call.ArgAt<QualifiedStreamId>(0).Equals(failedId)
                        ? new HashSet<PubSubSubscriptionState> { subscription }
                        : new HashSet<PubSubSubscriptionState>()));
            var backoff = Substitute.For<IBackoffProvider>();
            backoff.Next(Arg.Any<int>()).Returns(_ => throw new OperationCanceledException("Injected registration retry cancellation."));
            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var queueCache = new RecordingQueueCache();
            var adapterCache = Substitute.For<IQueueAdapterCache>();
            adapterCache.CreateQueueCache(queueId).Returns(queueCache);
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IList<IBatchContainer>>(
                [
                    new TestBatchContainer(failedId.StreamId, unknownPosition ? null! : new EventSequenceTokenV2(100)),
                    new TestBatchContainer(otherId.StreamId, new EventSequenceTokenV2(200))
                ]));

            // Canceling the retry wait terminates registration. A missing RuntimeClient fails
            // attachment before a consumer record exists, after producer registration succeeds.
            var agent = CreateAgent(pubSub, queueId, receiver, adapterCache, deliveryBackoff: backoff);
            var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            Assert.True(await accessor.ReadFromQueue(queueId, receiver, 1000));
            var streams = await accessor.GetPubSubCache();
            await Task.WhenAll(streams.Values.Select(stream => stream.RegistrationTask ?? Task.CompletedTask))
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            if (subscriberAttachment)
            {
                Assert.True(streams[failedId].StreamRegistered);
                Assert.Equal(0, streams[failedId].Count);
                var pendingRegistration = streams[failedId].RegistrationTask;
                Assert.NotNull(pendingRegistration);
                Assert.True(pendingRegistration.IsCompleted);
            }
            else
            {
                var retainedStreams = await accessor.GetPubSubCache();
                Assert.True(retainedStreams.ContainsKey(failedId));
                Assert.All(retainedStreams.Values, stream =>
                {
                    Assert.False(stream.StreamRegistered);
                    Assert.NotNull(stream.RegistrationTask);
                    Assert.True(stream.RegistrationTask.IsCompleted);
                });
            }

            await accessor.Shutdown();

            Assert.Empty(queueCache.DeliveryProgressTokens);
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Theory, TestCategory("BVT"), TestCategory("Streaming")]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Shutdown_DoesNotAdvanceToReadCancelledAfterReceiverReturns(bool shutdownDuringRead)
        {
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken)
                .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));
            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var streamId = StreamId.Create("namespace", Guid.NewGuid());
            var queueCache = new RecordingQueueCache();
            var adapterCache = Substitute.For<IQueueAdapterCache>();
            adapterCache.CreateQueueCache(queueId).Returns(queueCache);
            var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finishRead = new TaskCompletionSource<IList<IBatchContainer>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            var readCount = 0;
            receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(_ =>
            {
                if (++readCount == 1)
                {
                    return Task.FromResult<IList<IBatchContainer>>([new TestBatchContainer(streamId, new EventSequenceTokenV2(10))]);
                }

                readStarted.TrySetResult();
                return finishRead.Task;
            });
            var agent = CreateAgent(pubSub, queueId, receiver, adapterCache);
            var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            await accessor.RegisterStream(new("provider", streamId), new EventSequenceTokenV2(10), DateTime.UtcNow);
            Assert.True(await accessor.ReadFromQueue(queueId, receiver, 1000));
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var pump = accessor.RunQueuePump(queueId, cancellation.Token);
            await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Task? shutdown = null;
            if (shutdownDuringRead)
            {
                shutdown = accessor.Shutdown();
                Assert.False(shutdown.IsCompleted);
            }
            else
            {
                cancellation.Cancel();
            }

            finishRead.SetResult([new TestBatchContainer(streamId, new EventSequenceTokenV2(100))]);
            await pump.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await (shutdown ?? accessor.Shutdown()).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            var checkpoint = Assert.Single(queueCache.DeliveryProgressTokens);
            Assert.NotNull(checkpoint);
            Assert.Equal(10, checkpoint.SequenceNumber);
            await receiver.Received(2).GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Theory, TestCategory("BVT"), TestCategory("Streaming")]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task Recovery_InitialGroupedFailureRequiresFirstBatchForInclusiveReplay(
            bool purgeFirstBatch, bool explicitEarliestRequest)
        {
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken)
                .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));
            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var streamId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            var firstToken = new EventSequenceTokenV2(10);
            var lastToken = new EventSequenceTokenV2(20);
            var queueCache = new PurgeablePooledQueueCache(retainPurgeMetadata: true);
            var adapterCache = Substitute.For<IQueueAdapterCache>();
            adapterCache.CreateQueueCache(queueId).Returns(queueCache);
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(
                Task.FromResult<IList<IBatchContainer>>(
                [
                    new TestBatchContainer(streamId.StreamId, firstToken),
                    new TestBatchContainer(streamId.StreamId, lastToken),
                ]),
                Task.FromResult<IList<IBatchContainer>>([]));
            var observer = new RecordingConsumer(explicitEarliestRequest
                ? StreamHandshakeToken.CreateStartPositionToken(StreamSubscriptionStartPosition.EarliestAvailable)
                : null)
            {
                DeliveryException = new InvalidOperationException("Injected grouped delivery failure."),
            };
            observer.ReleaseDelivery();
            var agent = CreateAgent(pubSub, queueId, receiver, adapterCache, new FakeTimeProvider(),
                options: new StreamPullingAgentOptions
                {
                    BatchContainerBatchSize = 2,
                    InitialSubscriptionStartPosition = explicitEarliestRequest
                        ? StreamSubscriptionStartPosition.Latest
                        : StreamSubscriptionStartPosition.EarliestAvailable,
                    MaxEventDeliveryTime = TimeSpan.FromMilliseconds(100),
                },
                deliveryBackoff: new FixedBackoff(TimeSpan.FromMilliseconds(150)));
            var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            await accessor.RegisterStream(streamId, new EventSequenceTokenV2(0), DateTime.UtcNow);
            Assert.True(await accessor.ReadFromQueue(queueId, receiver, 1000));
            var consumer = (await accessor.GetPubSubCache()).Single().Value.AddConsumer(
                GuidId.GetGuidId(SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid())),
                streamId, observer, null, DateTime.UtcNow);
            Assert.True(await accessor.DoHandshakeWithConsumer(consumer, cacheToken: null));
            consumer.IsRegistered = true;
            Assert.Null(consumer.LastProcessedToken);
            Assert.False(consumer.HasDeliveryProgressError);

            await accessor.RunConsumerCursor(consumer).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.NotEmpty(observer.DeliveredBatches);
            Assert.All(observer.DeliveredBatches, batch => Assert.Equal(new[] { 10L, 20L },
                Assert.IsType<BatchContainerBatch>(batch).BatchContainers.Select(item => item.SequenceToken.SequenceNumber)));
            Assert.True(consumer.HasDeliveryProgressError);
            Assert.Equal(firstToken, Assert.IsType<StartToken>(consumer.DeliveryRecoveryToken).Token);
            Assert.Null(consumer.LastProcessedToken);
            Assert.Equal(lastToken, consumer.UnconfirmedDeliveryToken);
            Assert.Equal(StreamConsumerDataState.Inactive, consumer.State);

            if (purgeFirstBatch)
            {
                queueCache.PurgeOldest();
                var acceptedOldPosition = queueCache.TryGetCacheCursor(streamId.StreamId, firstToken);
                Assert.Equal(QueueCacheCursorResultKind.Success, acceptedOldPosition.Kind);
                using var probe = Assert.IsAssignableFrom<IQueueCacheCursor>(acceptedOldPosition.Cursor);
                Assert.Equal(QueueCacheCursorMoveResultKind.Success, probe.MoveNextWithResult().Kind);
                Assert.Equal(20, Assert.IsType<TestBatchContainer>(probe.GetCurrent(out _)).SequenceToken.SequenceNumber);
            }

            observer.DeliveryException = null;
            observer.DeliveredBatches.Clear();
            await accessor.RunQueuePump(queueId, TestContext.Current.CancellationToken);
            Assert.NotEmpty(observer.DeliveredBatches);
            Assert.All(observer.DeliveredBatches, batch => Assert.Equal(
                purgeFirstBatch ? [20L] : new[] { 10L, 20L },
                Assert.IsType<BatchContainerBatch>(batch).BatchContainers.Select(item => item.SequenceToken.SequenceNumber)));
            Assert.Equal(purgeFirstBatch, consumer.HasDeliveryProgressError);
            Assert.Equal(!purgeFirstBatch, consumer.IsCaughtUp);
            if (purgeFirstBatch)
            {
                Assert.Equal(firstToken, Assert.IsType<StartToken>(consumer.DeliveryRecoveryToken).Token);
                Assert.Null(consumer.LastProcessedToken);
            }
            else
            {
                Assert.Null(consumer.DeliveryRecoveryToken);
                Assert.Equal(lastToken, consumer.LastProcessedToken);
            }

            await accessor.Shutdown();
            if (purgeFirstBatch)
            {
                Assert.Empty(queueCache.DeliveryProgressTokens);
            }
            else
            {
                Assert.Equal(lastToken, Assert.Single(queueCache.DeliveryProgressTokens));
            }
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Theory, TestCategory("BVT"), TestCategory("Streaming")]
        [InlineData("delivery", false, false, 1)]
        [InlineData("delivery", false, true, 1)]
        [InlineData("delivery", true, false, 1)]
        [InlineData("delivery", true, true, 1)]
        [InlineData("filter", false, false, 1)]
        [InlineData("filter", false, true, 1)]
        [InlineData("filter", true, false, 1)]
        [InlineData("filter", true, true, 1)]
        [InlineData("rewind", false, false, 1)]
        [InlineData("rewind", false, true, 1)]
        [InlineData("rewind", true, false, 1)]
        [InlineData("rewind", true, true, 1)]
        [InlineData("reattach", false, false, 1)]
        [InlineData("reattach", false, true, 1)]
        [InlineData("reattach", true, false, 1)]
        [InlineData("reattach", true, true, 1)]
        [InlineData("delivery", false, false, 2)]
        [InlineData("delivery", false, true, 2)]
        [InlineData("delivery", true, false, 2)]
        [InlineData("delivery", true, true, 2)]
        [InlineData("filter", false, false, 2)]
        [InlineData("filter", false, true, 2)]
        [InlineData("filter", true, false, 2)]
        [InlineData("filter", true, true, 2)]
        [InlineData("rewind", false, false, 2)]
        [InlineData("rewind", false, true, 2)]
        [InlineData("rewind", true, false, 2)]
        [InlineData("rewind", true, true, 2)]
        [InlineData("reattach", false, false, 2)]
        [InlineData("reattach", false, true, 2)]
        [InlineData("reattach", true, false, 2)]
        [InlineData("reattach", true, true, 2)]
        public async Task Shutdown_PreservesReplayRequirementAfterLaterProgress(
            string progressKind, bool unknownPosition, bool expireStream, int batchSize)
        {
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken)
                .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));
            var timeProvider = new FakeTimeProvider();
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var streamId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            var queueCache = new ScriptedQueueCache();
            var adapterCache = Substitute.For<IQueueAdapterCache>();
            adapterCache.CreateQueueCache(queueId).Returns(queueCache);
            StreamSequenceToken? safeToken = unknownPosition ? null : new EventSequenceTokenV2(10);
            var laterToken = new EventSequenceTokenV2(200);
            var batch = Substitute.For<IBatchContainer>();
            batch.StreamId.Returns(streamId.StreamId);
            batch.SequenceToken.Returns(laterToken);
            batch.GetEvents<object>().Returns([Tuple.Create<object, StreamSequenceToken>("payload", laterToken)]);
            var filter = progressKind == "filter" ? Substitute.For<IStreamFilter>() : null;
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(
                Task.FromResult<IList<IBatchContainer>>([batch]),
                Task.FromResult<IList<IBatchContainer>>([]));
            var agent = CreateAgent(pubSub, queueId, receiver, adapterCache, timeProvider,
                options: new StreamPullingAgentOptions { BatchContainerBatchSize = batchSize }, streamFilter: filter);
            var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            await accessor.RegisterStream(streamId, new EventSequenceTokenV2(10), now);
            var stream = (await accessor.GetPubSubCache()).Single().Value;
            var observer = new RecordingConsumer();
            if (progressKind == "rewind")
            {
                observer.DeliveryResponses.Enqueue(StreamHandshakeToken.CreateDeliveyToken(new EventSequenceTokenV2(100)));
            }

            observer.ReleaseDelivery();
            var failedCursor = Substitute.For<IQueueCacheCursor>();
            failedCursor.MoveNextWithResult().Returns(_ => throw new InvalidOperationException("Injected cursor failure."));
            var consumer = stream.AddConsumer(
                GuidId.GetGuidId(Guid.NewGuid()), streamId, observer, null, now);
            consumer.IsRegistered = true;
            consumer.LastProcessedToken = safeToken;
            consumer.Cursor = failedCursor;

            Assert.True(await accessor.ReadFromQueue(queueId, receiver, 1000));
            if (progressKind == "reattach")
            {
                observer.HandshakeResponse = StreamHandshakeToken.CreateDeliveyToken(new EventSequenceTokenV2(300));
                await agent.RunOrQueueTask(() => agent.AddSubscriber(
                    consumer.SubscriptionId, streamId, GrainId.Create("test", "existing-subscriber"), null,
                    TestContext.Current.CancellationToken));
            }

            failedCursor.Received(1).MoveNextWithResult();
            var recoveredInitially = !unknownPosition && progressKind != "rewind";
            Assert.Equal(!recoveredInitially, consumer.HasDeliveryProgressError);
            Assert.Equal(recoveredInitially, consumer.IsCaughtUp);
            Assert.Equal(StreamConsumerDataState.Inactive, consumer.State);
            if (recoveredInitially)
            {
                Assert.Equal(progressKind == "reattach" ? 300 : 200,
                    Assert.IsType<EventSequenceTokenV2>(consumer.LastProcessedToken).SequenceNumber);
            }
            else
            {
                Assert.Same(safeToken, consumer.LastProcessedToken);
            }
            Assert.Equal(0, consumer.PendingHandshakes);
            if (progressKind == "filter")
            {
                Assert.NotNull(filter);
                filter.Received(1).ShouldDeliver(streamId.StreamId, "payload", null);
                Assert.Null(consumer.LastToken);
                Assert.Empty(observer.DeliveredTokens);
            }
            else
            {
                var deliveryToken = Assert.IsType<DeliveryToken>(consumer.LastToken);
                var sequenceToken = Assert.IsType<EventSequenceTokenV2>(deliveryToken.Token);
                Assert.Equal(progressKind == "reattach" ? 300 : 200, sequenceToken.SequenceNumber);
                Assert.Equal(progressKind == "rewind" ? 2 : 1, observer.DeliveredTokens.Count);
                Assert.All(observer.DeliveredTokens, token => Assert.Same(laterToken, token));
            }

            // A later rewind cursor is not proof of replay from the original safe anchor.
            // The next pump must retry that anchor before releasing the error.
            Assert.Null(consumer.UnconfirmedDeliveryToken);

            if (expireStream)
            {
                timeProvider.Advance(TimeSpan.FromDays(1));
                Assert.False(await accessor.ReadFromQueue(queueId, receiver, 1000));
                if (unknownPosition)
                {
                    Assert.Empty(await accessor.GetPubSubCache());
                    Assert.True(consumer.HasDeliveryProgressError);
                }
                else
                {
                    Assert.Equal(!recoveredInitially, consumer.HasDeliveryProgressError);
                    Assert.Empty(await accessor.GetPubSubCache());
                }
            }

            await accessor.Shutdown();

            if (unknownPosition)
            {
                Assert.Empty(queueCache.DeliveryProgressTokens);
            }
            else
            {
                var checkpoint = Assert.Single(queueCache.DeliveryProgressTokens);
                Assert.NotNull(checkpoint);
                Assert.Equal(recoveredInitially ? 200 : 10, checkpoint.SequenceNumber);
            }
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Theory, TestCategory("BVT"), TestCategory("Streaming")]
        [InlineData(false, false, 1)]
        [InlineData(false, true, 1)]
        [InlineData(true, false, 1)]
        [InlineData(true, true, 1)]
        [InlineData(false, false, 2)]
        [InlineData(false, true, 2)]
        [InlineData(true, false, 2)]
        [InlineData(true, true, 2)]
        public async Task Shutdown_PreservesReplayRequirementAfterRewindToEmptyCursor(bool unknownPosition, bool expireStream, int batchSize)
        {
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken)
                .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));
            var timeProvider = new FakeTimeProvider();
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var streamId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            StreamSequenceToken? safeToken = unknownPosition ? null : new EventSequenceTokenV2(10);
            var attemptedToken = new EventSequenceTokenV2(200);
            var failedCursor = Substitute.For<IQueueCacheCursor>();
            failedCursor.MoveNextWithResult().Returns(_ => throw new InvalidOperationException("Injected cursor failure."));
            var recoveryCursor = Substitute.For<IQueueCacheCursor>();
            recoveryCursor.MoveNextWithResult().Returns(QueueCacheCursorMoveResult.Success, QueueCacheCursorMoveResult.NoData);
            recoveryCursor.GetCurrent(out _).Returns(new TestBatchContainer(streamId.StreamId, attemptedToken));
            var queueCache = Substitute.For<IQueueCache>();
            queueCache.TryGetCacheCursor(Arg.Any<StreamId>(), Arg.Any<StreamSequenceToken?>())
                .Returns(QueueCacheCursorResult<IQueueCacheCursor>.FromCursor(new EmptyQueueCacheCursor()));
            var adapterCache = Substitute.For<IQueueAdapterCache>();
            adapterCache.CreateQueueCache(queueId).Returns(queueCache);
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(
                Task.FromResult<IList<IBatchContainer>>([new TestBatchContainer(streamId.StreamId, attemptedToken)]),
                Task.FromResult<IList<IBatchContainer>>([]));
            var agent = CreateAgent(pubSub, queueId, receiver, adapterCache, timeProvider,
                options: new StreamPullingAgentOptions { BatchContainerBatchSize = batchSize });
            var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            await accessor.RegisterStream(streamId, new EventSequenceTokenV2(10), now);
            var observer = new RecordingConsumer();
            observer.DeliveryResponses.Enqueue(StreamHandshakeToken.CreateDeliveyToken(new EventSequenceTokenV2(100)));
            observer.ReleaseDelivery();
            var consumer = (await accessor.GetPubSubCache()).Single().Value.AddConsumer(
                GuidId.GetGuidId(Guid.NewGuid()), streamId, observer, null, now);
            consumer.IsRegistered = true;
            consumer.LastProcessedToken = safeToken;
            consumer.Cursor = failedCursor;
            // Model a provider whose attempted batch is no longer retained when the consumer rewinds.
            queueCache.TryGetCacheCursor(Arg.Any<StreamId>(), Arg.Any<StreamSequenceToken?>()).Returns(
                QueueCacheCursorResult<IQueueCacheCursor>.FromCursor(recoveryCursor),
                QueueCacheCursorResult<IQueueCacheCursor>.FromCursor(new EmptyQueueCacheCursor()));

            Assert.True(await accessor.ReadFromQueue(queueId, receiver, 1000));

            failedCursor.Received(1).MoveNextWithResult();
            Assert.True(consumer.HasDeliveryProgressError);
            Assert.False(consumer.IsCaughtUp);
            Assert.Equal(StreamConsumerDataState.Inactive, consumer.State);
            Assert.Same(safeToken, consumer.LastProcessedToken);
            Assert.Same(attemptedToken, consumer.UnconfirmedDeliveryToken);
            var deliveryToken = Assert.IsType<DeliveryToken>(consumer.LastToken);
            var sequenceToken = Assert.IsType<EventSequenceTokenV2>(deliveryToken.Token);
            Assert.Equal(100, sequenceToken.SequenceNumber);
            Assert.Same(attemptedToken, Assert.Single(observer.DeliveredTokens));
            if (expireStream)
            {
                timeProvider.Advance(TimeSpan.FromDays(1));
                Assert.False(await accessor.ReadFromQueue(queueId, receiver, 1000));
                Assert.Empty(await accessor.GetPubSubCache());
                Assert.True(consumer.HasDeliveryProgressError);
                Assert.Null(consumer.UnconfirmedDeliveryToken);
                Assert.Null(consumer.Cursor);
                Assert.Null(consumer.PendingBatch);
            }

            await accessor.Shutdown();

            if (unknownPosition)
            {
                queueCache.DidNotReceive().UpdateDeliveryProgress(Arg.Any<StreamSequenceToken?>(), Arg.Any<DateTime>());
            }
            else
            {
                queueCache.Received(1).UpdateDeliveryProgress(safeToken, Arg.Any<DateTime>());
            }
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Theory, TestCategory("BVT"), TestCategory("Streaming")]
        [InlineData("delivery", false, false, false)]
        [InlineData("delivery", false, true, false)]
        [InlineData("delivery", true, false, false)]
        [InlineData("delivery", true, true, false)]
        [InlineData("implicit", false, false, false)]
        [InlineData("implicit", false, true, false)]
        [InlineData("implicit", true, false, false)]
        [InlineData("implicit", true, true, false)]
        [InlineData("explicit", false, false, false)]
        [InlineData("explicit", false, true, false)]
        [InlineData("explicit", true, false, false)]
        [InlineData("explicit", true, true, false)]
        [InlineData("delivery", false, false, true)]
        [InlineData("delivery", false, true, true)]
        [InlineData("delivery", true, false, true)]
        [InlineData("delivery", true, true, true)]
        [InlineData("implicit", false, false, true)]
        [InlineData("implicit", false, true, true)]
        [InlineData("implicit", true, false, true)]
        [InlineData("implicit", true, true, true)]
        [InlineData("explicit", false, false, true)]
        [InlineData("explicit", false, true, true)]
        [InlineData("explicit", true, false, true)]
        [InlineData("explicit", true, true, true)]
        public async Task Shutdown_PreservesReplayFloorForEarlierRequest(
            string tokenKind, bool unknownPosition, bool failCursorAcquisition, bool reattach)
        {
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken)
                .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));
            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var streamId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            var acknowledgedToken = new EventSequenceTokenV2(100);
            var attemptedToken = new EventSequenceTokenV2(200);
            var earlierToken = new EventSequenceTokenV2(50);
            var requestedToken = tokenKind == "delivery"
                ? StreamHandshakeToken.CreateDeliveyToken(earlierToken)
                : StreamHandshakeToken.CreateStartToken(earlierToken);
            var failedCursor = Substitute.For<IQueueCacheCursor>();
            var cursorError = new InvalidOperationException("Injected cursor failure before the rewind.");
            failedCursor.MoveNextWithResult().Returns(_ => throw cursorError);
            var recoveryCursor = Substitute.For<IQueueCacheCursor>();
            recoveryCursor.MoveNextWithResult().Returns(QueueCacheCursorMoveResult.Success, QueueCacheCursorMoveResult.NoData);
            recoveryCursor.GetCurrent(out _).Returns(new TestBatchContainer(streamId.StreamId, attemptedToken));
            var queueCache = Substitute.For<IQueueCache>();
            queueCache.TryGetCacheCursor(Arg.Any<StreamId>(), Arg.Any<StreamSequenceToken?>())
                .Returns(QueueCacheCursorResult<IQueueCacheCursor>.FromCursor(new EmptyQueueCacheCursor()));
            var progress = new List<StreamSequenceToken?>();
            queueCache.When(cache => cache.UpdateDeliveryProgress(Arg.Any<StreamSequenceToken?>(), Arg.Any<DateTime>()))
                .Do(call => progress.Add(call.ArgAt<StreamSequenceToken?>(0)));
            var adapterCache = Substitute.For<IQueueAdapterCache>();
            adapterCache.CreateQueueCache(queueId).Returns(queueCache);
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(
                Task.FromResult<IList<IBatchContainer>>([new TestBatchContainer(streamId.StreamId, attemptedToken)]));
            var agent = CreateAgent(pubSub, queueId, receiver, adapterCache);
            var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            await accessor.RegisterStream(streamId, acknowledgedToken, DateTime.UtcNow);
            var observer = new RecordingConsumer();
            if (!reattach)
            {
                observer.DeliveryResponses.Enqueue(requestedToken);
            }

            observer.ReleaseDelivery();
            var subscriptionId = SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid());
            if (tokenKind == "implicit")
            {
                subscriptionId = SubscriptionMarker.MarkAsImplictSubscriptionId(subscriptionId);
            }

            Assert.Equal(tokenKind == "implicit", SubscriptionMarker.IsImplicitSubscription(subscriptionId));

            var consumer = (await accessor.GetPubSubCache()).Single().Value.AddConsumer(
                GuidId.GetGuidId(subscriptionId), streamId, observer, null, DateTime.UtcNow);
            consumer.IsRegistered = true;
            // Seed the preceding acknowledgement; the unresolved error is raised by the cursor below.
            consumer.LastProcessedToken = unknownPosition ? null : acknowledgedToken;
            consumer.LastToken = unknownPosition ? null : StreamHandshakeToken.CreateDeliveyToken(acknowledgedToken);
            consumer.Cursor = failedCursor;
            var recoveryIssued = false;
            var earlierCursorRequested = false;
            var acquisitionError = new InvalidOperationException("Injected cursor acquisition failure at the earlier position.");
            queueCache.TryGetCacheCursor(Arg.Any<StreamId>(), Arg.Any<StreamSequenceToken?>()).Returns(call =>
            {
                if (!recoveryIssued)
                {
                    recoveryIssued = true;
                    return QueueCacheCursorResult<IQueueCacheCursor>.FromCursor(
                        reattach ? new EmptyQueueCacheCursor() : recoveryCursor);
                }

                if (call.ArgAt<StreamSequenceToken?>(1)?.SequenceNumber == earlierToken.SequenceNumber)
                {
                    earlierCursorRequested = true;
                    if (failCursorAcquisition)
                    {
                        throw acquisitionError;
                    }
                }

                return QueueCacheCursorResult<IQueueCacheCursor>.FromCursor(new EmptyQueueCacheCursor());
            });

            Assert.True(await accessor.ReadFromQueue(queueId, receiver, 1000));
            if (reattach)
            {
                observer.HandshakeResponse = requestedToken;
                await agent.RunOrQueueTask(() => agent.AddSubscriber(
                    consumer.SubscriptionId, streamId, GrainId.Create("test", "existing-subscriber"), null,
                    TestContext.Current.CancellationToken));
            }

            Assert.True(earlierCursorRequested);
            failedCursor.Received(1).MoveNextWithResult();
            Assert.Contains(cursorError, observer.Errors);
            Assert.True(consumer.HasDeliveryProgressError);
            Assert.False(consumer.IsCaughtUp);
            Assert.Equal(StreamConsumerDataState.Inactive, consumer.State);
            if (reattach)
            {
                Assert.Empty(observer.DeliveredTokens);
            }
            else
            {
                Assert.Same(attemptedToken, Assert.Single(observer.DeliveredTokens));
            }
            await accessor.Shutdown();

            if (unknownPosition || tokenKind == "explicit" || reattach && tokenKind == "implicit")
            {
                Assert.Null(consumer.LastProcessedToken);
                Assert.Empty(progress);
            }
            else
            {
                Assert.Equal(earlierToken, consumer.LastProcessedToken);
                Assert.Equal(earlierToken, Assert.Single(progress));
            }
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Theory, TestCategory("BVT"), TestCategory("Streaming")]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task Shutdown_PreservesReplayFloorAfterAccountingFailure(bool failCacheAdd, bool includeUnreadTail)
        {
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken)
                .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));
            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var idleId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            var failedId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            var tailId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            var activeId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            var backingCache = new PurgeablePooledQueueCache();
            var queueCache = Substitute.For<IQueueCache>();
            var accountingError = new InvalidOperationException("Injected synchronous fetched-batch accounting failure.");
            queueCache.When(cache => cache.AddToCache(Arg.Any<IList<IBatchContainer>>())).Do(call =>
            {
                var batches = call.Arg<IList<IBatchContainer>>();
                if (failCacheAdd && batches.Any(batch => batch.StreamId.Equals(failedId.StreamId)))
                {
                    throw accountingError;
                }

                backingCache.AddToCache(batches);
            });
            queueCache.TryGetCacheCursor(Arg.Any<StreamId>(), Arg.Any<StreamSequenceToken?>()).Returns(call =>
            {
                var streamId = call.ArgAt<StreamId>(0);
                if (!failCacheAdd && streamId.Equals(failedId.StreamId))
                {
                    throw accountingError;
                }

                return backingCache.TryGetCacheCursor(streamId, call.ArgAt<StreamSequenceToken?>(1));
            });
            queueCache.When(cache => cache.UpdateDeliveryProgress(Arg.Any<StreamSequenceToken?>(), Arg.Any<DateTime>()))
                .Do(call => backingCache.UpdateDeliveryProgress(call.ArgAt<StreamSequenceToken?>(0), call.Arg<DateTime>()));
            var adapterCache = Substitute.For<IQueueAdapterCache>();
            adapterCache.CreateQueueCache(queueId).Returns(queueCache);
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            var failedRead = new List<IBatchContainer>
            {
                new TestBatchContainer(failedId.StreamId, new EventSequenceTokenV2(100)),
            };
            if (includeUnreadTail)
            {
                failedRead.Add(new TestBatchContainer(tailId.StreamId, new EventSequenceTokenV2(150)));
            }

            receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(
                Task.FromResult<IList<IBatchContainer>>([new TestBatchContainer(idleId.StreamId, new EventSequenceTokenV2(1))]),
                Task.FromResult<IList<IBatchContainer>>(failedRead),
                Task.FromResult<IList<IBatchContainer>>([new TestBatchContainer(activeId.StreamId, new EventSequenceTokenV2(200))]));
            var agent = CreateAgent(pubSub, queueId, receiver, adapterCache);
            var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            await accessor.RegisterStream(idleId, new EventSequenceTokenV2(0), DateTime.UtcNow);
            await accessor.RegisterStream(activeId, new EventSequenceTokenV2(0), DateTime.UtcNow);
            var streams = await accessor.GetPubSubCache();
            var idleObserver = new RecordingConsumer();
            var activeObserver = new RecordingConsumer();
            var idleConsumer = AddConsumer(idleId, idleObserver);
            var activeConsumer = AddConsumer(activeId, activeObserver);

            Assert.True(await accessor.ReadFromQueue(queueId, receiver, 1000));
            Assert.True(idleConsumer.IsCaughtUp);
            Assert.Equal(1, Assert.Single(idleObserver.DeliveredTokens).SequenceNumber);
            if (failCacheAdd)
            {
                Assert.Same(accountingError, await Assert.ThrowsAsync<InvalidOperationException>(
                    () => accessor.ReadFromQueue(queueId, receiver, 1000)));
            }
            else
            {
                Assert.True(await accessor.ReadFromQueue(queueId, receiver, 1000));
            }

            streams = await accessor.GetPubSubCache();
            await Task.WhenAll(streams.Values.Select(stream => stream.RegistrationTask ?? Task.CompletedTask))
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            if (failCacheAdd)
            {
                Assert.False(streams.ContainsKey(failedId));
                Assert.False(streams.ContainsKey(tailId));
            }
            else
            {
                Assert.True(streams.ContainsKey(failedId));
                Assert.Equal(includeUnreadTail, streams.ContainsKey(tailId));
                var pendingRegistration = streams[failedId].RegistrationTask;
                Assert.NotNull(pendingRegistration);
                Assert.True(pendingRegistration.IsCompleted);
                Assert.False(streams[failedId].StreamRegistered);
                Assert.Equal(100, Assert.IsType<EventSequenceTokenV2>(streams[failedId].RegistrationStartToken).SequenceNumber);
            }

            await pubSub.DidNotReceive().RegisterProducer(failedId, Arg.Any<GrainId>(), Arg.Any<CancellationToken>());
            Assert.Equal(failCacheAdd, await accessor.ReadFromQueue(queueId, receiver, 1000));
            if (failCacheAdd)
            {
                Assert.True(activeConsumer.IsCaughtUp);
                Assert.Equal(200, Assert.Single(activeObserver.DeliveredTokens).SequenceNumber);
            }
            else
            {
                Assert.Empty(activeObserver.DeliveredTokens);
                await receiver.Received(2).GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
            }

            await accessor.Shutdown();
            Assert.Empty(backingCache.DeliveryProgressTokens);

            StreamConsumerData AddConsumer(QualifiedStreamId streamId, RecordingConsumer observer)
            {
                observer.ReleaseDelivery();
                var consumer = streams[streamId].AddConsumer(
                    GuidId.GetGuidId(Guid.NewGuid()), streamId, observer, null, DateTime.UtcNow);
                consumer.IsRegistered = true;
                consumer.LastProcessedToken = new EventSequenceTokenV2(0);
                consumer.Cursor = backingCache.GetCacheCursor(streamId.StreamId, null);
                return consumer;
            }
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Theory, TestCategory("BVT"), TestCategory("Streaming")]
        [InlineData("delivery", "throw", false)]
        [InlineData("delivery", "acquire-miss", false)]
        [InlineData("delivery", "advance-miss", false)]
        [InlineData("implicit", "throw", false)]
        [InlineData("implicit", "acquire-miss", false)]
        [InlineData("implicit", "advance-miss", false)]
        [InlineData("explicit", "throw", false)]
        [InlineData("explicit", "acquire-miss", false)]
        [InlineData("delivery", "throw", true)]
        [InlineData("delivery", "acquire-miss", true)]
        [InlineData("delivery", "advance-miss", true)]
        [InlineData("implicit", "throw", true)]
        [InlineData("implicit", "acquire-miss", true)]
        [InlineData("implicit", "advance-miss", true)]
        [InlineData("explicit", "throw", true)]
        [InlineData("explicit", "acquire-miss", true)]
        [InlineData("delivery", "none", false)]
        [InlineData("implicit", "none", false)]
        [InlineData("explicit", "none", false)]
        [InlineData("delivery", "none", true)]
        [InlineData("implicit", "none", true)]
        [InlineData("explicit", "none", true)]
        public async Task Shutdown_PreservesReplayAfterHandshakeFallback(string tokenKind, string failureMode, bool reattach)
        {
            var registration = new TaskCompletionSource<ISet<PubSubSubscriptionState>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken)
                .ReturnsForAnyArgs(_ => registration.Task);
            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var streamId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            var latestToken = new EventSequenceTokenV2(200);
            var requestedPosition = new EventSequenceTokenV2(tokenKind == "explicit" ? 50 : 100);
            var requestedToken = tokenKind == "delivery"
                ? StreamHandshakeToken.CreateDeliveyToken(requestedPosition)
                : StreamHandshakeToken.CreateStartToken(requestedPosition);
            var latestBatch = new TestBatchContainer(streamId.StreamId, latestToken);
            var acquisitionError = new InvalidOperationException("Injected handshake cursor acquisition failure.");
            var missingCursor = Substitute.For<IQueueCacheCursor>();
            missingCursor.MoveNextWithResult().Returns(QueueCacheCursorMoveResult.FromCacheMiss(new("requested", "low", "high")));
            var queueCache = Substitute.For<IQueueCache>();
            var requestedCursorCount = 0;
            queueCache.TryGetCacheCursor(Arg.Any<StreamId>(), Arg.Any<StreamSequenceToken?>()).Returns(call =>
            {
                if (call.ArgAt<StreamSequenceToken?>(1)?.SequenceNumber == requestedPosition.SequenceNumber)
                {
                    requestedCursorCount++;
                    switch (failureMode)
                    {
                        case "throw":
                            throw acquisitionError;
                        case "acquire-miss":
                            return QueueCacheCursorResult<IQueueCacheCursor>.FromCacheMiss(new("requested", "low", "high"));
                        case "advance-miss":
                            return QueueCacheCursorResult<IQueueCacheCursor>.FromCursor(missingCursor);
                    }
                }

                return QueueCacheCursorResult<IQueueCacheCursor>.FromCursor(
                    new ScriptedQueueCursor([latestBatch], streamId.StreamId, null));
            });
            var progress = new List<StreamSequenceToken?>();
            queueCache.When(cache => cache.UpdateDeliveryProgress(Arg.Any<StreamSequenceToken?>(), Arg.Any<DateTime>()))
                .Do(call => progress.Add(call.ArgAt<StreamSequenceToken?>(0)));
            var adapterCache = Substitute.For<IQueueAdapterCache>();
            adapterCache.CreateQueueCache(queueId).Returns(queueCache);
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IList<IBatchContainer>>([latestBatch]));
            var agent = CreateAgent(pubSub, queueId, receiver, adapterCache);
            var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            Assert.True(await accessor.ReadFromQueue(queueId, receiver, 1000));
            var stream = (await accessor.GetPubSubCache()).Single().Value;
            var registrationTask = stream.RegistrationTask;
            Assert.NotNull(registrationTask);
            Assert.False(registrationTask.IsCompleted);
            var subscriptionId = tokenKind == "implicit"
                ? SubscriptionMarker.MarkAsImplictSubscriptionId(Guid.NewGuid())
                : SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid());
            Assert.Equal(tokenKind == "implicit", SubscriptionMarker.IsImplicitSubscription(subscriptionId));
            var observer = new RecordingConsumer(reattach ? null : requestedToken);
            observer.ReleaseDelivery();
            var consumer = stream.AddConsumer(
                GuidId.GetGuidId(subscriptionId), streamId, observer, null, DateTime.UtcNow);
            Assert.False(consumer.IsRegistered);
            Assert.False(consumer.HasDeliveryProgressError);

            // The real initial-registration path finds this consumer instead of resolving a grain reference.
            registration.SetResult(new HashSet<PubSubSubscriptionState>
            {
                new(consumer.SubscriptionId, streamId, GrainId.Create("test", "existing-subscriber")),
            });
            await registrationTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            if (reattach)
            {
                Assert.True(consumer.IsCaughtUp);
                Assert.False(consumer.HasDeliveryProgressError);
                Assert.Equal(latestToken, consumer.LastProcessedToken);
                observer.DeliveredTokens.Clear();
                observer.HandshakeResponse = requestedToken;
                await agent.RunOrQueueTask(() => agent.AddSubscriber(
                    consumer.SubscriptionId, streamId, GrainId.Create("test", "existing-subscriber"), null,
                    TestContext.Current.CancellationToken));
            }

            Assert.True(stream.StreamRegistered);
            Assert.True(consumer.IsRegistered);
            Assert.Equal(0, consumer.PendingHandshakes);
            Assert.True(requestedCursorCount >= 1);
            if (reattach && tokenKind == "implicit" && failureMode == "advance-miss")
            {
                // A failed replacement yields rather than immediately opening another recovery cursor.
                await accessor.RunConsumerCursor(consumer);
            }
            Assert.Equal(StreamConsumerDataState.Inactive, consumer.State);
            Assert.NotEmpty(observer.DeliveredTokens);
            Assert.All(observer.DeliveredTokens, token => Assert.Same(latestToken, token));
            if (failureMode == "throw")
            {
                Assert.Contains(acquisitionError, observer.Errors);
            }
            else if (failureMode == "advance-miss")
            {
                missingCursor.Received().MoveNextWithResult();
            }

            await accessor.Shutdown();

            if (failureMode == "none")
            {
                Assert.False(consumer.HasDeliveryProgressError);
                Assert.Equal(latestToken, Assert.Single(progress));
            }
            else
            {
                if (tokenKind == "delivery")
                {
                    Assert.Equal(requestedPosition, Assert.Single(progress));
                    Assert.Equal(requestedPosition, consumer.LastProcessedToken);
                }
                else
                {
                    Assert.Empty(progress);
                    Assert.Null(consumer.LastProcessedToken);
                }

                Assert.True(consumer.HasDeliveryProgressError);
            }
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Theory, TestCategory("BVT"), TestCategory("Streaming")]
        [InlineData(false, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, false, false)]
        [InlineData(true, true, false)]
        [InlineData(false, false, true)]
        [InlineData(false, true, true)]
        [InlineData(true, false, true)]
        [InlineData(true, true, true)]
        public async Task Shutdown_PreservesReplayAfterUnorderedRead(bool separateReads, bool unknownFirst, bool nullContainer)
        {
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken)
                .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));
            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var knownId = StreamId.Create("namespace", Guid.NewGuid());
            var unknownId = StreamId.Create("namespace", Guid.NewGuid());
            var knownToken = new EventSequenceTokenV2(200);
            IBatchContainer knownBatch = new TestBatchContainer(knownId, knownToken);
            IBatchContainer unknownBatch = nullContainer ? null! : new TestBatchContainer(unknownId, null!);
            var firstBatch = unknownFirst ? unknownBatch : knownBatch;
            var secondBatch = unknownFirst ? knownBatch : unknownBatch;
            var queueCache = new RecordingQueueCache();
            var adapterCache = Substitute.For<IQueueAdapterCache>();
            adapterCache.CreateQueueCache(queueId).Returns(queueCache);
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(
                Task.FromResult<IList<IBatchContainer>>(separateReads ? [firstBatch] : [firstBatch, secondBatch]),
                Task.FromResult<IList<IBatchContainer>>([secondBatch]));
            var agent = CreateAgent(pubSub, queueId, receiver, adapterCache);
            var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);

            await ReadAndFinishRegistration();
            if (separateReads)
            {
                await ReadAndFinishRegistration();
            }

            var streams = await accessor.GetPubSubCache();
            Assert.Equal(nullContainer ? 1 : 2, streams.Count);
            Assert.All(streams.Values, stream =>
            {
                Assert.True(stream.StreamRegistered);
                Assert.Null(stream.RegistrationTask);
                Assert.Equal(0, stream.Count);
            });
            await accessor.Shutdown();

            if (nullContainer)
            {
                Assert.Equal(knownToken, Assert.Single(queueCache.DeliveryProgressTokens));
            }
            else
            {
                Assert.Empty(queueCache.DeliveryProgressTokens);
            }

            async Task ReadAndFinishRegistration()
            {
                Assert.True(await accessor.ReadFromQueue(queueId, receiver, 1000));
                var pendingStreams = await accessor.GetPubSubCache();
                await Task.WhenAll(pendingStreams.Values.Select(stream => stream.RegistrationTask ?? Task.CompletedTask))
                    .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            }
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Theory, TestCategory("BVT"), TestCategory("Streaming")]
        [InlineData("exact", 1)]
        [InlineData("exact", 2)]
        [InlineData("fallback", 1)]
        [InlineData("fallback", 2)]
        [InlineData("empty", 1)]
        [InlineData("empty", 2)]
        public async Task Recovery_OnlyAcknowledgedExactReplayReleasesProgress(string replayKind, int batchSize)
        {
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken)
                .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));
            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var streamId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            var queueCache = new PurgeablePooledQueueCache();
            var adapterCache = Substitute.For<IQueueAdapterCache>();
            adapterCache.CreateQueueCache(queueId).Returns(queueCache);
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(
                Task.FromResult<IList<IBatchContainer>>([new TestBatchContainer(streamId.StreamId, new EventSequenceTokenV2(100))]),
                Task.FromResult<IList<IBatchContainer>>([new TestBatchContainer(streamId.StreamId, new EventSequenceTokenV2(replayKind == "fallback" ? 500 : 200))]));
            var agent = CreateAgent(pubSub, queueId, receiver, adapterCache,
                options: new StreamPullingAgentOptions { BatchContainerBatchSize = batchSize });
            var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            await accessor.RegisterStream(streamId, new EventSequenceTokenV2(100), DateTime.UtcNow);
            var observer = new RecordingConsumer();
            observer.ReleaseDelivery();
            var consumer = (await accessor.GetPubSubCache()).Single().Value.AddConsumer(
                GuidId.GetGuidId(SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid())),
                streamId, observer, null, DateTime.UtcNow);
            consumer.IsRegistered = true;
            consumer.Cursor = queueCache.GetCacheCursor(streamId.StreamId, null);
            Assert.True(await accessor.ReadFromQueue(queueId, receiver, 1000));
            Assert.Equal(100, Assert.IsType<EventSequenceTokenV2>(consumer.LastProcessedToken).SequenceNumber);
            Assert.False(consumer.HasDeliveryProgressError);

            var failedCursor = Substitute.For<IQueueCacheCursor>();
            var cursorError = new InvalidOperationException("Injected cursor failure after acknowledged position 100.");
            if (replayKind == "fallback")
            {
                failedCursor.MoveNextWithResult().Returns(QueueCacheCursorMoveResult.FromCacheMiss(new("200", "500", "500")));
            }
            else
            {
                failedCursor.MoveNextWithResult().Returns(_ => throw cursorError);
            }
            consumer.Cursor = failedCursor;
            if (replayKind != "exact")
            {
                queueCache.AddToCache([new TestBatchContainer(streamId.StreamId, new EventSequenceTokenV2(200))]);
                queueCache.Purge();
            }

            if (replayKind == "empty")
            {
                await accessor.RunConsumerCursor(consumer);
            }
            else
            {
                Assert.True(await accessor.ReadFromQueue(queueId, receiver, 1000));
            }

            if (replayKind == "fallback")
            {
                failedCursor.Received(1).MoveNextWithResult();
                Assert.Empty(observer.Errors);
            }
            else
            {
                Assert.Contains(cursorError, observer.Errors);
            }
            Assert.Equal(StreamConsumerDataState.Inactive, consumer.State);
            var caughtUp = consumer.IsCaughtUp;
            await accessor.Shutdown();
            var expectedCheckpoint = replayKind == "exact" ? 200 : 100;
            Assert.Equal(expectedCheckpoint, Assert.IsType<EventSequenceTokenV2>(Assert.Single(queueCache.DeliveryProgressTokens)).SequenceNumber);
            Assert.Equal(replayKind != "exact", consumer.HasDeliveryProgressError);
            Assert.Equal(replayKind == "exact", caughtUp);
            Assert.Equal(
                replayKind == "empty" ? [100L] : new[] { 100L, replayKind == "fallback" ? 500L : 200L },
                observer.DeliveredTokens.Select(token => token.SequenceNumber));
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Theory, TestCategory("BVT"), TestCategory("Streaming")]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task Recovery_ExplicitStartSurvivesFallbackAndIdleCleanup(bool cleanupBeforeRecovery, bool cleanupAfterRecovery)
        {
            var registration = new TaskCompletionSource<ISet<PubSubSubscriptionState>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken)
                .ReturnsForAnyArgs(_ => registration.Task);
            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var streamId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            var timeProvider = new FakeTimeProvider();
            var backingCache = new PurgeablePooledQueueCache();
            backingCache.AddToCache(
            [
                new TestBatchContainer(streamId.StreamId, new EventSequenceTokenV2(50)),
                new TestBatchContainer(streamId.StreamId, new EventSequenceTokenV2(100)),
            ]);
            var queueCache = Substitute.For<IQueueCache>();
            queueCache.GetMaxAddCount().Returns(1000);
            queueCache.When(cache => cache.AddToCache(Arg.Any<IList<IBatchContainer>>()))
                .Do(call => backingCache.AddToCache(call.Arg<IList<IBatchContainer>>()));
            var failEarlierCursor = true;
            queueCache.TryGetCacheCursor(Arg.Any<StreamId>(), Arg.Any<StreamSequenceToken?>()).Returns(call =>
            {
                var token = call.ArgAt<StreamSequenceToken?>(1);
                if (failEarlierCursor && token?.SequenceNumber == 50)
                {
                    return QueueCacheCursorResult<IQueueCacheCursor>.FromCacheMiss(new("50", "100", "500"));
                }

                return backingCache.TryGetCacheCursor(call.ArgAt<StreamId>(0), token);
            });
            queueCache.When(cache => cache.UpdateDeliveryProgress(Arg.Any<StreamSequenceToken?>(), Arg.Any<DateTime>()))
                .Do(call => backingCache.UpdateDeliveryProgress(call.ArgAt<StreamSequenceToken?>(0), call.Arg<DateTime>()));
            var adapterCache = Substitute.For<IQueueAdapterCache>();
            adapterCache.CreateQueueCache(queueId).Returns(queueCache);
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(
                Task.FromResult<IList<IBatchContainer>>([new TestBatchContainer(streamId.StreamId, new EventSequenceTokenV2(500))]),
                Task.FromResult<IList<IBatchContainer>>([]));
            var agent = CreateAgent(pubSub, queueId, receiver, adapterCache, timeProvider,
                options: new StreamPullingAgentOptions { MaxEventDeliveryTime = TimeSpan.FromDays(2) });
            var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            Assert.True(await accessor.ReadFromQueue(queueId, receiver, 1000));
            var stream = (await accessor.GetPubSubCache()).Single().Value;
            var registrationTask = stream.RegistrationTask;
            Assert.NotNull(registrationTask);
            var observer = new RecordingConsumer(StreamHandshakeToken.CreateStartToken(new EventSequenceTokenV2(50)));
            observer.ReleaseDelivery();
            var consumer = stream.AddConsumer(
                GuidId.GetGuidId(SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid())),
                streamId, observer, null, timeProvider.GetUtcNow().UtcDateTime);
            registration.SetResult(new HashSet<PubSubSubscriptionState>
            {
                new(consumer.SubscriptionId, streamId, GrainId.Create("test", "existing-subscriber")),
            });
            await registrationTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.True(consumer.HasDeliveryProgressError);
            Assert.Null(consumer.LastProcessedToken);
            Assert.Equal(500, Assert.Single(observer.DeliveredTokens).SequenceNumber);
            Assert.Equal(500, Assert.IsType<EventSequenceTokenV2>(Assert.IsType<DeliveryToken>(consumer.LastToken).Token).SequenceNumber);
            if (cleanupBeforeRecovery)
            {
                timeProvider.Advance(TimeSpan.FromDays(1));
                await accessor.RunQueuePump(queueId, TestContext.Current.CancellationToken);
                Assert.True((await accessor.GetPubSubCache()).ContainsKey(streamId), "Unfulfilled inclusive replay must survive inactivity cleanup.");
            }

            observer.DeliveredTokens.Clear();
            failEarlierCursor = false;
            await accessor.RunQueuePump(queueId, TestContext.Current.CancellationToken);
            Assert.Equal(new[] { 50L, 100L, 500L }, observer.DeliveredTokens.Select(token => token.SequenceNumber));
            Assert.False(consumer.HasDeliveryProgressError);
            Assert.True(consumer.IsCaughtUp);
            if (cleanupAfterRecovery)
            {
                timeProvider.Advance(TimeSpan.FromDays(1));
                await accessor.RunQueuePump(queueId, TestContext.Current.CancellationToken);
                Assert.Empty(await accessor.GetPubSubCache());
            }

            await accessor.Shutdown();
            Assert.Equal(500, Assert.IsType<EventSequenceTokenV2>(Assert.Single(backingCache.DeliveryProgressTokens)).SequenceNumber);
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Theory, TestCategory("BVT"), TestCategory("Streaming")]
        [InlineData(false, false, 1)]
        [InlineData(false, false, 2)]
        [InlineData(false, true, 1)]
        [InlineData(false, true, 2)]
        [InlineData(true, false, 1)]
        [InlineData(true, false, 2)]
        [InlineData(true, true, 1)]
        [InlineData(true, true, 2)]
        public async Task Recovery_NewReadPreservesEmptyCursorReplayBoundary(
            bool inclusiveReplay, bool replayAvailable, int batchSize)
        {
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken)
                .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));
            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var streamId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            var requiredToken = new EventSequenceTokenV2(100);
            var latestToken = new EventSequenceTokenV2(500);
            var queueCache = new PurgeablePooledQueueCache(retainPurgeMetadata: true);
            queueCache.AddToCache(
            [
                new TestBatchContainer(streamId.StreamId, requiredToken),
                new TestBatchContainer(streamId.StreamId, new EventSequenceTokenV2(200)),
            ]);
            queueCache.Purge();
            var adapterCache = Substitute.For<IQueueAdapterCache>();
            adapterCache.CreateQueueCache(queueId).Returns(queueCache);
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IList<IBatchContainer>>(replayAvailable
                    ?
                    [
                        new TestBatchContainer(streamId.StreamId, requiredToken),
                        new TestBatchContainer(streamId.StreamId, new EventSequenceTokenV2(200)),
                        new TestBatchContainer(streamId.StreamId, latestToken),
                    ]
                    : [new TestBatchContainer(streamId.StreamId, latestToken)]));
            var agent = CreateAgent(pubSub, queueId, receiver, adapterCache,
                options: new StreamPullingAgentOptions { BatchContainerBatchSize = batchSize });
            var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            await accessor.RegisterStream(streamId, requiredToken, DateTime.UtcNow);
            var observer = new RecordingConsumer(inclusiveReplay
                ? StreamHandshakeToken.CreateStartToken(requiredToken)
                : StreamHandshakeToken.CreateDeliveyToken(requiredToken));
            observer.ReleaseDelivery();
            var consumer = (await accessor.GetPubSubCache()).Single().Value.AddConsumer(
                GuidId.GetGuidId(SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid())),
                streamId, observer, null, DateTime.UtcNow);
            Assert.True(await accessor.DoHandshakeWithConsumer(consumer, cacheToken: null));
            consumer.IsRegistered = true;
            Assert.False(consumer.HasDeliveryProgressError);

            var failedCursor = Substitute.For<IQueueCacheCursor>();
            var cursorError = new InvalidOperationException("Injected cursor failure before an empty-cache recovery.");
            failedCursor.MoveNextWithResult().Returns(_ => throw cursorError);
            consumer.SafeDisposeCursor(NullLogger.Instance);
            consumer.Cursor = failedCursor;
            await accessor.RunConsumerCursor(consumer);

            Assert.Contains(cursorError, observer.Errors);
            Assert.Empty(observer.DeliveredBatches);
            Assert.True(consumer.HasDeliveryProgressError);
            Assert.False(consumer.IsCaughtUp);
            Assert.Equal(StreamConsumerDataState.Inactive, consumer.State);
            var recoveryCursor = consumer.Cursor;
            Assert.NotNull(recoveryCursor);
            Assert.Same(recoveryCursor, consumer.DeliveryRecoveryCursor);
            var recoveryToken = consumer.DeliveryRecoveryToken;
            Assert.Equal(requiredToken, Assert.IsAssignableFrom<StreamHandshakeToken>(recoveryToken).Token);

            Assert.True(await accessor.ReadFromQueue(queueId, receiver, 1000));

            Assert.Equal(StreamConsumerDataState.Inactive, consumer.State);
            Assert.Equal(!replayAvailable, consumer.HasDeliveryProgressError);
            Assert.Equal(replayAvailable, consumer.IsCaughtUp);
            // A missing range must invalidate the recovery cursor, not silently reposition it to the new read.
            Assert.Null(consumer.DeliveryRecoveryCursor);
            if (replayAvailable)
            {
                Assert.Null(consumer.DeliveryRecoveryToken);
                Assert.Equal(latestToken, consumer.LastProcessedToken);
            }
            else
            {
                Assert.Same(recoveryToken, consumer.DeliveryRecoveryToken);
                Assert.Equal(inclusiveReplay ? null : requiredToken, consumer.LastProcessedToken);
            }

            var deliveredTokens = observer.DeliveredBatches
                .SelectMany(batch => batch is BatchContainerBatch group ? group.BatchContainers.AsEnumerable() : [batch])
                .Select(batch => batch.SequenceToken.SequenceNumber);
            Assert.Equal(replayAvailable ? new[] { 100L, 200L, 500L } : [500L], deliveredTokens);
            await accessor.Shutdown();
            if (inclusiveReplay && !replayAvailable)
            {
                Assert.Empty(queueCache.DeliveryProgressTokens);
            }
            else
            {
                Assert.Equal(replayAvailable ? latestToken : requiredToken, Assert.Single(queueCache.DeliveryProgressTokens));
            }
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Theory, TestCategory("BVT"), TestCategory("Streaming")]
        [InlineData(false, false, 1)]
        [InlineData(false, false, 2)]
        [InlineData(false, true, 1)]
        [InlineData(false, true, 2)]
        [InlineData(true, false, 1)]
        [InlineData(true, false, 2)]
        [InlineData(true, true, 1)]
        [InlineData(true, true, 2)]
        public async Task Recovery_AfterContractFailureReacquiresDisposedCursor(
            bool inclusiveReplay, bool replayAvailable, int batchSize)
        {
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken)
                .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));
            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var streamId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            var requiredToken = new EventSequenceTokenV2(100);
            var latestToken = new EventSequenceTokenV2(200);
            var queueCache = new PurgeablePooledQueueCache(retainPurgeMetadata: true);
            var adapterCache = Substitute.For<IQueueAdapterCache>();
            adapterCache.CreateQueueCache(queueId).Returns(queueCache);
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(
                Task.FromResult<IList<IBatchContainer>>(
                [
                    new TestBatchContainer(streamId.StreamId, requiredToken),
                    new TestBatchContainer(streamId.StreamId, latestToken),
                ]),
                Task.FromResult<IList<IBatchContainer>>([]));
            var agent = CreateAgent(pubSub, queueId, receiver, adapterCache,
                options: new StreamPullingAgentOptions { BatchContainerBatchSize = batchSize });
            var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            await accessor.RegisterStream(streamId, requiredToken, DateTime.UtcNow);
            Assert.True(await accessor.ReadFromQueue(queueId, receiver, 1000));
            var observer = new RecordingConsumer(inclusiveReplay
                ? StreamHandshakeToken.CreateStartToken(requiredToken)
                : StreamHandshakeToken.CreateDeliveyToken(requiredToken));
            observer.ReleaseDelivery();
            var consumer = (await accessor.GetPubSubCache()).Single().Value.AddConsumer(
                GuidId.GetGuidId(SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid())),
                streamId, observer, null, DateTime.UtcNow);
            Assert.True(await accessor.DoHandshakeWithConsumer(consumer, cacheToken: null));
            consumer.IsRegistered = true;
            consumer.SafeDisposeCursor(NullLogger.Instance);
            var invalidCursor = new InvalidMoveCursor();
            consumer.Cursor = invalidCursor;

            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => accessor.RunConsumerCursor(consumer));

            Assert.True(invalidCursor.IsDisposed);
            Assert.Null(consumer.Cursor);
            Assert.True(consumer.HasDeliveryProgressError);
            var recoveryToken = consumer.DeliveryRecoveryToken;
            Assert.Equal(requiredToken, Assert.IsAssignableFrom<StreamHandshakeToken>(recoveryToken).Token);
            Assert.Equal(StreamConsumerDataState.Inactive, consumer.State);
            consumer.PendingHandshakes++;
            await accessor.RunConsumerCursor(consumer);
            Assert.Null(consumer.Cursor);
            Assert.Equal(StreamConsumerDataState.Inactive, consumer.State);
            consumer.PendingHandshakes--;
            if (!replayAvailable)
            {
                queueCache.Purge();
            }

            await accessor.RunQueuePump(queueId, TestContext.Current.CancellationToken);

            Assert.NotNull(consumer.Cursor);
            Assert.Equal(StreamConsumerDataState.Inactive, consumer.State);
            Assert.Equal(!replayAvailable, consumer.HasDeliveryProgressError);
            Assert.Equal(replayAvailable, consumer.IsCaughtUp);
            if (replayAvailable)
            {
                Assert.Null(consumer.DeliveryRecoveryToken);
                Assert.Equal(latestToken, consumer.LastProcessedToken);
                var deliveredTokens = observer.DeliveredBatches
                    .SelectMany(batch => batch is BatchContainerBatch group ? group.BatchContainers.AsEnumerable() : [batch])
                    .Select(batch => batch.SequenceToken.SequenceNumber);
                Assert.Equal(inclusiveReplay ? new[] { 100L, 200L } : [200L], deliveredTokens);
            }
            else
            {
                Assert.Same(recoveryToken, consumer.DeliveryRecoveryToken);
                Assert.Same(consumer.Cursor, consumer.DeliveryRecoveryCursor);
                Assert.Equal(inclusiveReplay ? null : requiredToken, consumer.LastProcessedToken);
                Assert.Empty(observer.DeliveredBatches);
            }

            await accessor.Shutdown();
            if (inclusiveReplay && !replayAvailable)
            {
                Assert.Empty(queueCache.DeliveryProgressTokens);
            }
            else
            {
                Assert.Equal(replayAvailable ? latestToken : requiredToken, Assert.Single(queueCache.DeliveryProgressTokens));
            }

            await accessor.RunConsumerCursor(consumer);
            Assert.Null(consumer.Cursor);
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Theory, TestCategory("BVT"), TestCategory("Streaming")]
        [InlineData("delivery", false, false, 1)]
        [InlineData("delivery", false, true, 2)]
        [InlineData("delivery", true, false, 2)]
        [InlineData("delivery", true, true, 1)]
        [InlineData("start", false, false, 2)]
        [InlineData("start", false, true, 1)]
        [InlineData("start", true, false, 1)]
        [InlineData("start", true, true, 2)]
        [InlineData("none", false, false, 1)]
        [InlineData("none", false, false, 2)]
        public async Task Recovery_StaleCallbackCannotAcknowledgeReattachedCursor(
            string tokenKind, bool reuseCursor, bool lateRewind, int batchSize)
        {
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken)
                .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));
            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var streamId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            var attemptedToken = new EventSequenceTokenV2(200);
            var earlierToken = new EventSequenceTokenV2(50);
            var batch = new TestBatchContainer(streamId.StreamId, attemptedToken);
            var oldCursor = Substitute.For<IQueueCacheCursor>();
            oldCursor.MoveNextWithResult().Returns(QueueCacheCursorMoveResult.Success, QueueCacheCursorMoveResult.NoData);
            oldCursor.GetCurrent(out _).Returns(batch);
            IQueueCacheCursor reboundCursor = reuseCursor ? oldCursor : new EmptyQueueCacheCursor();
            var queueCache = Substitute.For<IQueueCache>();
            var failEarlierRequest = true;
            queueCache.TryGetCacheCursor(Arg.Any<StreamId>(), Arg.Any<StreamSequenceToken?>()).Returns(call =>
            {
                if (failEarlierRequest && call.ArgAt<StreamSequenceToken?>(1)?.SequenceNumber == 50)
                {
                    return QueueCacheCursorResult<IQueueCacheCursor>.FromCacheMiss(new("50", "100", "200"));
                }

                return QueueCacheCursorResult<IQueueCacheCursor>.FromCursor(reboundCursor);
            });
            var progress = new List<StreamSequenceToken?>();
            queueCache.When(cache => cache.UpdateDeliveryProgress(Arg.Any<StreamSequenceToken?>(), Arg.Any<DateTime>()))
                .Do(call => progress.Add(call.ArgAt<StreamSequenceToken?>(0)));
            var adapterCache = Substitute.For<IQueueAdapterCache>();
            adapterCache.CreateQueueCache(queueId).Returns(queueCache);
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IList<IBatchContainer>>([batch]));
            var agent = CreateAgent(pubSub, queueId, receiver, adapterCache,
                options: new StreamPullingAgentOptions { BatchContainerBatchSize = batchSize });
            var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            await accessor.RegisterStream(streamId, new EventSequenceTokenV2(100), DateTime.UtcNow);
            Assert.True(await accessor.ReadFromQueue(queueId, receiver, 1000));
            var observer = new RecordingConsumer();
            if (lateRewind)
            {
                observer.DeliveryResponses.Enqueue(StreamHandshakeToken.CreateDeliveyToken(new EventSequenceTokenV2(400)));
            }

            var consumer = (await accessor.GetPubSubCache()).Single().Value.AddConsumer(
                GuidId.GetGuidId(SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid())),
                streamId, observer, null, DateTime.UtcNow);
            consumer.IsRegistered = true;
            consumer.LastProcessedToken = new EventSequenceTokenV2(100);
            consumer.LastToken = StreamHandshakeToken.CreateDeliveyToken(consumer.LastProcessedToken);
            consumer.Cursor = oldCursor;
            var delivery = accessor.RunConsumerCursor(consumer);
            try
            {
                await observer.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                Assert.False(delivery.IsCompleted);
                Assert.Equal(100, Assert.IsType<EventSequenceTokenV2>(consumer.LastProcessedToken).SequenceNumber);
                Assert.False(consumer.HasDeliveryProgressError);
                if (tokenKind != "none")
                {
                    observer.HandshakeResponse = tokenKind == "start"
                        ? StreamHandshakeToken.CreateStartToken(earlierToken)
                        : StreamHandshakeToken.CreateDeliveyToken(earlierToken);
                    await Reattach();
                    Assert.True(consumer.HasDeliveryProgressError);

                    // Rebind an exact-but-empty recovery cursor while the old delivery is still blocked.
                    // Some providers recycle the same cursor object, so identity alone cannot identify the callback.
                    failEarlierRequest = false;
                    await Reattach();
                    Assert.Same(reboundCursor, consumer.Cursor);
                    Assert.Same(reboundCursor, consumer.DeliveryRecoveryCursor);
                    Assert.Equal(reuseCursor, ReferenceEquals(oldCursor, consumer.Cursor));
                    Assert.True(consumer.HasDeliveryProgressError);
                    Assert.Equal(0, consumer.PendingHandshakes);
                    Assert.Same(observer.HandshakeResponse, consumer.LastToken);
                }

                observer.ReleaseDelivery();
                await delivery.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                Assert.Same(attemptedToken, Assert.Single(observer.DeliveredTokens));
                Assert.Equal(StreamConsumerDataState.Inactive, consumer.State);
                if (tokenKind == "none")
                {
                    Assert.False(consumer.HasDeliveryProgressError);
                    Assert.True(consumer.IsCaughtUp);
                    Assert.Null(consumer.UnconfirmedDeliveryToken);
                    Assert.Equal(200, Assert.IsType<EventSequenceTokenV2>(consumer.LastProcessedToken).SequenceNumber);
                    Assert.Equal(200, Assert.IsType<EventSequenceTokenV2>(
                        Assert.IsType<DeliveryToken>(consumer.LastToken).Token).SequenceNumber);
                }
                else
                {
                    Assert.True(consumer.HasDeliveryProgressError);
                    Assert.False(consumer.IsCaughtUp);
                    Assert.Same(observer.HandshakeResponse, consumer.LastToken);
                    Assert.Equal(earlierToken, consumer.DeliveryRecoveryToken?.Token);
                    Assert.Same(attemptedToken, consumer.UnconfirmedDeliveryToken);
                    Assert.Equal(tokenKind == "start" ? null : earlierToken, consumer.LastProcessedToken);
                }

                await accessor.Shutdown();
                if (tokenKind == "start")
                {
                    Assert.Empty(progress);
                }
                else
                {
                    Assert.Equal(tokenKind == "none" ? 200 : 50,
                        Assert.IsType<EventSequenceTokenV2>(Assert.Single(progress)).SequenceNumber);
                }
            }
            finally
            {
                observer.ReleaseDelivery();
            }

            Task Reattach() => agent.RunOrQueueTask(() => agent.AddSubscriber(
                consumer.SubscriptionId, streamId, GrainId.Create("test", "reattached-subscriber"), null,
                TestContext.Current.CancellationToken));
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task Recovery_RetriesCanceledRegistrationFromOriginalCachedPosition()
        {
            var firstRegistration = new TaskCompletionSource<ISet<PubSubSubscriptionState>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pubSub = Substitute.For<IStreamPubSub>();
            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var streamId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            var subscriptionId = GuidId.GetGuidId(SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid()));
            var subscriptions = new HashSet<PubSubSubscriptionState>
            {
                new(subscriptionId, streamId, GrainId.Create("test", "existing-subscriber")),
            };
            pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken)
                .ReturnsForAnyArgs(_ => firstRegistration.Task, _ => Task.FromResult<ISet<PubSubSubscriptionState>>(subscriptions));
            var backoff = Substitute.For<IBackoffProvider>();
            // Cancel the first attempt's retry wait, without stopping the agent.
            var cancelFirstWait = true;
            backoff.Next(Arg.Any<int>()).Returns(_ =>
            {
                if (cancelFirstWait)
                {
                    cancelFirstWait = false;
                    throw new OperationCanceledException("Transient registration retry cancellation.");
                }

                return TimeSpan.Zero;
            });
            var queueCache = new PurgeablePooledQueueCache();
            var adapterCache = Substitute.For<IQueueAdapterCache>();
            adapterCache.CreateQueueCache(queueId).Returns(queueCache);
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(
                Task.FromResult<IList<IBatchContainer>>(
                [
                    new TestBatchContainer(streamId.StreamId, new EventSequenceTokenV2(100)),
                    new TestBatchContainer(streamId.StreamId, new EventSequenceTokenV2(200)),
                ]),
                Task.FromResult<IList<IBatchContainer>>([]));
            var agent = CreateAgent(pubSub, queueId, receiver, adapterCache, deliveryBackoff: backoff);
            var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            Assert.True(await accessor.ReadFromQueue(queueId, receiver, 1000));
            var stream = (await accessor.GetPubSubCache()).Single().Value;
            var firstTask = stream.RegistrationTask;
            Assert.NotNull(firstTask);
            var observer = new RecordingConsumer();
            observer.ReleaseDelivery();
            stream.AddConsumer(subscriptionId, streamId, observer, null, DateTime.UtcNow);
            firstRegistration.SetException(new InvalidOperationException("Transient pubsub registration failure."));
            await firstTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            await accessor.RunQueuePump(queueId, TestContext.Current.CancellationToken);
            var streams = await accessor.GetPubSubCache();
            await Task.WhenAll(streams.Values.Select(value => value.RegistrationTask ?? Task.CompletedTask))
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await pubSub.Received(2).RegisterProducer(streamId, Arg.Any<GrainId>(), Arg.Any<CancellationToken>());
            Assert.Equal(new[] { 100L, 200L }, observer.DeliveredTokens.Select(token => token.SequenceNumber));
            Assert.Equal(1, observer.HandshakeCount);
            await accessor.Shutdown();
            Assert.Equal(200, Assert.IsType<EventSequenceTokenV2>(Assert.Single(queueCache.DeliveryProgressTokens)).SequenceNumber);
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task Recovery_RetriesOnlyFailedInitialAttachmentsWithoutNewMessages()
        {
            var registration = new TaskCompletionSource<ISet<PubSubSubscriptionState>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken).ReturnsForAnyArgs(_ => registration.Task);
            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var streamId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            var failedId = GuidId.GetGuidId(SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid()));
            var healthyId = GuidId.GetGuidId(SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid()));
            var queueCache = new PurgeablePooledQueueCache();
            var adapterCache = Substitute.For<IQueueAdapterCache>();
            adapterCache.CreateQueueCache(queueId).Returns(queueCache);
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(
                Task.FromResult<IList<IBatchContainer>>(
                [
                    new TestBatchContainer(streamId.StreamId, new EventSequenceTokenV2(100)),
                    new TestBatchContainer(streamId.StreamId, new EventSequenceTokenV2(200)),
                ]),
                Task.FromResult<IList<IBatchContainer>>([]));
            var agent = CreateAgent(pubSub, queueId, receiver, adapterCache);
            var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            Assert.True(await accessor.ReadFromQueue(queueId, receiver, 1000));
            var stream = (await accessor.GetPubSubCache()).Single().Value;
            var firstTask = stream.RegistrationTask;
            Assert.NotNull(firstTask);
            var healthy = new RecordingConsumer();
            healthy.ReleaseDelivery();
            stream.AddConsumer(healthyId, streamId, healthy, null, DateTime.UtcNow);
            registration.SetResult(new HashSet<PubSubSubscriptionState>
            {
                new(failedId, streamId, GrainId.Create("test", "initially-unavailable-subscriber")),
                new(healthyId, streamId, GrainId.Create("test", "healthy-subscriber")),
            });
            await firstTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(1, healthy.HandshakeCount);
            Assert.Equal(new[] { 100L, 200L }, healthy.DeliveredTokens.Select(token => token.SequenceNumber));
            // The first attachment failed resolving its reference. Make it available for the retry.
            var recovered = new RecordingConsumer();
            recovered.ReleaseDelivery();
            stream.AddConsumer(failedId, streamId, recovered, null, DateTime.UtcNow);
            await accessor.RunQueuePump(queueId, TestContext.Current.CancellationToken);
            var streams = await accessor.GetPubSubCache();
            await Task.WhenAll(streams.Values.Select(value => value.RegistrationTask ?? Task.CompletedTask))
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.Equal(1, recovered.HandshakeCount);
            Assert.Equal(new[] { 100L, 200L }, recovered.DeliveredTokens.Select(token => token.SequenceNumber));
            Assert.Equal(1, healthy.HandshakeCount);
            Assert.Equal(new[] { 100L, 200L }, healthy.DeliveredTokens.Select(token => token.SequenceNumber));
            await accessor.Shutdown();
            Assert.Equal(200, Assert.IsType<EventSequenceTokenV2>(Assert.Single(queueCache.DeliveryProgressTokens)).SequenceNumber);
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Theory, TestCategory("BVT"), TestCategory("Streaming")]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task Recovery_RemovingConsumerReleasesOnlyItsReplayConstraint(bool faultFirstConsumer, bool removeSecondConsumer)
        {
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken)
                .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));
            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var streamId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            var queueCache = new RecordingQueueCache();
            var adapterCache = Substitute.For<IQueueAdapterCache>();
            adapterCache.CreateQueueCache(queueId).Returns(queueCache);
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(
                Task.FromResult<IList<IBatchContainer>>([new TestBatchContainer(streamId.StreamId, new EventSequenceTokenV2(200))]));
            var agent = CreateAgent(pubSub, queueId, receiver, adapterCache);
            var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            await accessor.RegisterStream(streamId, new EventSequenceTokenV2(0), DateTime.UtcNow);
            Assert.True(await accessor.ReadFromQueue(queueId, receiver, 1000));
            var stream = (await accessor.GetPubSubCache()).Single().Value;
            var firstObserver = new RecordingConsumer();
            firstObserver.ReleaseDelivery();
            var first = AddConsumer(firstObserver, 100);
            var second = AddConsumer(new RecordingConsumer(), 150);
            await accessor.RunConsumerCursor(first);
            await accessor.RunConsumerCursor(second);
            Assert.True(first.HasDeliveryProgressError);
            Assert.True(second.HasDeliveryProgressError);
            if (faultFirstConsumer)
            {
                firstObserver.HandshakeResponse = new UnknownHandshakeToken();
                await agent.RunOrQueueTask(() => agent.AddSubscriber(
                    first.SubscriptionId, streamId, GrainId.Create("test", "faulted-subscriber"), null,
                    TestContext.Current.CancellationToken));
                await pubSub.Received(1).FaultSubscription(streamId, first.SubscriptionId, Arg.Any<CancellationToken>());
            }
            else
            {
                await agent.RunOrQueueTask(() => agent.RemoveSubscriber(
                    first.SubscriptionId, streamId, TestContext.Current.CancellationToken));
            }

            Assert.False(stream.Contains(first.SubscriptionId));
            Assert.True(stream.Contains(second.SubscriptionId));
            Assert.True(second.HasDeliveryProgressError);
            if (removeSecondConsumer)
            {
                await agent.RunOrQueueTask(() => agent.RemoveSubscriber(
                    second.SubscriptionId, streamId, TestContext.Current.CancellationToken));
            }

            await accessor.Shutdown();
            Assert.Equal(removeSecondConsumer ? 200 : 150,
                Assert.IsType<EventSequenceTokenV2>(Assert.Single(queueCache.DeliveryProgressTokens)).SequenceNumber);

            StreamConsumerData AddConsumer(RecordingConsumer observer, long safePosition)
            {
                var consumer = stream.AddConsumer(
                    GuidId.GetGuidId(SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid())),
                    streamId, observer, null, DateTime.UtcNow);
                consumer.IsRegistered = true;
                consumer.LastProcessedToken = new EventSequenceTokenV2(safePosition);
                consumer.LastToken = StreamHandshakeToken.CreateDeliveyToken(consumer.LastProcessedToken);
                var cursor = Substitute.For<IQueueCacheCursor>();
                cursor.MoveNextWithResult().Returns(_ => throw new InvalidOperationException("Injected unresolved cursor failure."));
                consumer.Cursor = cursor;
                return consumer;
            }
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Theory, TestCategory("BVT"), TestCategory("Streaming")]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Shutdown_DoesNotReusePreviousReceiverReadBoundaryAfterReinitialize(bool readAfterReinitialize)
        {
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken)
                .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));
            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var streamId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            var queueCache = new RecordingQueueCache();
            var adapterCache = Substitute.For<IQueueAdapterCache>();
            adapterCache.CreateQueueCache(queueId).Returns(queueCache);
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(
                Task.FromResult<IList<IBatchContainer>>([new TestBatchContainer(streamId.StreamId, new EventSequenceTokenV2(200))]),
                Task.FromResult<IList<IBatchContainer>>([new TestBatchContainer(streamId.StreamId, new EventSequenceTokenV2(10))]));
            var agent = CreateAgent(pubSub, queueId, receiver, adapterCache);
            var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            await accessor.RegisterStream(streamId, new EventSequenceTokenV2(200), DateTime.UtcNow);
            Assert.True(await accessor.ReadFromQueue(queueId, receiver, 1000));
            await accessor.Shutdown();

            await InitializeAgent(agent);
            if (readAfterReinitialize)
            {
                await accessor.RegisterStream(streamId, new EventSequenceTokenV2(10), DateTime.UtcNow);
                Assert.True(await accessor.ReadFromQueue(queueId, receiver, 1000));
            }

            await accessor.Shutdown();

            Assert.Equal(
                new long?[] { 200, readAfterReinitialize ? 10 : null },
                queueCache.DeliveryProgressTokens.Select(token => token?.SequenceNumber));
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Theory, TestCategory("BVT"), TestCategory("Streaming")]
        [InlineData("none")]
        [InlineData("ordered")]
        [InlineData("unknown")]
        public async Task Recovery_AfterReinitializeTracksOnlyCurrentReceiverReads(string nextRead)
        {
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken)
                .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));
            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var streamId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            var queueCache = new RecordingQueueCache();
            var adapterCache = Substitute.For<IQueueAdapterCache>();
            adapterCache.CreateQueueCache(queueId).Returns(queueCache);
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(
                Task.FromResult<IList<IBatchContainer>>([new TestBatchContainer(streamId.StreamId, null!)]),
                Task.FromResult<IList<IBatchContainer>>([new TestBatchContainer(streamId.StreamId, new EventSequenceTokenV2(200))]),
                Task.FromResult<IList<IBatchContainer>>([new TestBatchContainer(streamId.StreamId,
                    nextRead == "unknown" ? null! : new EventSequenceTokenV2(10))]));
            var agent = CreateAgent(pubSub, queueId, receiver, adapterCache);
            var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            await accessor.RegisterStream(streamId, new EventSequenceTokenV2(100), DateTime.UtcNow);
            Assert.True(await accessor.ReadFromQueue(queueId, receiver, 1000));
            Assert.True(await accessor.ReadFromQueue(queueId, receiver, 1000));
            await accessor.Shutdown();

            // A later ordered read cannot repair the unknown position within the same receiver lifetime.
            Assert.Empty(queueCache.DeliveryProgressTokens);
            await InitializeAgent(agent);
            if (nextRead != "none")
            {
                await accessor.RegisterStream(streamId, new EventSequenceTokenV2(10), DateTime.UtcNow);
                Assert.True(await accessor.ReadFromQueue(queueId, receiver, 1000));
            }

            await accessor.Shutdown();

            adapterCache.Received(2).CreateQueueCache(queueId);
            if (nextRead == "ordered")
            {
                Assert.Equal(10, Assert.IsType<EventSequenceTokenV2>(Assert.Single(queueCache.DeliveryProgressTokens)).SequenceNumber);
            }
            else if (nextRead == "unknown")
            {
                Assert.Empty(queueCache.DeliveryProgressTokens);
            }
            else
            {
                Assert.All(queueCache.DeliveryProgressTokens, token => Assert.Null(token));
            }
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Theory, TestCategory("BVT"), TestCategory("Streaming")]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task Recovery_StopsRetryingPersistentContractFailures(bool implicitSubscription, bool faultOnError)
        {
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken)
                .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));
            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var streamId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            var timeProvider = new FakeTimeProvider();
            var requiredToken = new EventSequenceTokenV2(100);
            var latestToken = new EventSequenceTokenV2(200);
            var queueCache = Substitute.For<IQueueCache>();
            queueCache.GetMaxAddCount().Returns(1000);
            queueCache.TryGetCacheCursor(Arg.Any<StreamId>(), Arg.Any<StreamSequenceToken?>())
                .Returns(QueueCacheCursorResult<IQueueCacheCursor>.FromCursor(new EmptyQueueCacheCursor()));
            var progress = new List<StreamSequenceToken?>();
            queueCache.When(cache => cache.UpdateDeliveryProgress(Arg.Any<StreamSequenceToken?>(), Arg.Any<DateTime>()))
                .Do(call => progress.Add(call.ArgAt<StreamSequenceToken?>(0)));
            var adapterCache = Substitute.For<IQueueAdapterCache>();
            adapterCache.CreateQueueCache(queueId).Returns(queueCache);
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IList<IBatchContainer>>([new TestBatchContainer(streamId.StreamId, latestToken)]));
            var failureHandler = Substitute.For<IStreamFailureHandler>();
            failureHandler.ShouldFaultSubsriptionOnError.Returns(faultOnError);
            var stopped = false;
            failureHandler.OnDeliveryFailure(default!, default!, default, default).ReturnsForAnyArgs(_ =>
            {
                stopped = true;
                return Task.CompletedTask;
            });
            var backoff = Substitute.For<IBackoffProvider>();
            backoff.Next(Arg.Any<int>()).Returns(_ => stopped ? TimeSpan.Zero : TimeSpan.FromSeconds(1));
            var agent = CreateAgent(pubSub, queueId, receiver, adapterCache, timeProvider,
                deliveryBackoff: backoff, failureHandler: failureHandler);
            var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            await accessor.RegisterStream(streamId, requiredToken, timeProvider.GetUtcNow().UtcDateTime);
            Assert.True(await accessor.ReadFromQueue(queueId, receiver, 1000));
            var observer = new RecordingConsumer();
            observer.ReleaseDelivery();
            observer.OnDelivery = () => observer.DeliveryException = observer.DeliveredTokens.Count == 1
                ? new InvalidOperationException("Transient failure delivering fresh traffic.")
                : null;
            var subscriptionId = implicitSubscription
                ? SubscriptionMarker.MarkAsImplictSubscriptionId(Guid.NewGuid())
                : SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid());
            var consumer = (await accessor.GetPubSubCache()).Single().Value.AddConsumer(
                GuidId.GetGuidId(subscriptionId), streamId, observer, null, timeProvider.GetUtcNow().UtcDateTime);
            consumer.IsRegistered = true;
            consumer.LastProcessedToken = requiredToken;
            consumer.Cursor = new InvalidMoveCursor();
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => accessor.RunConsumerCursor(consumer));
            var cursors = new List<InvalidMoveCursor>();
            queueCache.TryGetCacheCursor(Arg.Any<StreamId>(), Arg.Any<StreamSequenceToken?>()).Returns(call =>
            {
                if (!Equals(call.ArgAt<StreamSequenceToken?>(1), requiredToken))
                {
                    return QueueCacheCursorResult<IQueueCacheCursor>.FromCursor(
                        new ScriptedQueueCursor([new TestBatchContainer(streamId.StreamId, latestToken)], streamId.StreamId, null));
                }

                var cursor = new InvalidMoveCursor();
                cursors.Add(cursor);
                return QueueCacheCursorResult<IQueueCacheCursor>.FromCursor(cursor);
            });

            Assert.True(await accessor.ReadFromQueue(queueId, receiver, 1000));
            Assert.Single(cursors);
            for (var i = 0; i < 20; i++)
            {
                Assert.True(await accessor.ReadFromQueue(queueId, receiver, 1000));
            }
            Assert.Single(cursors);

            for (var i = 0; i < 10; i++)
            {
                timeProvider.Advance(TimeSpan.FromSeconds(1));
                Assert.True(await accessor.ReadFromQueue(queueId, receiver, 1000));
            }

            Assert.Equal(6, cursors.Count);
            Assert.All(cursors, cursor => Assert.True(cursor.IsDisposed));
            Assert.Null(consumer.PendingBatch);
            var shouldFault = faultOnError && !implicitSubscription;
            await failureHandler.Received(1).OnDeliveryFailure(consumer.SubscriptionId, "provider", streamId.StreamId, null);
            Assert.True(consumer.DeliveryRecovery!.Stopped);
            await pubSub.Received(shouldFault ? 1 : 0).FaultSubscription(streamId, consumer.SubscriptionId, Arg.Any<CancellationToken>());
            for (var i = 0; i < 20; i++)
            {
                timeProvider.Advance(TimeSpan.FromSeconds(1));
                Assert.True(await accessor.ReadFromQueue(queueId, receiver, 1000));
            }
            Assert.Equal(6, cursors.Count);
            if (shouldFault)
            {
                Assert.Empty(observer.DeliveredTokens);
            }
            else
            {
                Assert.Equal(new[] { latestToken, latestToken }, observer.DeliveredTokens);
            }
            await accessor.Shutdown();
            Assert.Equal(shouldFault ? latestToken : requiredToken, Assert.Single(progress));
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Theory, TestCategory("BVT"), TestCategory("Streaming")]
        [InlineData("contract", -1, false)]
        [InlineData("acquisition", -1, false)]
        [InlineData("cache-miss", -1, false)]
        [InlineData("empty", -1, false)]
        [InlineData("contract", 2, false)]
        [InlineData("empty", 2, false)]
        [InlineData("contract", -1, true)]
        public async Task Recovery_BoundsIdleRetriesAndReclaimsInactiveState(
            string failure, int maximumSeconds, bool holdFailureNotification)
        {
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken)
                .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));
            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var streamId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            var timeProvider = new FakeTimeProvider();
            var requiredToken = new EventSequenceTokenV2(100);
            var latestToken = new EventSequenceTokenV2(200);
            var cache = Substitute.For<IQueueCache>();
            var cachedBatches = new List<IBatchContainer>();
            cache.When(value => value.AddToCache(Arg.Any<IList<IBatchContainer>>()))
                .Do(call => cachedBatches.AddRange(call.Arg<IList<IBatchContainer>>()));
            cache.GetMaxAddCount().Returns(1000);
            cache.TryGetCacheCursor(Arg.Any<StreamId>(), Arg.Any<StreamSequenceToken?>())
                .Returns(QueueCacheCursorResult<IQueueCacheCursor>.FromCursor(new EmptyQueueCacheCursor()));
            var adapterCache = Substitute.For<IQueueAdapterCache>();
            adapterCache.CreateQueueCache(queueId).Returns(cache);
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            var freshTraffic = true;
            receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult<IList<IBatchContainer>>(freshTraffic
                    ? [new TestBatchContainer(streamId.StreamId, latestToken)]
                    : []));
            var failureHandler = Substitute.For<IStreamFailureHandler>();
            var failureNotification = new TaskCompletionSource();
            var reportingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            failureHandler.OnDeliveryFailure(default!, default!, default, default)
                .ReturnsForAnyArgs(_ =>
                {
                    reportingStarted.SetResult();
                    return holdFailureNotification ? failureNotification.Task : Task.CompletedTask;
                });
            var backoff = Substitute.For<IBackoffProvider>();
            backoff.Next(Arg.Any<int>()).Returns(TimeSpan.FromSeconds(1));
            var agent = CreateAgent(pubSub, queueId, receiver, adapterCache, timeProvider,
                options: new StreamPullingAgentOptions
                {
                    MaxEventDeliveryTime = maximumSeconds < 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(maximumSeconds),
                },
                deliveryBackoff: backoff, failureHandler: failureHandler);
            var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            await accessor.RegisterStream(streamId, requiredToken, timeProvider.GetUtcNow().UtcDateTime);
            Assert.True(await accessor.ReadFromQueue(queueId, receiver, 1000));
            freshTraffic = false;
            var observer = new RecordingConsumer();
            observer.ReleaseDelivery();
            var consumer = (await accessor.GetPubSubCache()).Single().Value.AddConsumer(
                GuidId.GetGuidId(SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid())),
                streamId, observer, null, timeProvider.GetUtcNow().UtcDateTime);
            consumer.IsRegistered = true;
            consumer.PendingStartToken = requiredToken;
            consumer.Cursor = new InvalidMoveCursor();
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => accessor.RunConsumerCursor(consumer));
            var acquisitions = 0;
            var cursors = new List<IQueueCacheCursor>();
            cache.TryGetCacheCursor(Arg.Any<StreamId>(), Arg.Any<StreamSequenceToken?>()).Returns(call =>
            {
                if (consumer.DeliveryRecovery is { Stopped: true })
                {
                    var start = call.ArgAt<StreamSequenceToken?>(1)!;
                    return QueueCacheCursorResult<IQueueCacheCursor>.FromCursor(new ScriptedQueueCursor(
                        cachedBatches.Where(batch => batch.SequenceToken.SequenceNumber >= start.SequenceNumber).ToList(),
                        streamId.StreamId, null));
                }
                Assert.Equal(requiredToken, call.ArgAt<StreamSequenceToken?>(1));
                acquisitions++;
                if (failure == "acquisition") throw new InvalidOperationException("Injected acquisition failure.");
                var cursor = Substitute.For<IQueueCacheCursor>();
                cursor.MoveNextWithResult().Returns(failure switch
                {
                    "cache-miss" => QueueCacheCursorMoveResult.FromCacheMiss(new("100", "200", "200")),
                    "empty" => QueueCacheCursorMoveResult.NoData,
                    _ => default,
                });
                cursors.Add(cursor);
                return QueueCacheCursorResult<IQueueCacheCursor>.FromCursor(cursor);
            });

            var expectedAttempts = maximumSeconds < 0 ? 6 : maximumSeconds;
            for (var i = 0; i < expectedAttempts - 1; i++)
            {
                Assert.False(await accessor.ReadFromQueue(queueId, receiver, 1000));
                Assert.Equal(i + 1, consumer.DeliveryRecovery!.Attempts);
                for (var j = 0; j < 10; j++)
                {
                    Assert.False(await accessor.ReadFromQueue(queueId, receiver, 1000));
                }
                Assert.Equal(i + 1, consumer.DeliveryRecovery.Attempts);
                timeProvider.Advance(TimeSpan.FromSeconds(1));
            }

            var finalAttempt = accessor.RunConsumerCursor(consumer);
            if (maximumSeconds >= 0)
            {
                if (failure == "contract")
                {
                    await Assert.ThrowsAnyAsync<InvalidOperationException>(() => finalAttempt);
                }
                else
                {
                    await finalAttempt;
                }
                timeProvider.Advance(TimeSpan.FromSeconds(1));
                finalAttempt = accessor.RunConsumerCursor(consumer);
            }
            await reportingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.True(consumer.DeliveryRecovery!.Stopped);
            Assert.Null(consumer.Cursor);
            Assert.Null(consumer.PendingBatch);
            Assert.True(consumer.HasDeliveryProgressError);
            Assert.Null(consumer.LastProcessedToken);
            Assert.All(cursors, cursor => cursor.Received(1).Dispose());
            Assert.Equal(expectedAttempts, consumer.DeliveryRecovery.Attempts);
            Assert.Equal(failure == "empty" ? 1 : expectedAttempts, acquisitions);
            if (holdFailureNotification)
            {
                Assert.False(finalAttempt.IsCompleted);
                Assert.Equal(StreamConsumerDataState.Active, consumer.State);
                for (var i = 0; i < 10; i++)
                {
                    await accessor.RunConsumerCursor(consumer);
                    Assert.False(await accessor.ReadFromQueue(queueId, receiver, 1000));
                }
                Assert.Equal(expectedAttempts, acquisitions);
                freshTraffic = true;
                latestToken = new EventSequenceTokenV2(300);
                Assert.True(await accessor.ReadFromQueue(queueId, receiver, 1000));
                latestToken = new EventSequenceTokenV2(400);
                Assert.True(await accessor.ReadFromQueue(queueId, receiver, 1000));
                freshTraffic = false;
                Assert.Equal(300, consumer.PendingContinuationToken!.SequenceNumber);
                failureNotification.SetResult();
            }
            if (failure is "contract" or "acquisition" && maximumSeconds < 0)
            {
                await Assert.ThrowsAnyAsync<InvalidOperationException>(() => finalAttempt);
            }
            else
            {
                await finalAttempt;
            }
            Assert.Equal(StreamConsumerDataState.Inactive, consumer.State);
            if (holdFailureNotification)
            {
                Assert.Equal(new long[] { 300, 400 }, observer.DeliveredTokens.Select(token => token.SequenceNumber));
            }
            await failureHandler.Received(1).OnDeliveryFailure(consumer.SubscriptionId, "provider", streamId.StreamId, null);

            timeProvider.Advance(TimeSpan.FromDays(1));
            Assert.False(await accessor.ReadFromQueue(queueId, receiver, 1000));
            Assert.Empty(await accessor.GetPubSubCache());
            await accessor.Shutdown();
            cache.DidNotReceive().UpdateDeliveryProgress(Arg.Any<StreamSequenceToken?>(), Arg.Any<DateTime>());
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Theory, TestCategory("BVT"), TestCategory("Streaming")]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Recovery_BoundsOuterRegistrationRetriesAndReleasesPins(bool failAttachment)
        {
            var registration = new TaskCompletionSource<ISet<PubSubSubscriptionState>>();
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken)
                .ReturnsForAnyArgs(registration.Task);
            var timeProvider = new FakeTimeProvider();
            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var streamId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            var requiredToken = new EventSequenceTokenV2(100);
            var pin = Substitute.For<IQueueCacheCursor>();
            var cache = Substitute.For<IQueueCache>();
            cache.GetMaxAddCount().Returns(1000);
            var acquisitions = 0;
            cache.TryGetCacheCursor(Arg.Any<StreamId>(), Arg.Any<StreamSequenceToken?>()).Returns(_ =>
            {
                acquisitions++;
                if (failAttachment && acquisitions == 1)
                {
                    return QueueCacheCursorResult<IQueueCacheCursor>.FromCursor(pin);
                }
                throw new InvalidOperationException("Injected registration cursor failure.");
            });
            var adapterCache = Substitute.For<IQueueAdapterCache>();
            adapterCache.CreateQueueCache(queueId).Returns(cache);
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(
                Task.FromResult<IList<IBatchContainer>>([new TestBatchContainer(streamId.StreamId, requiredToken)]),
                Task.FromResult<IList<IBatchContainer>>([]));
            var backoff = Substitute.For<IBackoffProvider>();
            backoff.Next(Arg.Any<int>()).Returns(TimeSpan.FromSeconds(1));
            var agent = CreateAgent(pubSub, queueId, receiver, adapterCache, timeProvider,
                options: new StreamPullingAgentOptions { MaxEventDeliveryTime = Timeout.InfiniteTimeSpan },
                deliveryBackoff: backoff);
            var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            Assert.True(await accessor.ReadFromQueue(queueId, receiver, 1000));
            var stream = (await accessor.GetPubSubCache()).Single().Value;
            var firstAttempt = stream.RegistrationTask!;
            var subscriptions = new HashSet<PubSubSubscriptionState>();
            var observer = new RecordingConsumer();
            StreamConsumerData? consumer = null;
            if (failAttachment)
            {
                consumer = stream.AddConsumer(GuidId.GetGuidId(SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid())),
                    streamId, observer, null, timeProvider.GetUtcNow().UtcDateTime);
                subscriptions.Add(new(consumer.SubscriptionId, streamId, GrainId.Create("test", "existing-subscriber")));
            }
            registration.SetResult(subscriptions);
            await firstAttempt;
            for (var attempt = 1; attempt < 6; attempt++)
            {
                Assert.Equal(attempt, stream.RegistrationRecovery!.Attempts);
                var before = acquisitions;
                for (var read = 0; read < 10; read++)
                {
                    Assert.False(await accessor.ReadFromQueue(queueId, receiver, 1000));
                }
                Assert.Equal(before, acquisitions);
                if (failAttachment)
                {
                    Assert.Same(pin, stream.RegistrationCursor);
                    pin.DidNotReceive().Dispose();
                }
                timeProvider.Advance(TimeSpan.FromSeconds(1));
                Assert.False(await accessor.ReadFromQueue(queueId, receiver, 1000));
                await stream.RegistrationTask!;
            }

            Assert.Equal(6, stream.RegistrationRecovery!.Attempts);
            Task<bool>? handshake = null;
            var handshakeResponse = new TaskCompletionSource<StreamHandshakeToken?>();
            if (consumer is not null)
            {
                var handshakeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                observer.OnHandshake = () => handshakeStarted.SetResult();
                observer.HandshakeTask = handshakeResponse.Task;
                handshake = accessor.DoHandshakeWithConsumer(consumer, requiredToken);
                await handshakeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            }
            var finalAcquisitions = acquisitions;
            Assert.False(await accessor.ReadFromQueue(queueId, receiver, 1000));
            Assert.True(stream.RegistrationRecovery.Stopped);
            Assert.Empty(await accessor.GetPubSubCache());
            Assert.Null(stream.RegistrationCursor);
            Assert.Equal(0, stream.Count);
            if (failAttachment) pin.Received(1).Dispose();
            if (handshake is not null)
            {
                cache.TryGetCacheCursor(Arg.Any<StreamId>(), Arg.Any<StreamSequenceToken?>()).Returns(_ =>
                {
                    acquisitions++;
                    return QueueCacheCursorResult<IQueueCacheCursor>.FromCursor(new EmptyQueueCacheCursor());
                });
                handshakeResponse.SetResult(StreamHandshakeToken.CreateStartToken(requiredToken));
                Assert.False(await handshake);
                Assert.True(consumer!.IsRemoved);
                Assert.Null(consumer.Cursor);
                await accessor.RunConsumerCursor(consumer);
                Assert.Empty(observer.DeliveredTokens);
            }
            timeProvider.Advance(TimeSpan.FromDays(1));
            Assert.False(await accessor.ReadFromQueue(queueId, receiver, 1000));
            Assert.Equal(finalAcquisitions, acquisitions);
            await accessor.Shutdown();
            cache.DidNotReceive().UpdateDeliveryProgress(Arg.Any<StreamSequenceToken?>(), Arg.Any<DateTime>());
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task Recovery_DoesNotRestartAnExpiredDeliveryWindow()
        {
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken)
                .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));
            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var streamId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            var timeProvider = new FakeTimeProvider();
            var processed = new EventSequenceTokenV2(90);
            var failed = new EventSequenceTokenV2(100);
            var cache = new ScriptedQueueCache();
            var adapterCache = Substitute.For<IQueueAdapterCache>();
            adapterCache.CreateQueueCache(queueId).Returns(cache);
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(
                Task.FromResult<IList<IBatchContainer>>([new TestBatchContainer(streamId.StreamId, failed)]),
                Task.FromResult<IList<IBatchContainer>>([]));
            var backoff = Substitute.For<IBackoffProvider>();
            backoff.Next(Arg.Any<int>()).Returns(_ => throw new OperationCanceledException("End the initial callback's retry wait."));
            var failureHandler = Substitute.For<IStreamFailureHandler>();
            var agent = CreateAgent(pubSub, queueId, receiver, adapterCache, timeProvider,
                options: new StreamPullingAgentOptions { MaxEventDeliveryTime = TimeSpan.FromSeconds(1) },
                deliveryBackoff: backoff, failureHandler: failureHandler);
            var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            await accessor.RegisterStream(streamId, failed, timeProvider.GetUtcNow().UtcDateTime);
            Assert.True(await accessor.ReadFromQueue(queueId, receiver, 1000));
            var observer = new RecordingConsumer
            {
                OnDelivery = () => timeProvider.Advance(TimeSpan.FromSeconds(2)),
                DeliveryException = new InvalidOperationException("Injected delivery failure."),
            };
            observer.ReleaseDelivery();
            var consumer = (await accessor.GetPubSubCache()).Single().Value.AddConsumer(
                GuidId.GetGuidId(SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid())),
                streamId, observer, null, timeProvider.GetUtcNow().UtcDateTime);
            consumer.IsRegistered = true;
            consumer.LastProcessedToken = processed;
            consumer.Cursor = cache.GetCacheCursor(streamId.StreamId, processed);
            await accessor.RunConsumerCursor(consumer);
            Assert.True(consumer.HasDeliveryProgressError);
            Assert.False(await accessor.ReadFromQueue(queueId, receiver, 1000));
            Assert.True(consumer.DeliveryRecovery!.Stopped);
            Assert.Equal(0, consumer.DeliveryRecovery.Attempts);
            Assert.Null(consumer.Cursor);
            Assert.Null(consumer.PendingBatch);
            Assert.Equal(failed, Assert.Single(observer.DeliveredTokens));
            await failureHandler.Received(1).OnDeliveryFailure(consumer.SubscriptionId, "provider", streamId.StreamId, failed);
            await failureHandler.DidNotReceive().OnDeliveryFailure(consumer.SubscriptionId, "provider", streamId.StreamId, processed);
            await accessor.Shutdown();
            Assert.Equal(processed, Assert.Single(cache.DeliveryProgressTokens));
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task Recovery_YieldsWhenTheReplacementCursorAlsoMisses()
        {
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken)
                .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));
            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var streamId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            var cache = Substitute.For<IQueueCache>();
            cache.TryGetCacheCursor(Arg.Any<StreamId>(), Arg.Any<StreamSequenceToken?>())
                .Returns(QueueCacheCursorResult<IQueueCacheCursor>.FromCursor(new EmptyQueueCacheCursor()));
            var adapterCache = Substitute.For<IQueueAdapterCache>();
            adapterCache.CreateQueueCache(queueId).Returns(cache);
            var agent = CreateAgent(pubSub, queueId, queueAdapterCache: adapterCache);
            var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            await accessor.RegisterStream(streamId, new EventSequenceTokenV2(100), DateTime.UtcNow);
            var consumer = (await accessor.GetPubSubCache()).Single().Value.AddConsumer(
                GuidId.GetGuidId(SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid())),
                streamId, new RecordingConsumer(), null, DateTime.UtcNow);
            consumer.IsRegistered = true;
            consumer.LastProcessedToken = new EventSequenceTokenV2(100);
            var moves = 0;
            var cursors = new List<IQueueCacheCursor>();
            IQueueCacheCursor CreateMissingCursor()
            {
                if (cursors.Count > 10) throw new InvalidOperationException("Recovery did not yield.");
                var cursor = Substitute.For<IQueueCacheCursor>();
                cursor.MoveNextWithResult().Returns(_ =>
                {
                    moves++;
                    return QueueCacheCursorMoveResult.FromCacheMiss(new("100", "200", "200"));
                });
                cursors.Add(cursor);
                return cursor;
            }
            cache.TryGetCacheCursorAtPosition(streamId.StreamId, StreamSubscriptionStartPosition.EarliestAvailable)
                .Returns(_ => QueueCacheCursorResult<IQueueCacheCursor>.FromCursor(CreateMissingCursor()));
            consumer.Cursor = CreateMissingCursor();
            await accessor.RunConsumerCursor(consumer);
            Assert.Equal(2, moves);
            Assert.Equal(2, cursors.Count);
            Assert.All(cursors, cursor => cursor.Received(1).Dispose());
            Assert.Null(consumer.Cursor);
            Assert.True(consumer.HasDeliveryProgressError);
            Assert.Equal(1, consumer.DeliveryRecovery!.Attempts);
            Assert.Equal(StreamConsumerDataState.Inactive, consumer.State);
            await accessor.Shutdown();
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task Shutdown_DoesNotTreatFinishedRegistrationTaskAsRegisteredProducer()
        {
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken)
                .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));
            var streamId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var queueCache = new RecordingQueueCache();
            var adapterCache = Substitute.For<IQueueAdapterCache>();
            adapterCache.CreateQueueCache(queueId).Returns(queueCache);
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(
                Task.FromResult<IList<IBatchContainer>>([new TestBatchContainer(streamId.StreamId, new EventSequenceTokenV2(200))]));
            var agent = CreateAgent(pubSub, queueId, receiver, adapterCache);
            var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            await accessor.RegisterStream(streamId, new EventSequenceTokenV2(200), DateTime.UtcNow);
            Assert.True(await accessor.ReadFromQueue(queueId, receiver, 1000));
            var stream = (await accessor.GetPubSubCache()).Single().Value;
            // A registration which exits during shutdown can finish without learning its subscribers.
            stream.StreamRegistered = false;
            stream.RegistrationTask = Task.CompletedTask;

            await accessor.Shutdown();

            Assert.Empty(queueCache.DeliveryProgressTokens);
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Theory, TestCategory("BVT"), TestCategory("Streaming")]
        [InlineData(false, 1, 100)]
        [InlineData(true, 1, 42)]
        [InlineData(false, 2, 100)]
        [InlineData(true, 2, 42)]
        public async Task Shutdown_DistinguishesEmptyCursorFromBatchWithMissingPosition(bool missingPosition, int batchSize, long expectedCheckpoint)
        {
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken)
                .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));
            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var streamId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            var cursor = Substitute.For<IQueueCacheCursor>();
            cursor.MoveNextWithResult().Returns(
                missingPosition ? QueueCacheCursorMoveResult.Success : QueueCacheCursorMoveResult.NoData,
                QueueCacheCursorMoveResult.NoData);
            cursor.GetCurrent(out _).Returns(new TestBatchContainer(streamId.StreamId, null!));
            var queueCache = Substitute.For<IQueueCache>();
            queueCache.TryGetCacheCursor(Arg.Any<StreamId>(), Arg.Any<StreamSequenceToken?>())
                .Returns(QueueCacheCursorResult<IQueueCacheCursor>.FromCursor(cursor));
            var adapterCache = Substitute.For<IQueueAdapterCache>();
            adapterCache.CreateQueueCache(queueId).Returns(queueCache);
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(
                Task.FromResult<IList<IBatchContainer>>([new TestBatchContainer(streamId.StreamId, new EventSequenceTokenV2(100))]));
            var agent = CreateAgent(pubSub, queueId, receiver, adapterCache,
                options: new StreamPullingAgentOptions { BatchContainerBatchSize = batchSize });
            var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            await accessor.RegisterStream(streamId, new EventSequenceTokenV2(0), DateTime.UtcNow);
            var observer = new RecordingConsumer();
            var consumer = (await accessor.GetPubSubCache()).Single().Value.AddConsumer(
                GuidId.GetGuidId(Guid.NewGuid()), streamId, observer, null, DateTime.UtcNow);
            consumer.IsRegistered = true;
            consumer.Cursor = cursor;
            consumer.IsCaughtUp = true;
            consumer.LastProcessedToken = missingPosition ? new EventSequenceTokenV2(42) : null;

            Assert.True(await accessor.ReadFromQueue(queueId, receiver, 1000));
            Assert.Equal(!missingPosition, consumer.IsCaughtUp);
            Assert.Empty(observer.DeliveredTokens);
            await accessor.Shutdown();

            queueCache.Received(1).UpdateDeliveryProgress(
                Arg.Is<StreamSequenceToken>(token => token.SequenceNumber == expectedCheckpoint), Arg.Any<DateTime>());
        }

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task Shutdown_PushesEarliestDeliveryProgressTokenToCache()
        {
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken)
                .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));

            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IList<IBatchContainer>>(new List<IBatchContainer>()));
            receiver.Shutdown(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

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
            pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken)
                .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));

            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IList<IBatchContainer>>(new List<IBatchContainer>()));
            receiver.Shutdown(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

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
        [Theory, TestCategory("BVT"), TestCategory("Streaming")]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Shutdown_PreservesCheckpointForIncompatibleTokens(bool providerTokenFirst)
        {
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken)
                .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));

            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            receiver.Shutdown(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
            var queueCache = new RecordingQueueCache();
            var queueAdapterCache = Substitute.For<IQueueAdapterCache>();
            queueAdapterCache.CreateQueueCache(queueId).Returns(queueCache);
            var streamId = new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid()));
            var agent = CreateAgent(pubSub, queueId, receiver, queueAdapterCache);
            var accessor = (PersistentStreamPullingAgent.ITestAccessor)agent;
            await InitializeAgent(agent);
            await accessor.RegisterStream(streamId, new EventSequenceTokenV2(1), DateTime.UtcNow);
            var streamData = (await accessor.GetPubSubCache()).Single().Value;
            Assert.Null(streamData.RegistrationTask);
            queueCache.ClearDeliveryProgress();

            StreamSequenceToken[] tokens = [new EventSequenceTokenV2(10), new IsolatedProviderToken(20)];
            if (providerTokenFirst)
            {
                Array.Reverse(tokens);
            }

            foreach (var token in tokens)
            {
                var consumer = streamData.AddConsumer(
                    GuidId.GetGuidId(Guid.NewGuid()),
                    streamId,
                    streamConsumer: null!,
                    filterData: null,
                    now: DateTime.UtcNow);
                consumer.IsRegistered = true;
                consumer.LastProcessedToken = token;
            }

            await accessor.Shutdown();

            Assert.Empty(queueCache.DeliveryProgressTokens);
            Assert.Equal(0, queueCache.DeliveryProgressCallCount);
            await receiver.Received(1).Shutdown(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
            await pubSub.Received(1).UnregisterProducer(streamId, Arg.Any<GrainId>(), Arg.Any<CancellationToken>());
            Assert.Empty(await accessor.GetPubSubCache());
        }

        private sealed class IsolatedProviderToken(long sequenceNumber) : EventSequenceTokenV2(sequenceNumber);

        [TestSuite("BVT")]
        [TestProvider("None")]
        [TestArea("Streaming")]
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task Shutdown_SkipsDeliveryProgressForPendingRegistrations()
        {
            var registration = new TaskCompletionSource<ISet<PubSubSubscriptionState>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken)
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
            receiver.Shutdown(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(_ =>
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
            await testAccessor.RunQueuePump(queueId, TestContext.Current.CancellationToken);

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
        [Theory, TestCategory("BVT"), TestCategory("Streaming")]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public async Task Shutdown_SkipsDeliveryProgressForUnregisteredOrReattachingConsumer(int pendingHandshakes)
        {
            var pubSub = Substitute.For<IStreamPubSub>();
            pubSub.RegisterProducer(default, default, TestContext.Current.CancellationToken)
                .ReturnsForAnyArgs(Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>()));

            var queueId = QueueId.GetQueueId("queue", 0u, 0u);
            var receiver = Substitute.For<IQueueAdapterReceiver>();
            receiver.GetQueueMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IList<IBatchContainer>>(new List<IBatchContainer>()));
            receiver.Shutdown(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

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
            unregisteredConsumer.IsRegistered = pendingHandshakes > 0;
            unregisteredConsumer.PendingHandshakes = pendingHandshakes;
            unregisteredConsumer.IsCaughtUp = pendingHandshakes > 0;
            unregisteredConsumer.LastProcessedToken = pendingHandshakes > 0 ? new EventSequenceTokenV2(50) : null;

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
            receiver.Shutdown(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

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

#pragma warning restore CS0618
