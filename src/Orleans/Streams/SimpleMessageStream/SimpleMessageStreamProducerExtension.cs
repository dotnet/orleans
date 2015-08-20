/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Providers.Streams.SimpleMessageStream
{
    /// <summary>
    /// Multiplexes messages to mutiple different producers in the same grain over one grain-extension interface.
    /// 
    /// On the silo, we have one extension per activation and this extesion multiplexes all streams on this activation 
    ///     (different stream ids and different stream providers).
    /// On the client, we have one extension per stream (we bind an extesion for every StreamProducer, therefore every stream has its own extension).
    /// </summary>
    [Serializable]
    internal class SimpleMessageStreamProducerExtension : IStreamProducerExtension
    {
        private readonly Dictionary<StreamId, StreamConsumerExtensionCollection> remoteConsumers;
        private readonly IStreamProviderRuntime     providerRuntime;
        private readonly bool                       fireAndForgetDelivery;
        private readonly Logger                     logger;

        internal SimpleMessageStreamProducerExtension(IStreamProviderRuntime providerRt, bool fireAndForget)
        {
            providerRuntime = providerRt;
            fireAndForgetDelivery = fireAndForget;
            remoteConsumers = new Dictionary<StreamId, StreamConsumerExtensionCollection>();
            logger = providerRuntime.GetLogger(GetType().Name);
        }

        internal void AddStream(StreamId streamId)
        {
            StreamConsumerExtensionCollection obs;
            // no need to lock on _remoteConsumers, since on the client we have one extension per stream (per StreamProducer)
            // so this call is only made once, when StreamProducer is created.
            if (remoteConsumers.TryGetValue(streamId, out obs)) return;

            obs = new StreamConsumerExtensionCollection();
            remoteConsumers.Add(streamId, obs);
        }

        internal void RemoveStream(StreamId streamId)
        {
            remoteConsumers.Remove(streamId);
        }

        internal void AddSubscribers(StreamId streamId, ICollection<PubSubSubscriptionState> newSubscribers)
        {
            if (logger.IsVerbose) logger.Verbose("{0} AddSubscribers {1} for stream {2}", providerRuntime.ExecutingEntityIdentity(), Utils.EnumerableToString(newSubscribers), streamId);
            
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
                return consumers.DeliverItem(streamId, item, fireAndForgetDelivery);
            }
            else
            {
                // We got an item when we don't think we're the subscriber. This is a normal race condition.
                // We can drop the item on the floor, or pass it to the rendezvous, or log a warning.
            }
            return TaskDone.Done;
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
            return TaskDone.Done;
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
            return TaskDone.Done;
        }


        // Called by rendezvous when new remote subsriber subscribes to this stream.
        public Task AddSubscriber(GuidId subscriptionId, StreamId streamId, IStreamConsumerExtension streamConsumer, IStreamFilterPredicateWrapper filter)
        {
            if (logger.IsVerbose)
            {
                logger.Verbose("{0} AddSubscriber {1} for stream {2}", providerRuntime.ExecutingEntityIdentity(), streamConsumer, streamId);
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
            return TaskDone.Done;
        }

        public Task RemoveSubscriber(GuidId subscriptionId, StreamId streamId)
        {
            if (logger.IsVerbose)
            {
                logger.Verbose("{0} RemoveSubscription {1}", providerRuntime.ExecutingEntityIdentity(),
                    subscriptionId);
            }

            foreach (StreamConsumerExtensionCollection consumers in remoteConsumers.Values)
            {
                consumers.RemoveRemoteSubscriber(subscriptionId);
            }
            return TaskDone.Done;
        }



        [Serializable]
        internal class StreamConsumerExtensionCollection
        {
            private readonly ConcurrentDictionary<GuidId, Tuple<IStreamConsumerExtension, IStreamFilterPredicateWrapper>> consumers;

            internal StreamConsumerExtensionCollection()
            {
                consumers = new ConcurrentDictionary<GuidId, Tuple<IStreamConsumerExtension, IStreamFilterPredicateWrapper>>();
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

            internal Task DeliverItem(StreamId streamId, object item, bool fireAndForgetDelivery)
            {
                var tasks = fireAndForgetDelivery ? null : new List<Task>();
                var immutableItem = new Immutable<object>(item);
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

                    Task task = remoteConsumer.DeliverItem(subscriptionKvp.Key, immutableItem, null);
                    if (fireAndForgetDelivery) task.Ignore();
                    else tasks.Add(task);
                }
                // If there's no subscriber, presumably we just drop the item on the floor
                return fireAndForgetDelivery ? TaskDone.Done : Task.WhenAll(tasks);
            }

            internal Task CompleteStream(StreamId streamId, bool fireAndForgetDelivery)
            {
                var tasks = fireAndForgetDelivery ? null : new List<Task>();
                foreach (GuidId subscriptionId in consumers.Keys)
                {
                    var data = consumers[subscriptionId];
                    IStreamConsumerExtension remoteConsumer = data.Item1;
                    Task task = remoteConsumer.CompleteStream(subscriptionId);
                    if (fireAndForgetDelivery) task.Ignore();
                    else tasks.Add(task);
                }
                // If there's no subscriber, presumably we just drop the item on the floor
                return fireAndForgetDelivery ? TaskDone.Done : Task.WhenAll(tasks);
            }

            internal Task ErrorInStream(StreamId streamId, Exception exc, bool fireAndForgetDelivery)
            {
                var tasks = fireAndForgetDelivery ? null : new List<Task>();
                foreach (GuidId subscriptionId in consumers.Keys)
                {
                    var data = consumers[subscriptionId];
                    IStreamConsumerExtension remoteConsumer = data.Item1;
                    Task task = remoteConsumer.ErrorInStream(subscriptionId, exc);
                    if (fireAndForgetDelivery) task.Ignore();
                    else tasks.Add(task);
                }
                // If there's no subscriber, presumably we just drop the item on the floor
                return fireAndForgetDelivery ? TaskDone.Done : Task.WhenAll(tasks);
            }
        }
    }
}
