using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Core;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Serialization.Serializers;
using Orleans.Storage;
using Orleans.Streams.Core;

#nullable enable
namespace Orleans.Streams
{
    internal sealed partial class PubSubGrainStateStorageFactory
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<PubSubGrainStateStorageFactory> _logger;

        public PubSubGrainStateStorageFactory(IServiceProvider serviceProvider, ILoggerFactory loggerFactory)
        {
            _serviceProvider = serviceProvider;
            _logger = loggerFactory.CreateLogger<PubSubGrainStateStorageFactory>();
        }

        public StateStorageBridge<PubSubGrainState> GetStorage(PubSubRendezvousGrain grain)
        {
            var span = grain.GrainId.Key.AsSpan();
            var i = span.IndexOf((byte)'/');
            if (i < 0)
            {
                throw new ArgumentException($"Unable to parse \"{grain.GrainId.Key}\" as a stream id");
            }

            var providerName = Encoding.UTF8.GetString(span[..i]);

            LogDebugTryingToFindStorageProvider(providerName);

            var storage = _serviceProvider.GetKeyedService<IGrainStorage>(providerName);
            if (storage == null)
            {
                LogDebugFallbackToStorageProvider(ProviderConstants.DEFAULT_PUBSUB_PROVIDER_NAME);

                storage = _serviceProvider.GetRequiredKeyedService<IGrainStorage>(ProviderConstants.DEFAULT_PUBSUB_PROVIDER_NAME);
            }

            return new(nameof(PubSubRendezvousGrain), grain.GrainContext, storage);
        }

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Trying to find storage provider {ProviderName}"
        )]
        private partial void LogDebugTryingToFindStorageProvider(string providerName);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Fallback to storage provider {ProviderName}"
        )]
        private partial void LogDebugFallbackToStorageProvider(string providerName);
    }

    [Serializable]
    [GenerateSerializer]
    internal sealed class PubSubGrainState
    {
        [Id(0)]
        public HashSet<PubSubPublisherState> Producers { get; set; } = new HashSet<PubSubPublisherState>();
        [Id(1)]
        public HashSet<PubSubSubscriptionState> Consumers { get; set; } = new HashSet<PubSubSubscriptionState>();
    }

    [GrainType("pubsubrendezvous")]
    internal sealed partial class PubSubRendezvousGrain : Grain, IPubSubRendezvousGrain, IGrainMigrationParticipant
    {
        private readonly ILogger _logger;
        private const bool DEBUG_PUB_SUB = false;

        private readonly PubSubGrainStateStorageFactory _storageFactory;
        private readonly StateStorageBridge<PubSubGrainState> _storage;

        private PubSubGrainState State => _storage.State;

        public PubSubRendezvousGrain(PubSubGrainStateStorageFactory storageFactory, ILogger<PubSubRendezvousGrain> logger)
        {
            _storageFactory = storageFactory;
            _logger = logger;
            _storage = _storageFactory.GetStorage(this);
        }

        public override async Task OnActivateAsync(CancellationToken cancellationToken)
        {
            await ReadStateAsync();
            LogPubSubCounts("OnActivateAsync");
        }

        public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            LogPubSubCounts("OnDeactivateAsync");
            return Task.CompletedTask;
        }

        public async Task<ISet<PubSubSubscriptionState>> RegisterProducer(QualifiedStreamId streamId, GrainId streamProducer)
        {
            StreamInstruments.PubSubProducersAdded.Add(1);

            try
            {
                var publisherState = new PubSubPublisherState(streamId, streamProducer);
                State.Producers.Add(publisherState);
                LogPubSubCounts("RegisterProducer {0}", streamProducer);
                await WriteStateAsync();
                StreamInstruments.PubSubProducersTotal.Add(1);
            }
            catch (Exception exc)
            {
                LogErrorRegisterProducer(streamId, streamProducer, exc);

                // Corrupted state, deactivate grain.
                DeactivateOnIdle();
                throw;
            }
            return State.Consumers.Where(c => !c.IsFaulted).ToSet();
        }

        public async Task UnregisterProducer(QualifiedStreamId streamId, GrainId streamProducer)
        {
            StreamInstruments.PubSubProducersRemoved.Add(1);
            try
            {
                int numRemoved = State.Producers.RemoveWhere(s => s.Equals(streamId, streamProducer));
                LogPubSubCounts("UnregisterProducer {0} NumRemoved={1}", streamProducer, numRemoved);

                if (numRemoved > 0)
                {
                    Task updateStorageTask = State.Producers.Count == 0 && State.Consumers.Count == 0
                        ? ClearStateAsync() //State contains no producers or consumers, remove it from storage
                        : WriteStateAsync();
                    await updateStorageTask;
                }
                StreamInstruments.PubSubProducersTotal.Add(-numRemoved);
            }
            catch (Exception exc)
            {
                LogErrorUnregisterProducer(streamId, streamProducer, exc);

                // Corrupted state, deactivate grain.
                DeactivateOnIdle();
                throw;
            }
            if (State.Producers.Count == 0 && State.Consumers.Count == 0)
            {
                DeactivateOnIdle(); // No producers or consumers left now, so flag ourselves to expedite Deactivation
            }
        }

        public async Task RegisterConsumer(
            GuidId subscriptionId,
            QualifiedStreamId streamId,
            GrainId streamConsumer,
            string filterData)
        {
            StreamInstruments.PubSubConsumersAdded.Add(1);
            var pubSubState = State.Consumers.FirstOrDefault(s => s.Equals(subscriptionId));
            if (pubSubState != null && pubSubState.IsFaulted)
                throw new FaultedSubscriptionException(subscriptionId, streamId);
            try
            {
                if (pubSubState == null)
                {
                    pubSubState = new PubSubSubscriptionState(subscriptionId, streamId, streamConsumer);
                    State.Consumers.Add(pubSubState);
                }

                if (!string.IsNullOrWhiteSpace(filterData))
                    pubSubState.AddFilter(filterData);

                LogPubSubCounts("RegisterConsumer {0}", streamConsumer);
                await WriteStateAsync();
                StreamInstruments.PubSubConsumersTotal.Add(1);
            }
            catch (Exception exc)
            {
                LogErrorRegisterConsumer(streamId, subscriptionId, streamConsumer, exc);

                // Corrupted state, deactivate grain.
                DeactivateOnIdle();
                throw;
            }

            int numProducers = State.Producers.Count;
            if (numProducers <= 0)
                return;

            LogDebugNotifyProducersOfNewConsumer(numProducers, streamConsumer, new(State.Producers));

            // Notify producers about a new streamConsumer.
            var tasks = new List<Task>();
            var producers = State.Producers.ToList();
            int initialProducerCount = producers.Count;
            try
            {
                foreach (PubSubPublisherState producerState in producers)
                {
                    tasks.Add(ExecuteProducerTask(producerState, p => p.AddSubscriber(subscriptionId, streamId, streamConsumer, filterData)));
                }

                Exception? exception = null;
                try
                {
                    await Task.WhenAll(tasks);
                }
                catch (Exception exc)
                {
                    exception = exc;
                }

                // if the number of producers has been changed, resave state.
                if (State.Producers.Count != initialProducerCount)
                {
                    await WriteStateAsync();
                    StreamInstruments.PubSubConsumersTotal.Add(-(initialProducerCount - State.Producers.Count));
                }

                if (exception != null)
                {
                    ExceptionDispatchInfo.Capture(exception).Throw();
                }
            }
            catch (Exception exc)
            {
                LogErrorRegisterConsumerFailed(
                    streamId,
                    subscriptionId,
                    streamConsumer,
                    exc);

                // Corrupted state, deactivate grain.
                DeactivateOnIdle();
                throw;
            }
        }

        private void RemoveProducer(PubSubPublisherState producer)
        {
            LogWarningProducerIsDead(producer, producer.Stream);

            State.Producers.Remove(producer);
        }

        public async Task UnregisterConsumer(GuidId subscriptionId, QualifiedStreamId streamId)
        {
            StreamInstruments.PubSubConsumersRemoved.Add(1);

            try
            {
                int numRemoved = State.Consumers.RemoveWhere(c => c.Equals(subscriptionId));

                LogPubSubCounts("UnregisterSubscription {0} NumRemoved={1}", subscriptionId, numRemoved);

                if (await TryClearState())
                {
                    // If state was cleared expedite Deactivation
                    DeactivateOnIdle();
                }
                else
                {
                    if (numRemoved != 0)
                    {
                        await WriteStateAsync();
                    }
                    await NotifyProducersOfRemovedSubscription(subscriptionId, streamId);
                }
                StreamInstruments.PubSubConsumersTotal.Add(-numRemoved);
            }
            catch (Exception exc)
            {
                LogErrorUnregisterConsumer(streamId, subscriptionId, exc);

                // Corrupted state, deactivate grain.
                DeactivateOnIdle();
                throw;
            }
        }

        public Task<int> ProducerCount(QualifiedStreamId streamId)
        {
            return Task.FromResult(State.Producers.Count);
        }

        public Task<int> ConsumerCount(QualifiedStreamId streamId)
        {
            return Task.FromResult(GetConsumersForStream(streamId).Length);
        }

        public Task<PubSubSubscriptionState[]> DiagGetConsumers(QualifiedStreamId streamId)
        {
            return Task.FromResult(GetConsumersForStream(streamId));
        }

        private PubSubSubscriptionState[] GetConsumersForStream(QualifiedStreamId streamId)
        {
            return State.Consumers.Where(c => !c.IsFaulted && c.Stream.Equals(streamId)).ToArray();
        }

        private void LogPubSubCounts(string fmt, params object[] args)
        {
            if (_logger.IsEnabled(LogLevel.Debug) || DEBUG_PUB_SUB)
            {
                int numProducers = 0;
                int numConsumers = 0;
                if (State?.Producers != null)
                    numProducers = State.Producers.Count;
                if (State?.Consumers != null)
                    numConsumers = State.Consumers.Count;

                string when = args != null && args.Length != 0 ? string.Format(fmt, args) : fmt;
                _logger.LogDebug("{When}. Now have total of {ProducerCount} producers and {ConsumerCount} consumers. All Consumers = {Consumers}, All Producers = {Producers}",
                    when, numProducers, numConsumers, Utils.EnumerableToString(State?.Consumers), Utils.EnumerableToString(State?.Producers));
            }
        }

        // Check that what we have cached locally matches what is in the persistent table.
        public async Task Validate()
        {
            var captureProducers = State.Producers;
            var captureConsumers = State.Consumers;

            await ReadStateAsync();

            if (captureProducers.Count != State.Producers.Count)
            {
                throw new OrleansException(
                    $"State mismatch between PubSubRendezvousGrain and its persistent state. captureProducers.Count={captureProducers.Count}, State.Producers.Count={State.Producers.Count}");
            }

            if (captureProducers.Any(producer => !State.Producers.Contains(producer)))
            {
                throw new OrleansException(
                    $"State mismatch between PubSubRendezvousGrain and its persistent state. captureProducers={Utils.EnumerableToString(captureProducers)}, State.Producers={Utils.EnumerableToString(State.Producers)}");
            }

            if (captureConsumers.Count != State.Consumers.Count)
            {
                LogPubSubCounts("Validate: Consumer count mismatch");
                throw new OrleansException(
                    $"State mismatch between PubSubRendezvousGrain and its persistent state. captureConsumers.Count={captureConsumers.Count}, State.Consumers.Count={State.Consumers.Count}");
            }

            if (captureConsumers.Any(consumer => !State.Consumers.Contains(consumer)))
            {
                throw new OrleansException(
                    $"State mismatch between PubSubRendezvousGrain and its persistent state. captureConsumers={Utils.EnumerableToString(captureConsumers)}, State.Consumers={Utils.EnumerableToString(State.Consumers)}");
            }
        }

        public Task<List<StreamSubscription>> GetAllSubscriptions(QualifiedStreamId streamId, GrainId streamConsumer)
        {
            if (streamConsumer != default)
            {
                List<StreamSubscription> subscriptions =
                    State.Consumers.Where(c => !c.IsFaulted && c.Consumer.Equals(streamConsumer))
                        .Select(
                            c =>
                                new StreamSubscription(c.SubscriptionId.Guid, streamId.ProviderName, streamId,
                                    streamConsumer)).ToList();
                return Task.FromResult(subscriptions);
            }
            else
            {
                List<StreamSubscription> subscriptions =
                    State.Consumers.Where(c => !c.IsFaulted)
                        .Select(
                            c =>
                                new StreamSubscription(c.SubscriptionId.Guid, streamId.ProviderName, streamId,
                                    c.Consumer)).ToList();
                return Task.FromResult(subscriptions);
            }

        }

        public async Task FaultSubscription(GuidId subscriptionId)
        {
            var pubSubState = State.Consumers.FirstOrDefault(s => s.Equals(subscriptionId));
            if (pubSubState == null)
            {
                return;
            }
            try
            {
                pubSubState.Fault();
                LogDebugSettingSubscriptionToFaulted(subscriptionId);

                await WriteStateAsync();
                await NotifyProducersOfRemovedSubscription(pubSubState.SubscriptionId, pubSubState.Stream);
            }
            catch (Exception exc)
            {
                LogErrorSetSubscriptionToFaulted(subscriptionId, exc);

                // Corrupted state, deactivate grain.
                DeactivateOnIdle();
                throw;
            }
        }

        private async Task NotifyProducersOfRemovedSubscription(GuidId subscriptionId, QualifiedStreamId streamId)
        {
            int numProducersBeforeNotify = State.Producers.Count;
            if (numProducersBeforeNotify > 0)
            {
                LogDebugNotifyProducersOfRemovedConsumer(numProducersBeforeNotify);

                // Notify producers about unregistered consumer.
                List<Task> tasks = State.Producers
                    .Select(producerState => ExecuteProducerTask(producerState, p => p.RemoveSubscriber(subscriptionId, streamId)))
                    .ToList();
                await Task.WhenAll(tasks);
                //if producers got removed
                if (State.Producers.Count < numProducersBeforeNotify)
                    await WriteStateAsync();
            }
        }

        /// <summary>
        /// Try clear state will only clear the state if there are no producers or consumers.
        /// </summary>
        /// <returns></returns>
        private async Task<bool> TryClearState()
        {
            if (State.Producers.Count == 0 && State.Consumers.Count == 0) // + we already know that numProducers == 0 from previous if-clause
            {
                await ClearStateAsync(); //State contains no producers or consumers, remove it from storage
                return true;
            }
            return false;
        }

        private async Task ExecuteProducerTask(PubSubPublisherState producer, Func<IStreamProducerExtension, Task> producerTask)
        {
            try
            {
                var extension = GrainFactory
                    .GetGrain(producer.Producer)
                    .AsReference<IStreamProducerExtension>();
                await producerTask(extension);
            }
            catch (GrainExtensionNotInstalledException)
            {
                RemoveProducer(producer);
            }
            catch (ClientNotAvailableException)
            {
                RemoveProducer(producer);
            }
            catch (OrleansMessageRejectionException)
            {
                // if producer is a system target on and unavailable silo, remove it.
                if (producer.Producer.IsSystemTarget())
                {
                    RemoveProducer(producer);
                }
                else // otherwise, throw
                {
                    throw;
                }
            }
        }

        private Task ReadStateAsync() => _storage.ReadStateAsync();
        private Task WriteStateAsync() => _storage.WriteStateAsync();
        private Task ClearStateAsync() => _storage.ClearStateAsync();
        void IGrainMigrationParticipant.OnDehydrate(IDehydrationContext dehydrationContext) => _storage.OnDehydrate(dehydrationContext);
        void IGrainMigrationParticipant.OnRehydrate(IRehydrationContext rehydrationContext) => _storage.OnRehydrate(rehydrationContext);

        [LoggerMessage(
            Level = LogLevel.Error,
            EventId = (int)ErrorCode.Stream_RegisterProducerFailed,
            Message = "Failed to register a stream producer. Stream: {StreamId}, Producer: {StreamProducer}"
        )]
        private partial void LogErrorRegisterProducer(QualifiedStreamId streamId, GrainId streamProducer, Exception exception);

        [LoggerMessage(
            Level = LogLevel.Error,
            EventId = (int)ErrorCode.Stream_UnregisterProducerFailed,
            Message = "Failed to unregister a stream producer. Stream: {StreamId}, Producer: {StreamProducer}"
        )]
        private partial void LogErrorUnregisterProducer(QualifiedStreamId streamId, GrainId streamProducer, Exception exception);

        [LoggerMessage(
            Level = LogLevel.Error,
            EventId = (int)ErrorCode.Stream_RegisterConsumerFailed,
            Message = "Failed to register a stream consumer. Stream: {StreamId}, SubscriptionId {SubscriptionId}, Consumer: {StreamConsumer}"
        )]
        private partial void LogErrorRegisterConsumer(QualifiedStreamId streamId, GuidId subscriptionId, GrainId streamConsumer, Exception exception);

        private readonly struct ProducersLogRecord(HashSet<PubSubPublisherState> producers)
        {
            public override string ToString() => Utils.EnumerableToString(producers);
        }

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Notifying {ProducerCount} existing producer(s) about new consumer {Consumer}. Producers={Producers}"
        )]
        private partial void LogDebugNotifyProducersOfNewConsumer(int producerCount, GrainId consumer, ProducersLogRecord producers);

        [LoggerMessage(
            Level = LogLevel.Error,
            EventId = (int)ErrorCode.Stream_RegisterConsumerFailed,
            Message = "Failed to update producers while registering a stream consumer. Stream: {StreamId}, SubscriptionId {SubscriptionId}, Consumer: {StreamConsumer}"
        )]
        private partial void LogErrorRegisterConsumerFailed(QualifiedStreamId streamId, GuidId subscriptionId, GrainId streamConsumer, Exception exception);

        [LoggerMessage(
            Level = LogLevel.Warning,
            EventId = (int)ErrorCode.Stream_ProducerIsDead,
            Message = "Producer {Producer} on stream {StreamId} is no longer active - permanently removing producer."
        )]
        private partial void LogWarningProducerIsDead(PubSubPublisherState producer, QualifiedStreamId streamId);

        [LoggerMessage(
            Level = LogLevel.Error,
            EventId = (int)ErrorCode.Stream_UnregisterConsumerFailed,
            Message = "Failed to unregister a stream consumer. Stream: {StreamId}, SubscriptionId {SubscriptionId}"
        )]
        private partial void LogErrorUnregisterConsumer(QualifiedStreamId streamId, GuidId subscriptionId, Exception exception);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Setting subscription {SubscriptionId} to a faulted state."
        )]
        private partial void LogDebugSettingSubscriptionToFaulted(GuidId subscriptionId);

        [LoggerMessage(
            Level = LogLevel.Error,
            EventId = (int)ErrorCode.Stream_SetSubscriptionToFaultedFailed,
            Message = "Failed to set subscription state to faulted. SubscriptionId {SubscriptionId}"
        )]
        private partial void LogErrorSetSubscriptionToFaulted(GuidId subscriptionId, Exception exception);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Notifying {ProducerCountBeforeNotify} existing producers about unregistered consumer."
        )]
        private partial void LogDebugNotifyProducersOfRemovedConsumer(int producerCountBeforeNotify);
    }
}
