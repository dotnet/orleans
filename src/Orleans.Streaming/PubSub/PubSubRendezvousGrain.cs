using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Core;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.Streams.Core;

namespace Orleans.Streams
{
    internal class PubSubGrainStateStorageFactory
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILoggerFactory _loggerFactory;

        public PubSubGrainStateStorageFactory(IServiceProvider serviceProvider, ILoggerFactory loggerFactory)
        {
            _serviceProvider = serviceProvider;
            _loggerFactory = loggerFactory;
        }

        public IStorage<PubSubGrainState> GetStorage(PubSubRendezvousGrain grain)
        {
            var logger = _loggerFactory.CreateLogger<PubSubGrainStateStorageFactory>();
            var streamId = InternalStreamId.Parse(grain.GetGrainId().Key.ToString());

            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug($"Trying to find storage provider {streamId.ProviderName}");

            var storage = _serviceProvider.GetServiceByName<IGrainStorage>(streamId.ProviderName);
            if (storage == null)
            {
                if (logger.IsEnabled(LogLevel.Debug))
                    logger.LogDebug($"Fallback to storage provider {ProviderConstants.DEFAULT_PUBSUB_PROVIDER_NAME}");

                storage = _serviceProvider.GetRequiredServiceByName<IGrainStorage>(ProviderConstants.DEFAULT_PUBSUB_PROVIDER_NAME);
            }
            return new StateStorageBridge<PubSubGrainState>(typeof(PubSubRendezvousGrain).FullName, grain.GrainReference, storage, _loggerFactory);
        }
    }

    [Serializable]
    [GenerateSerializer]
    internal class PubSubGrainState
    {
        [Id(1)]
        public HashSet<PubSubPublisherState> Producers { get; set; } = new HashSet<PubSubPublisherState>();
        [Id(2)]
        public HashSet<PubSubSubscriptionState> Consumers { get; set; } = new HashSet<PubSubSubscriptionState>();
    }

    [GrainType("pubsubrendezvous")]
    internal class PubSubRendezvousGrain : Grain, IPubSubRendezvousGrain
    {
        private readonly ILogger logger;
        private const bool DEBUG_PUB_SUB = false;

        private static readonly CounterStatistic counterProducersAdded;
        private static readonly CounterStatistic counterProducersRemoved;
        private static readonly CounterStatistic counterProducersTotal;
        private static readonly CounterStatistic counterConsumersAdded;
        private static readonly CounterStatistic counterConsumersRemoved;
        private static readonly CounterStatistic counterConsumersTotal;

        private readonly PubSubGrainStateStorageFactory _storageFactory;
        private IStorage<PubSubGrainState> _storage;

        private PubSubGrainState State => _storage.State;

        static PubSubRendezvousGrain()
        {
            counterProducersAdded   = CounterStatistic.FindOrCreate(StatisticNames.STREAMS_PUBSUB_PRODUCERS_ADDED);
            counterProducersRemoved = CounterStatistic.FindOrCreate(StatisticNames.STREAMS_PUBSUB_PRODUCERS_REMOVED);
            counterProducersTotal   = CounterStatistic.FindOrCreate(StatisticNames.STREAMS_PUBSUB_PRODUCERS_TOTAL);
            counterConsumersAdded   = CounterStatistic.FindOrCreate(StatisticNames.STREAMS_PUBSUB_CONSUMERS_ADDED);
            counterConsumersRemoved = CounterStatistic.FindOrCreate(StatisticNames.STREAMS_PUBSUB_CONSUMERS_REMOVED);
            counterConsumersTotal   = CounterStatistic.FindOrCreate(StatisticNames.STREAMS_PUBSUB_CONSUMERS_TOTAL);
        }

        public PubSubRendezvousGrain(PubSubGrainStateStorageFactory storageFactory, ILogger<PubSubRendezvousGrain> logger)
        {
            _storageFactory = storageFactory;
            this.logger = logger;
        }

        public override async Task OnActivateAsync(CancellationToken cancellationToken)
        {
            LogPubSubCounts("OnActivateAsync");
            _storage = _storageFactory.GetStorage(this);
            await ReadStateAsync();
        }

        public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            LogPubSubCounts("OnDeactivateAsync");
            return Task.CompletedTask;
        }

        public async Task<ISet<PubSubSubscriptionState>> RegisterProducer(InternalStreamId streamId, IStreamProducerExtension streamProducer)
        {
            counterProducersAdded.Increment();

            try
            {
                var publisherState = new PubSubPublisherState(streamId, streamProducer);
                State.Producers.Add(publisherState);
                LogPubSubCounts("RegisterProducer {0}", streamProducer);
                await WriteStateAsync();
                counterProducersTotal.Increment();
            }
            catch (Exception exc)
            {
                logger.Error(ErrorCode.Stream_RegisterProducerFailed, $"Failed to register a stream producer.  Stream: {streamId}, Producer: {streamProducer}", exc);
                // Corrupted state, deactivate grain.
                DeactivateOnIdle();
                throw;
            }
            return State.Consumers.Where(c => !c.IsFaulted).ToSet();
        }

        public async Task UnregisterProducer(InternalStreamId streamId, IStreamProducerExtension streamProducer)
        {
            counterProducersRemoved.Increment();
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
                counterProducersTotal.DecrementBy(numRemoved);
            }
            catch (Exception exc)
            {
                logger.Error(ErrorCode.Stream_UnegisterProducerFailed,
                    $"Failed to unregister a stream producer.  Stream: {streamId}, Producer: {streamProducer}", exc);
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
            InternalStreamId streamId,
            IStreamConsumerExtension streamConsumer,
            string filterData)
        {
            counterConsumersAdded.Increment();
            PubSubSubscriptionState pubSubState = State.Consumers.FirstOrDefault(s => s.Equals(subscriptionId));
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
                counterConsumersTotal.Increment();
            }
            catch (Exception exc)
            {
                logger.Error(ErrorCode.Stream_RegisterConsumerFailed,
                    $"Failed to register a stream consumer.  Stream: {streamId}, SubscriptionId {subscriptionId}, Consumer: {streamConsumer}", exc);
                // Corrupted state, deactivate grain.
                DeactivateOnIdle();
                throw;
            }

            int numProducers = State.Producers.Count;
            if (numProducers <= 0)
                return;

            if (logger.IsEnabled(LogLevel.Debug))
                logger.Debug("Notifying {0} existing producer(s) about new consumer {1}. Producers={2}",
                    numProducers, streamConsumer, Utils.EnumerableToString(State.Producers));

            // Notify producers about a new streamConsumer.
            var tasks = new List<Task>();
            var producers = State.Producers.ToList();
            int initialProducerCount = producers.Count;
            try
            {
                foreach (PubSubPublisherState producerState in producers)
                {
                    tasks.Add(ExecuteProducerTask(producerState, producerState.Producer.AddSubscriber(subscriptionId, streamId, streamConsumer, filterData)));
                }

                Exception exception = null;
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
                    counterConsumersTotal.DecrementBy(initialProducerCount - State.Producers.Count);
                }

                if (exception != null)
                {
                    throw exception;
                }
            }
            catch (Exception exc)
            {
                logger.Error(ErrorCode.Stream_RegisterConsumerFailed,
                    $"Failed to update producers while register a stream consumer.  Stream: {streamId}, SubscriptionId {subscriptionId}, Consumer: {streamConsumer}", exc);
                // Corrupted state, deactivate grain.
                DeactivateOnIdle();
                throw;
            }
        }

        private void RemoveProducer(PubSubPublisherState producer)
        {
            logger.Warn(ErrorCode.Stream_ProducerIsDead,
                "Producer {0} on stream {1} is no longer active - permanently removing producer.",
                producer, producer.Stream);

            State.Producers.Remove(producer);
        }

        public async Task UnregisterConsumer(GuidId subscriptionId, InternalStreamId streamId)
        {
            counterConsumersRemoved.Increment();

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
                counterConsumersTotal.DecrementBy(numRemoved);
            }
            catch (Exception exc)
            {
                logger.Error(ErrorCode.Stream_UnregisterConsumerFailed,
                    $"Failed to unregister a stream consumer.  Stream: {streamId}, SubscriptionId {subscriptionId}", exc);
                // Corrupted state, deactivate grain.
                DeactivateOnIdle();
                throw;
            }
        }

        public Task<int> ProducerCount(InternalStreamId streamId)
        {
            return Task.FromResult(State.Producers.Count);
        }

        public Task<int> ConsumerCount(InternalStreamId streamId)
        {
            return Task.FromResult(GetConsumersForStream(streamId).Length);
        }

        public Task<PubSubSubscriptionState[]> DiagGetConsumers(InternalStreamId streamId)
        {
            return Task.FromResult(GetConsumersForStream(streamId));
        }

        private PubSubSubscriptionState[] GetConsumersForStream(InternalStreamId streamId)
        {
            return State.Consumers.Where(c => !c.IsFaulted && c.Stream.Equals(streamId)).ToArray();
        }

        private void LogPubSubCounts(string fmt, params object[] args)
        {
            if (logger.IsEnabled(LogLevel.Debug) || DEBUG_PUB_SUB)
            {
                int numProducers = 0;
                int numConsumers = 0;
                if (State?.Producers != null)
                    numProducers = State.Producers.Count;
                if (State?.Consumers != null)
                    numConsumers = State.Consumers.Count;

                string when = args != null && args.Length != 0 ? string.Format(fmt, args) : fmt;
                logger.Info("{0}. Now have total of {1} producers and {2} consumers. All Consumers = {3}, All Producers = {4}",
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

        public Task<List<StreamSubscription>> GetAllSubscriptions(InternalStreamId streamId, IStreamConsumerExtension streamConsumer)
        {
            var grainRef = streamConsumer as GrainReference;
            if (grainRef != null)
            {
                List<StreamSubscription> subscriptions =
                    State.Consumers.Where(c => !c.IsFaulted && c.Consumer.Equals(streamConsumer))
                        .Select(
                            c =>
                                new StreamSubscription(c.SubscriptionId.Guid, streamId.ProviderName, streamId,
                                    grainRef.GrainId)).ToList();
                return Task.FromResult(subscriptions);
            }
            else
            {
                List<StreamSubscription> subscriptions =
                    State.Consumers.Where(c => !c.IsFaulted)
                        .Select(
                            c =>
                                new StreamSubscription(c.SubscriptionId.Guid, streamId.ProviderName, streamId,
                                    c.consumerReference.GrainId)).ToList();
                return Task.FromResult(subscriptions);
            }

        }

        public async Task FaultSubscription(GuidId subscriptionId)
        {
            PubSubSubscriptionState pubSubState = State.Consumers.FirstOrDefault(s => s.Equals(subscriptionId));
            if (pubSubState == null)
            {
                return;
            }
            try
            {
                pubSubState.Fault();
                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("Setting subscription {0} to a faulted state.", subscriptionId.Guid);

                await WriteStateAsync();
                await NotifyProducersOfRemovedSubscription(pubSubState.SubscriptionId, pubSubState.Stream);
            }
            catch (Exception exc)
            {
                logger.Error(ErrorCode.Stream_SetSubscriptionToFaultedFailed,
                    $"Failed to set subscription state to faulted.  SubscriptionId {subscriptionId}", exc);
                // Corrupted state, deactivate grain.
                DeactivateOnIdle();
                throw;
            }
        }

        private async Task NotifyProducersOfRemovedSubscription(GuidId subscriptionId, InternalStreamId streamId)
        {
            int numProducersBeforeNotify = State.Producers.Count;
            if (numProducersBeforeNotify > 0)
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("Notifying {0} existing producers about unregistered consumer.", numProducersBeforeNotify);

                // Notify producers about unregistered consumer.
                List<Task> tasks = State.Producers
                    .Select(producerState => ExecuteProducerTask(producerState, producerState.Producer.RemoveSubscriber(subscriptionId, streamId)))
                    .ToList();
                await Task.WhenAll(tasks);
                //if producers got removed
                if (State.Producers.Count < numProducersBeforeNotify)
                    await this.WriteStateAsync();
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

        private async Task ExecuteProducerTask(PubSubPublisherState producer, Task producerTask)
        {
            try
            {
                await producerTask;
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
                var grainRef = producer.Producer as GrainReference;
                // if producer is a system target on and unavailable silo, remove it.
                if (grainRef == null || grainRef.GrainId.IsSystemTarget())
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
    }
}
