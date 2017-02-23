using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Streams.PubSub
{
    [Serializable]
    internal class MemoryPubSubStreamSubscriptions
    {
        // use ConcurrentDictionary as a concurrent HasSet
        //http://stackoverflow.com/questions/18922985/concurrent-hashsett-in-net-framework
        private readonly ConcurrentDictionary<PubSubPublisherState, byte> producers;
        private readonly ConcurrentDictionary<PubSubSubscriptionState, byte> consumers;

        public MemoryPubSubStreamSubscriptions()
        {
            producers = new ConcurrentDictionary<PubSubPublisherState, byte>();
            consumers = new ConcurrentDictionary<PubSubSubscriptionState, byte>();
        }

        public void AddProducer(PubSubPublisherState state)
        {
            producers.AddOrUpdate(state, byte.MinValue, (publisherState, b) => b);
        }

        public void RemoveProducer(PubSubPublisherState state)
        {
            byte b;
            bool re = producers.TryRemove(state, out b);
            if (!re)
            {
                throw new OrleansException($"{this.GetType().Name} : Remove producer failed");
            }
        }

        public void AddConsumer(PubSubSubscriptionState state)
        {
            consumers.AddOrUpdate(state, byte.MinValue, (subscriptionState, b) => b);
        }

        public void RemoveConsumer(PubSubSubscriptionState state)
        {
            byte b;
            bool re = consumers.TryRemove(state, out b);
            if (!re)
            {
                throw new OrleansException($"{this.GetType().Name} : Remove consumer failed");
            }
        }

        public ICollection<PubSubPublisherState> Producers => producers.Keys;
        public ICollection<PubSubSubscriptionState> Consumers => consumers.Keys;
    }
    
    internal class MemoryPubSubStore
    {
        private readonly ConcurrentDictionary<StreamId, MemoryPubSubStreamSubscriptions> store;
        private readonly Logger logger;
        public MemoryPubSubStore()
        {
            store = new ConcurrentDictionary<StreamId, MemoryPubSubStreamSubscriptions>();
            logger = LogManager.GetLogger(this.GetType().Name, LoggerType.Runtime);
        }

        public ISet<PubSubSubscriptionState> RegisterProducer(StreamId streamId, string streamProvider,
            IStreamProducerExtension streamProducer)
        {
            var publisherState = new PubSubPublisherState(streamId, streamProducer);
            MemoryPubSubStreamSubscriptions subscriptions = new MemoryPubSubStreamSubscriptions();
            try
            {
                subscriptions.AddProducer(publisherState);
                subscriptions = store.AddOrUpdate(streamId, subscriptions, (id, streamSubscriptions) =>
                {
                    streamSubscriptions.AddProducer(publisherState);
                    return streamSubscriptions;
                });
            }
            catch (Exception exc)
            {
                logger.Error(ErrorCode.Stream_RegisterProducerFailed, $"Failed to register a stream producer.  Stream: {streamId}, Producer: {streamProducer}", exc);
                throw;
            }
            return subscriptions.Consumers.Where(c => !c.IsFaulted).ToSet();
        }

        public void UnregisterProducer(StreamId streamId, string streamProvider, IStreamProducerExtension streamProducer)
        {
            var publisherState = new PubSubPublisherState(streamId, streamProducer);
            try
            {
                MemoryPubSubStreamSubscriptions subscriptions;
                if (store.TryGetValue(streamId, out subscriptions))
                {
                    subscriptions.RemoveProducer(publisherState);
                }
            }
            catch (Exception exc)
            {
                logger.Error(ErrorCode.Stream_UnegisterProducerFailed,
                    $"Failed to unregister a stream producer. Stream: {streamId}, Producer: {streamProducer}", exc);
                throw;
            }
        }

        public async Task RegisterConsumer(GuidId subscriptionId, StreamId streamId, string streamProvider,
            IStreamConsumerExtension streamConsumer, IStreamFilterPredicateWrapper filter)
        {
            MemoryPubSubStreamSubscriptions subscriptions = new MemoryPubSubStreamSubscriptions();
            PubSubSubscriptionState state = new PubSubSubscriptionState(subscriptionId, streamId, streamConsumer);
            try
            {
                subscriptions.AddConsumer(state);
                subscriptions = store.AddOrUpdate(streamId, subscriptions, (id, streamSubscriptions) =>
                {
                    PubSubSubscriptionState existedState =
                        streamSubscriptions.Consumers.FirstOrDefault(s => s.Equals(subscriptionId));
                    if (existedState != null && existedState.IsFaulted)
                        throw new FaultedSubscriptionException(subscriptionId, streamId);
                    if (existedState == null)
                    {
                        existedState = state;
                        if (filter != null)
                        {
                            existedState.AddFilter(filter);
                        }
                        streamSubscriptions.AddConsumer(existedState);
                    }

                    return streamSubscriptions;
                });
            }
            catch (Exception exc)
            {
                logger.Error(ErrorCode.Stream_RegisterConsumerFailed, $"Failed to register a stream consumer.  Stream: {streamId}, SubscriptionId {subscriptionId}, Consumer: {streamConsumer}", exc);
                throw;
            }

            try
            {
                await NotifyProducersOfNewSubscription(subscriptions.Producers, subscriptionId, streamId, streamConsumer, filter);
            }
            catch (Exception exc)
            {
                logger.Error(ErrorCode.Stream_RegisterConsumerFailed,
                    $"Failed to update producers while register a stream consumer.  Stream: {streamId}, SubscriptionId {subscriptionId}, Consumer: {streamConsumer}", exc);
                throw;
            }
        }

        public async Task UnregisterConsumer(GuidId subscriptionId, StreamId streamId, string streamProvider)
        {
            MemoryPubSubStreamSubscriptions subscriptions;
            if (store.TryGetValue(streamId, out subscriptions))
            {
                var state = subscriptions.Consumers.FirstOrDefault(c => c.Equals(subscriptionId));
                if (state != null)
                {
                    if(state.IsFaulted)
                        throw new FaultedSubscriptionException(subscriptionId, streamId);
                    subscriptions.RemoveConsumer(state);
                }
                if (state == null)
                    return;
                //Notify procuder of removed consumer
                try
                {
                    await NotifyProducersOfRemovedSubscription(subscriptionId, streamId, subscriptions.Producers);
                }
                catch (Exception exc)
                {
                    logger.Error(ErrorCode.Stream_UnregisterConsumerFailed,
                    $"Failed to unregister a stream consumer.  Stream: {streamId}, SubscriptionId {subscriptionId}", exc);
                    throw;
                }
            }
        }

        public int ProducerCount(Guid streamId, string streamProvider, string streamNamespace)
        {
            MemoryPubSubStreamSubscriptions subscriptions;
            StreamId streamID = StreamId.GetStreamId(streamId, streamProvider, streamNamespace);
            if (store.TryGetValue(streamID, out subscriptions))
            {
                return subscriptions.Producers.Count;
            }
             return 0;
        }

        public int ConsumerCount(Guid streamId, string streamProvider, string streamNamespace)
        {
            MemoryPubSubStreamSubscriptions subscriptions;
            StreamId streamID = StreamId.GetStreamId(streamId, streamProvider, streamNamespace);
            if (store.TryGetValue(streamID, out subscriptions))
            {
                return subscriptions.Consumers.Count;
            }
            return 0;
        }

        public List<GuidId> GetAllSubscriptions(StreamId streamId, IStreamConsumerExtension streamConsumer)
        {
            List<GuidId> subscriptionIds = new List<GuidId>();
            MemoryPubSubStreamSubscriptions subscriptions;
            if (store.TryGetValue(streamId, out subscriptions))
            {
                subscriptionIds = subscriptions.Consumers.Where(c => !c.IsFaulted && c.Consumer.Equals(streamConsumer))
                                                          .Select(c => c.SubscriptionId)
                                                          .ToList();
            }
            return subscriptionIds;
        }

        public bool FaultSubscription(StreamId streamId, GuidId subscriptionId)
        {
            MemoryPubSubStreamSubscriptions subscriptions;
            if (store.TryGetValue(streamId, out subscriptions))
            {
                PubSubSubscriptionState state = subscriptions.Consumers.FirstOrDefault(c => c.Equals(subscriptionId));
                if (state != null)
                {
                    state.Fault();
                }
            }
            return true;
        }

        private async Task NotifyProducersOfNewSubscription(ICollection<PubSubPublisherState> producers, GuidId subscriptionId, StreamId streamId,
            IStreamConsumerExtension streamConsumer, IStreamFilterPredicateWrapper filter)
        {
            //Notify Producers about a new consumer
            if (producers.Count < 0)
                return;
            if (logger.IsVerbose)
                logger.Info("Notifying {0} existing producer(s) about new consumer {1}. Producers={2}",
                    producers.Count, streamConsumer, Utils.EnumerableToString(producers));
            var tasks = producers.Select(producerState => producerState.Producer.AddSubscriber(subscriptionId, streamId, streamConsumer, filter)).ToList();
            await Task.WhenAll(tasks);
        }

        private async Task NotifyProducersOfRemovedSubscription(GuidId subscriptionId, StreamId streamId, ICollection<PubSubPublisherState> producers)
        {
            if (producers.Count > 0)
            {
                if (logger.IsVerbose) logger.Verbose("Notifying {0} existing producers about unregistered consumer.", producers.Count);

                // Notify producers about unregistered consumer.
                List<Task> tasks = producers.Select(producerState => producerState.Producer.RemoveSubscriber(subscriptionId, streamId))
                                                  .ToList();
                await Task.WhenAll(tasks);
            }
        }
    }
}
