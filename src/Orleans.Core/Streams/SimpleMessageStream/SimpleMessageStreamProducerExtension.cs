using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.Streams;
using Microsoft.Extensions.Logging;
using System.Threading;

namespace Orleans.Providers.Streams.SimpleMessageStream
{
    /// <summary>
    /// Multiplexes messages to multiple different producers in the same grain over one grain-extension interface.
    /// 
    /// On the silo, we have one extension per activation and this extension multiplexes all streams on this activation 
    ///     (different stream ids and different stream providers).
    /// On the client, we have one extension per stream (we bind an extension for every StreamProducer, therefore every stream has its own extension).
    /// </summary>
    [Serializable]
    internal class SimpleMessageStreamProducerExtension : IStreamProducerExtension
    {
        private readonly Dictionary<StreamId, StreamConsumerExtensionCollection> remoteConsumers;
        private readonly IStreamProviderRuntime     providerRuntime;
        private readonly IStreamPubSub              streamPubSub;
        private readonly bool                       fireAndForgetDelivery;
        private readonly bool                       optimizeForImmutableData;
        private readonly ILogger                    logger;

        internal SimpleMessageStreamProducerExtension(IStreamProviderRuntime providerRt, IStreamPubSub pubsub, ILogger logger, bool fireAndForget, bool optimizeForImmutable)
        {
            providerRuntime = providerRt;
            streamPubSub = pubsub;
            fireAndForgetDelivery = fireAndForget;
            optimizeForImmutableData = optimizeForImmutable;
            remoteConsumers = new Dictionary<StreamId, StreamConsumerExtensionCollection>();
            this.logger = logger;
        }

        internal void AddStream(StreamId streamId)
        {
            StreamConsumerExtensionCollection obs;
            // no need to lock on _remoteConsumers, since on the client we have one extension per stream (per StreamProducer)
            // so this call is only made once, when StreamProducer is created.
            if (remoteConsumers.TryGetValue(streamId, out obs)) return;

            obs = new StreamConsumerExtensionCollection(streamPubSub, this.logger);
            remoteConsumers.Add(streamId, obs);
        }

        internal void RemoveStream(StreamId streamId)
        {
            remoteConsumers.Remove(streamId);
        }

        internal void AddSubscribers(StreamId streamId, ICollection<PubSubSubscriptionState> newSubscribers)
        {
            if (logger.IsEnabled(LogLevel.Debug))
                logger.Debug("{0} AddSubscribers {1} for stream {2}", providerRuntime.ExecutingEntityIdentity(), Utils.EnumerableToString(newSubscribers), streamId);
            
            StreamConsumerExtensionCollection consumers;
            if (remoteConsumers.TryGetValue(streamId, out consumers))
            {
                foreach (var newSubscriber in newSubscribers)
                {
                    consumers.AddRemoteSubscriber(newSubscriber.SubscriptionId, newSubscriber.Consumer, newSubscriber.Filter);
                }
            }
            else
            {
                // We got an item when we don't think we're the subscriber. This is a normal race condition.
                // We can drop the item on the floor, or pass it to the rendezvous, or log a warning.
            }
        }

        internal Task DeliverItem(StreamId streamId, object item)
        {
            StreamConsumerExtensionCollection consumers;
            if (remoteConsumers.TryGetValue(streamId, out consumers))
            {
                // Note: This is the main hot code path, 
                // and the caller immediately does await on the Task 
                // returned from this method, so we can just direct return here 
                // without incurring overhead of additional await.
                return consumers.DeliverItem(streamId, item, fireAndForgetDelivery, optimizeForImmutableData);
            }
            else
            {
                // We got an item when we don't think we're the subscriber. This is a normal race condition.
                // We can drop the item on the floor, or pass it to the rendezvous, or log a warning.
            }
            return Task.CompletedTask;
        }

        internal Task CompleteStream(StreamId streamId)
        {
            StreamConsumerExtensionCollection consumers;
            if (remoteConsumers.TryGetValue(streamId, out consumers))
            {
                return consumers.CompleteStream(streamId, fireAndForgetDelivery);
            }
            else
            {
                // We got an item when we don't think we're the subscriber. This is a normal race condition.
                // We can drop the item on the floor, or pass it to the rendezvous, or log a warning.
            }
            return Task.CompletedTask;
        }

        internal Task ErrorInStream(StreamId streamId, Exception exc)
        {
            StreamConsumerExtensionCollection consumers;
            if (remoteConsumers.TryGetValue(streamId, out consumers))
            {
                return consumers.ErrorInStream(streamId, exc, fireAndForgetDelivery);
            }
            else
            {
                // We got an item when we don't think we're the subscriber. This is a normal race condition.
                // We can drop the item on the floor, or pass it to the rendezvous, or log a warning.
            }
            return Task.CompletedTask;
        }


        // Called by rendezvous when new remote subsriber subscribes to this stream.
        public Task AddSubscriber(GuidId subscriptionId, StreamId streamId, IStreamConsumerExtension streamConsumer, IStreamFilterPredicateWrapper filter)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.Debug("{0} AddSubscriber {1} for stream {2}", providerRuntime.ExecutingEntityIdentity(), streamConsumer, streamId);
            }

            StreamConsumerExtensionCollection consumers;
            if (remoteConsumers.TryGetValue(streamId, out consumers))
            {
                consumers.AddRemoteSubscriber(subscriptionId, streamConsumer, filter);
            }
            else
            {
                // We got an item when we don't think we're the subscriber. This is a normal race condition.
                // We can drop the item on the floor, or pass it to the rendezvous, or log a warning.
            }
            return Task.CompletedTask;
        }

        public Task RemoveSubscriber(GuidId subscriptionId, StreamId streamId)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.Debug("{0} RemoveSubscription {1}", providerRuntime.ExecutingEntityIdentity(),
                    subscriptionId);
            }

            foreach (StreamConsumerExtensionCollection consumers in remoteConsumers.Values)
            {
                consumers.RemoveRemoteSubscriber(subscriptionId);
            }
            return Task.CompletedTask;
        }



        [Serializable]
        internal class StreamConsumerExtensionCollection
        {
            private readonly ConcurrentDictionary<GuidId, Tuple<IStreamConsumerExtension, IStreamFilterPredicateWrapper>> consumers;
            private readonly IStreamPubSub streamPubSub;
            private readonly ILogger logger;

            internal StreamConsumerExtensionCollection(IStreamPubSub pubSub, ILogger logger)
            {
                this.streamPubSub = pubSub;
                this.logger = logger;
                this.consumers = new ConcurrentDictionary<GuidId, Tuple<IStreamConsumerExtension, IStreamFilterPredicateWrapper>>();
            }

            internal void AddRemoteSubscriber(GuidId subscriptionId, IStreamConsumerExtension streamConsumer, IStreamFilterPredicateWrapper filter)
            {
                consumers.TryAdd(subscriptionId, Tuple.Create(streamConsumer, filter));
            }

            internal void RemoveRemoteSubscriber(GuidId subscriptionId)
            {
                Tuple<IStreamConsumerExtension, IStreamFilterPredicateWrapper> ignore;
                consumers.TryRemove(subscriptionId, out ignore);
                if (consumers.Count == 0)
                {
                    // Unsubscribe from PubSub?
                }
            }

            internal Task DeliverItem(StreamId streamId, object item, bool fireAndForgetDelivery, bool optimizeForImmutableData)
            {
                var tasks = fireAndForgetDelivery ? null : new List<Task>();
                foreach (KeyValuePair<GuidId, Tuple<IStreamConsumerExtension, IStreamFilterPredicateWrapper>> subscriptionKvp in consumers)
                {
                    IStreamConsumerExtension remoteConsumer = subscriptionKvp.Value.Item1;

                    // Apply filter(s) to see if we should forward this item to this consumer
                    IStreamFilterPredicateWrapper filter = subscriptionKvp.Value.Item2;
                    if (filter != null)
                    {
                        if (!filter.ShouldReceive(streamId, filter.FilterData, item))
                            continue;
                    }

                    Task task = DeliverToRemote(remoteConsumer, streamId, subscriptionKvp.Key, item, optimizeForImmutableData, fireAndForgetDelivery);
                    if (fireAndForgetDelivery) task.Ignore();
                    else tasks.Add(task);
                }
                // If there's no subscriber, presumably we just drop the item on the floor
                return fireAndForgetDelivery ? Task.CompletedTask : Task.WhenAll(tasks);
            }

            private async Task DeliverToRemote(IStreamConsumerExtension remoteConsumer, StreamId streamId, GuidId subscriptionId, object item, bool optimizeForImmutableData, bool fireAndForgetDelivery)
            {
                try
                {
                    if (optimizeForImmutableData)
                        await remoteConsumer.DeliverImmutable(subscriptionId, streamId, new Immutable<object>(item), null, null);
                    else
                        await remoteConsumer.DeliverMutable(subscriptionId, streamId, item, null, null);
                }
                catch (ClientNotAvailableException)
                {
                    Tuple<IStreamConsumerExtension, IStreamFilterPredicateWrapper> discard;
                    if (consumers.TryRemove(subscriptionId, out discard))
                    {
                        streamPubSub.UnregisterConsumer(subscriptionId, streamId, streamId.ProviderName).Ignore();
                        logger.Warn(ErrorCode.Stream_ConsumerIsDead,
                            "Consumer {0} on stream {1} is no longer active - permanently removing Consumer.", remoteConsumer, streamId);
                    }
                }
                catch(Exception ex)
                {
                    if (!fireAndForgetDelivery)
                    {
                        throw;
                    }
                    this.logger.LogWarning(ex, "Failed to deliver message to consumer on {SubscriptionId} for stream {StreamId}.", subscriptionId, streamId);
                }
            }

            internal Task CompleteStream(StreamId streamId, bool fireAndForgetDelivery)
            {
                var tasks = fireAndForgetDelivery ? null : new List<Task>();
                foreach (KeyValuePair<GuidId, Tuple<IStreamConsumerExtension, IStreamFilterPredicateWrapper>> kvp in consumers)
                {
                    IStreamConsumerExtension remoteConsumer = kvp.Value.Item1;
                    GuidId subscriptionId = kvp.Key;
                    Task task = NotifyComplete(remoteConsumer, subscriptionId, streamId, fireAndForgetDelivery);
                    if (fireAndForgetDelivery) task.Ignore();
                    else tasks.Add(task);
                }
                // If there's no subscriber, presumably we just drop the item on the floor
                return fireAndForgetDelivery ? Task.CompletedTask : Task.WhenAll(tasks);
            }

            private async Task NotifyComplete(IStreamConsumerExtension remoteConsumer, GuidId subscriptionId, StreamId streamId, bool fireAndForgetDelivery)
            {
                try
                {
                    await remoteConsumer.CompleteStream(subscriptionId);
                } catch(Exception ex)
                {
                    if (!fireAndForgetDelivery)
                    {
                        throw;
                    }
                    this.logger.LogWarning(ex, "Failed to notify consumer of stream completion on {SubscriptionId} for stream {StreamId}.", subscriptionId, streamId);
                }
            }

            internal Task ErrorInStream(StreamId streamId, Exception exc, bool fireAndForgetDelivery)
            {
                var tasks = fireAndForgetDelivery ? null : new List<Task>();
                foreach (KeyValuePair<GuidId, Tuple<IStreamConsumerExtension, IStreamFilterPredicateWrapper>> kvp in consumers)
                {
                    IStreamConsumerExtension remoteConsumer = kvp.Value.Item1;
                    GuidId subscriptionId = kvp.Key;
                    Task task = NotifyError(remoteConsumer, subscriptionId, exc, streamId, fireAndForgetDelivery);
                    if (fireAndForgetDelivery) task.Ignore();
                    else tasks.Add(task);
                }
                // If there's no subscriber, presumably we just drop the item on the floor
                return fireAndForgetDelivery ? Task.CompletedTask : Task.WhenAll(tasks);
            }

            private async Task NotifyError(IStreamConsumerExtension remoteConsumer, GuidId subscriptionId, Exception exc, StreamId streamId, bool fireAndForgetDelivery)
            {
                try
                {
                    await remoteConsumer.ErrorInStream(subscriptionId, exc);
                }
                catch (Exception ex)
                {
                    if (!fireAndForgetDelivery)
                    {
                        throw;
                    }
                    this.logger.LogWarning(ex, "Failed to notify consumer of stream error on {SubscriptionId} for stream {StreamId}. Error: {ErrorException}", subscriptionId, streamId, exc);
                }
            }
        }
    }
}
