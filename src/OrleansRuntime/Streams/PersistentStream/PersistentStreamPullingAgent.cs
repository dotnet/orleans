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
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Orleans.Runtime;
using Orleans.Concurrency;

namespace Orleans.Streams
{
    internal class PersistentStreamPullingAgent : SystemTarget, IPersistentStreamPullingAgent
    {
        private static readonly IBackoffProvider DefaultBackoffProvider = new ExponentialBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1));
        private const int StreamInactivityCheckFrequency = 10;

        private readonly string streamProviderName;
        private readonly IStreamProviderRuntime providerRuntime;
        private readonly IStreamPubSub pubSub;
        private readonly Dictionary<StreamId, StreamConsumerCollection> pubSubCache;
        private readonly SafeRandom safeRandom;
        private readonly PersistentStreamProviderConfig config;
        private readonly Logger logger;
        private readonly CounterStatistic numReadMessagesCounter;
        private readonly CounterStatistic numSentMessagesCounter;
        private int numMessages;

        private IQueueAdapter queueAdapter;
        private IQueueCache queueCache;
        private IQueueAdapterReceiver receiver;
        private IStreamFailureHandler streamFailureHandler;
        private DateTime lastTimeCleanedPubSubCache;
        private IDisposable timer;

        internal readonly QueueId QueueId;


        internal PersistentStreamPullingAgent(
            GrainId id, 
            string strProviderName,
            IStreamProviderRuntime runtime,
            IStreamPubSub streamPubSub,
            QueueId queueId,
            PersistentStreamProviderConfig config)
            : base(id, runtime.ExecutingSiloAddress, true)
        {
            if (runtime == null) throw new ArgumentNullException("runtime", "PersistentStreamPullingAgent: runtime reference should not be null");
            if (strProviderName == null) throw new ArgumentNullException("runtime", "PersistentStreamPullingAgent: strProviderName should not be null");

            QueueId = queueId;
            streamProviderName = strProviderName;
            providerRuntime = runtime;
            pubSub = streamPubSub;
            pubSubCache = new Dictionary<StreamId, StreamConsumerCollection>();
            safeRandom = new SafeRandom();
            this.config = config;
            numMessages = 0;

            logger = providerRuntime.GetLogger(GrainId + "-" + streamProviderName);
            logger.Info((int)ErrorCode.PersistentStreamPullingAgent_01, 
                "Created {0} {1} for Stream Provider {2} on silo {3} for Queue {4}.",
                GetType().Name, GrainId.ToDetailedString(), streamProviderName, Silo, QueueId.ToStringWithHashCode());

            string statUniquePostfix = strProviderName + "." + QueueId;
            numReadMessagesCounter = CounterStatistic.FindOrCreate(new StatisticName(StatisticNames.STREAMS_PERSISTENT_STREAM_NUM_READ_MESSAGES, statUniquePostfix));
            numSentMessagesCounter = CounterStatistic.FindOrCreate(new StatisticName(StatisticNames.STREAMS_PERSISTENT_STREAM_NUM_SENT_MESSAGES, statUniquePostfix));
            IntValueStatistic.FindOrCreate(new StatisticName(StatisticNames.STREAMS_PERSISTENT_STREAM_PUBSUB_CACHE_SIZE, statUniquePostfix), () => pubSubCache.Count);
            IntValueStatistic.FindOrCreate(new StatisticName(StatisticNames.STREAMS_PERSISTENT_STREAM_QUEUE_CACHE_SIZE, statUniquePostfix), () => queueCache !=null ? queueCache.Size : 0);
        }

        /// <summary>
        /// Take responsibility for a new queues that was assigned to me via a new range.
        /// We first store the new queue in our internal data structure, try to initialize it and start a pumping timer.
        /// ERROR HANDLING:
        ///     The resposibility to handle initializatoion and shutdown failures is inside the INewQueueAdapterReceiver code.
        ///     The agent will call Initialize once and log an error. It will not call initiliaze again.
        ///     The receiver itself may attempt later to recover from this error and do initialization again. 
        ///     The agent will assume initialization has succeeded and will subsequently start calling pumping receive.
        ///     Same applies to shutdown.
        /// </summary>
        /// <param name="qAdapter"></param>
        /// <param name="queueAdapterCache"></param>
        /// <param name="failureHandler"></param>
        /// <returns></returns>
        public async Task Initialize(Immutable<IQueueAdapter> qAdapter, Immutable<IQueueAdapterCache> queueAdapterCache, Immutable<IStreamFailureHandler> failureHandler)
        {
            if (qAdapter.Value == null) throw new ArgumentNullException("qAdapter", "Init: queueAdapter should not be null");
            if (failureHandler.Value == null) throw new ArgumentNullException("failureHandler", "Init: streamDeliveryFailureHandler should not be null");

            logger.Info((int)ErrorCode.PersistentStreamPullingAgent_02, "Init of {0} {1} on silo {2} for queue {3}.",
                GetType().Name, GrainId.ToDetailedString(), Silo, QueueId.ToStringWithHashCode());
            
            // Remove cast once we cleanup
            queueAdapter = qAdapter.Value;
            streamFailureHandler = failureHandler.Value;
            lastTimeCleanedPubSubCache = DateTime.UtcNow;

            try
            {
                receiver = queueAdapter.CreateReceiver(QueueId);
            }
            catch (Exception exc)
            {
                logger.Error((int)ErrorCode.PersistentStreamPullingAgent_02, String.Format("Exception while calling IQueueAdapter.CreateNewReceiver."), exc);
                return;
            }

            try
            {
                if (queueAdapterCache.Value != null)
                {
                    queueCache = queueAdapterCache.Value.CreateQueueCache(QueueId);
                }
            }
            catch (Exception exc)
            {
                logger.Error((int)ErrorCode.PersistentStreamPullingAgent_23, String.Format("Exception while calling IQueueAdapterCache.CreateQueueCache."), exc);
                return;
            }

            try
            {
                var task = OrleansTaskExtentions.SafeExecute(() => receiver.Initialize(config.InitQueueTimeout));
                task = task.LogException(logger, ErrorCode.PersistentStreamPullingAgent_03, String.Format("QueueAdapterReceiver {0} failed to Initialize.", QueueId.ToStringWithHashCode()));
                await task;
            }
            catch
            {
                // Just ignore this exception and proceed as if Initialize has succeeded.
                // We already logged individual exceptions for individual calls to Initialize. No need to log again.
            }
            // Setup a reader for a new receiver. 
            // Even if the receiver failed to initialise, treat it as OK and start pumping it. It's receiver responsibility to retry initialization.
            var randomTimerOffset = safeRandom.NextTimeSpan(config.GetQueueMsgsTimerPeriod);
            timer = providerRuntime.RegisterTimer(AsyncTimerCallback, QueueId, randomTimerOffset, config.GetQueueMsgsTimerPeriod);

            logger.Info((int) ErrorCode.PersistentStreamPullingAgent_04, "Taking queue {0} under my responsibility.", QueueId.ToStringWithHashCode());
        }

        public async Task Shutdown()
        {
            // Stop pulling from queues that are not in my range anymore.
            logger.Info((int)ErrorCode.PersistentStreamPullingAgent_05, "Shutdown of {0} responsible for queue: {1}", GetType().Name, QueueId.ToStringWithHashCode());
            if (timer != null)
            {
                var tmp = timer;
                timer = null;
                Utils.SafeExecute(tmp.Dispose);
            }

            var unregisterTasks = new List<Task>();
            var meAsStreamProducer = this.AsReference<IStreamProducerExtension>();
            foreach (var streamId in pubSubCache.Keys)
            {
                logger.Info((int)ErrorCode.PersistentStreamPullingAgent_06, "Unregister PersistentStreamPullingAgent Producer for stream {0}.", streamId);
                unregisterTasks.Add(pubSub.UnregisterProducer(streamId, streamProviderName, meAsStreamProducer));
            }

            try
            {
                var task = OrleansTaskExtentions.SafeExecute(() => receiver.Shutdown(config.InitQueueTimeout));
                task = task.LogException(logger, ErrorCode.PersistentStreamPullingAgent_07,
                    String.Format("QueueAdapterReceiver {0} failed to Shutdown.", QueueId));
                await task;
            }
            catch
            {
                // Just ignore this exception and proceed as if Shutdown has succeeded.
                // We already logged individual exceptions for individual calls to Shutdown. No need to log again.
            }

            try
            {
                await Task.WhenAll(unregisterTasks);
            }
            catch (Exception exc)
            {
                logger.Warn((int)ErrorCode.PersistentStreamPullingAgent_08,
                    "Failed to unregister myself as stream producer to some streams taht used to be in my responsibility.", exc);
            }
        }

        public Task AddSubscriber(
            GuidId subscriptionId,
            StreamId streamId,
            IStreamConsumerExtension streamConsumer,
            IStreamFilterPredicateWrapper filter)
        {
            if (logger.IsVerbose) logger.Verbose((int)ErrorCode.PersistentStreamPullingAgent_09, "AddSubscriber: Stream={0} Subscriber={1}.", streamId, streamConsumer);
            // cannot await here because explicit consumers trigger this call, so it could cause a deadlock.
            AddSubscriber_Impl(subscriptionId, streamId, streamConsumer, null, filter)
                .LogException(logger, ErrorCode.PersistentStreamPullingAgent_26,
                    String.Format("Failed to add subscription for stream {0}." , streamId))
                .Ignore();
            return TaskDone.Done;
        }

        // Called by rendezvous when new remote subscriber subscribes to this stream.
        private async Task AddSubscriber_Impl(
            GuidId subscriptionId,
            StreamId streamId,
            IStreamConsumerExtension streamConsumer,
            StreamSequenceToken token,
            IStreamFilterPredicateWrapper filter)
        {
            IQueueCacheCursor cursor = null;
            StreamSequenceToken requestedToken = null;
            // if not cache, then we can't get cursor and there is no reason to ask consumer for token.
            if (queueCache != null)
            {
                DataNotAvailableException errorOccured = null;
                try
                {
                    requestedToken = await streamConsumer.GetSequenceToken(subscriptionId);
                    // Set cursor if not cursor is set, or if subscription provides new token
                    requestedToken = requestedToken ?? token;
                    if (requestedToken != null)
                    {
                        cursor = queueCache.GetCacheCursor(streamId.Guid, streamId.Namespace, requestedToken);
                    }
                }
                catch (DataNotAvailableException dataNotAvailableException)
                {
                    errorOccured = dataNotAvailableException;
                }
                if (errorOccured != null)
                {
                    // notify consumer that the data is not available, if we can.
                    await OrleansTaskExtentions.ExecuteAndIgnoreException(() => streamConsumer.ErrorInStream(subscriptionId, errorOccured));
                }
            }
            AddSubscriberToSubscriptionCache(subscriptionId, streamId, streamConsumer, cursor, requestedToken, filter);
        }

        // Called by rendezvous when new remote subscriber subscribes to this stream or when registering a new stream with the pubsub system.
        private void AddSubscriberToSubscriptionCache(
            GuidId subscriptionId,
            StreamId streamId,
            IStreamConsumerExtension streamConsumer,
            IQueueCacheCursor newCursor,
            StreamSequenceToken requestedToken,
            IStreamFilterPredicateWrapper filter)
        {
            StreamConsumerCollection streamDataCollection;
            if (!pubSubCache.TryGetValue(streamId, out streamDataCollection))
            {
                streamDataCollection = new StreamConsumerCollection(DateTime.UtcNow);
                pubSubCache.Add(streamId, streamDataCollection);
            }

            StreamConsumerData data;
            if (!streamDataCollection.TryGetConsumer(subscriptionId, out data))
                data = streamDataCollection.AddConsumer(subscriptionId, streamId, streamConsumer, filter);

            data.LastToken = requestedToken;

            // if we have a new cursor, use it
            if (newCursor != null)
            {
                data.Cursor = newCursor;
            } // else if we don't yet have a cursor, get a cursor at the end of the cash (null sequence token).
            else if (data.Cursor == null && queueCache != null)
            {
                data.Cursor = queueCache.GetCacheCursor(streamId.Guid, streamId.Namespace, null);
            }

            if (data.State == StreamConsumerDataState.Inactive)
                RunConsumerCursor(data, filter).Ignore(); // Start delivering events if not actively doing so
        }

        public Task RemoveSubscriber(GuidId subscriptionId, StreamId streamId)
        {
            RemoveSubscriber_Impl(subscriptionId, streamId);
            return TaskDone.Done;
        }

        public void RemoveSubscriber_Impl(GuidId subscriptionId, StreamId streamId)
        {
            StreamConsumerCollection streamData;
            if (!pubSubCache.TryGetValue(streamId, out streamData)) return;

            // remove consumer
            bool removed = streamData.RemoveConsumer(subscriptionId);
            if (removed && logger.IsVerbose) logger.Verbose((int)ErrorCode.PersistentStreamPullingAgent_10, "Removed Consumer: subscription={0}, for stream {1}.", subscriptionId, streamId);
            
            if (streamData.Count == 0)
                pubSubCache.Remove(streamId);
        }

        private async Task AsyncTimerCallback(object state)
        {
            try
            {
                var myQueueId = (QueueId)(state);
                if (timer == null) return; // timer was already removed, last tick
                
                IQueueAdapterReceiver rcvr = receiver;
                int maxCacheAddCount = queueCache != null ? queueCache.MaxAddCount : QueueAdapterConstants.UNLIMITED_GET_QUEUE_MSG;

                // loop through the queue until it is empty.
                while (timer != null) // timer will be set to null when we are asked to shudown. 
                {
                    var now = DateTime.UtcNow;
                    // Try to cleanup the pubsub cache at the cadence of 10 times in the configurable StreamInactivityPeriod.
                    if ((now - lastTimeCleanedPubSubCache) >= config.StreamInactivityPeriod.Divide(StreamInactivityCheckFrequency))
                    {
                        lastTimeCleanedPubSubCache = now;
                        CleanupPubSubCache(now);
                    }

                    if (queueCache != null)
                    {
                        IList<IBatchContainer> purgedItems;
                        if (queueCache.TryPurgeFromCache(out purgedItems))
                        {
                            await rcvr.MessagesDeliveredAsync(purgedItems);
                        }
                    }

                    if (queueCache != null && queueCache.IsUnderPressure())
                    {
                        // Under back pressure. Exit the loop. Will attempt again in the next timer callback.
                        logger.Info((int)ErrorCode.PersistentStreamPullingAgent_24, String.Format("Stream cache is under pressure. Backing off."));
                        return;
                    }

                    // Retrive one multiBatch from the queue. Every multiBatch has an IEnumerable of IBatchContainers, each IBatchContainer may have multiple events.
                    IList<IBatchContainer> multiBatch = await rcvr.GetQueueMessagesAsync(maxCacheAddCount);
                    
                    if (multiBatch == null || multiBatch.Count == 0) return; // queue is empty. Exit the loop. Will attempt again in the next timer callback.

                    if (queueCache != null)
                    {
                        queueCache.AddToCache(multiBatch);
                    }
                    numMessages += multiBatch.Count;
                    numReadMessagesCounter.IncrementBy(multiBatch.Count);
                    if (logger.IsVerbose2) logger.Verbose2((int)ErrorCode.PersistentStreamPullingAgent_11, "Got {0} messages from queue {1}. So far {2} msgs from this queue.",
                        multiBatch.Count, myQueueId.ToStringWithHashCode(), numMessages);
                    
                    foreach (var group in 
                        multiBatch
                        .Where(m => m != null)
                        .GroupBy(container => new Tuple<Guid, string>(container.StreamGuid, container.StreamNamespace)))
                    {
                        var streamId = StreamId.GetStreamId(group.Key.Item1, queueAdapter.Name, group.Key.Item2);
                        StreamConsumerCollection streamData;
                        if (pubSubCache.TryGetValue(streamId, out streamData))
                        {
                            streamData.RefreshActivity(now);
                            StartInactiveCursors(streamData); // if this is an existing stream, start any inactive cursors
                        }
                        else
                        {
                            RegisterStream(streamId, group.First().SequenceToken, now).Ignore(); // if this is a new stream register as producer of stream in pub sub system
                        }
                    }
                }
            }
            catch (Exception exc)
            {
                logger.Error((int)ErrorCode.PersistentStreamPullingAgent_12,
                    String.Format("Exception while PersistentStreamPullingAgentGrain.AsyncTimerCallback"), exc);
            }
        }

        private void CleanupPubSubCache(DateTime now)
        {
            if (pubSubCache.Count == 0) return;
            var toRemove = pubSubCache.Where(pair => pair.Value.IsInactive(now, config.StreamInactivityPeriod))
                         .Select(pair => pair.Key)
                         .ToList();
            toRemove.ForEach(key => pubSubCache.Remove(key));
        }

        private async Task RegisterStream(StreamId streamId, StreamSequenceToken firstToken, DateTime now)
        {
            var streamData = new StreamConsumerCollection(now);
            pubSubCache.Add(streamId, streamData);
            // Create a fake cursor to point into a cache.
            // That way we will not purge the event from the cache, until we talk to pub sub.
            // This will help ensure the "casual consistency" between pre-existing subscripton (of a potentially new already subscribed consumer) 
            // and later production.
            var pinCursor = queueCache.GetCacheCursor(streamId.Guid, streamId.Namespace, firstToken);

            try
            {
                await RegisterAsStreamProducer(streamId, firstToken);
            }finally
            {
                // Cleanup the fake pinning cursor.
                pinCursor.Dispose();
            }
        }

        private void StartInactiveCursors(StreamConsumerCollection streamData)
        {
            foreach (StreamConsumerData consumerData in streamData.AllConsumers())
            {
                if (consumerData.State == StreamConsumerDataState.Inactive)
                {
                    // wake up inactive consumers
                    RunConsumerCursor(consumerData, consumerData.Filter).Ignore();
                }
                else
                {
                    if (consumerData.Cursor != null)
                    {
                        consumerData.Cursor.Refresh();
                    }
                }
            }
        }

        private async Task RunConsumerCursor(StreamConsumerData consumerData, IStreamFilterPredicateWrapper filterWrapper)
        {
            try
            {
                // double check in case of interleaving
                if (consumerData.State == StreamConsumerDataState.Active ||
                    consumerData.Cursor == null) return;
                
                consumerData.State = StreamConsumerDataState.Active;
                while (consumerData.Cursor != null && consumerData.Cursor.MoveNext())
                {
                    IBatchContainer batch = null;
                    Exception ex;
                    Task deliveryTask;
                    bool deliveryFailed = false;
                    try
                    {
                        batch = consumerData.Cursor.GetCurrent(out ex);
                    }
                    catch (DataNotAvailableException dataNotAvailable)
                    {
                        ex = dataNotAvailable;
                    }

                    // Apply filtering to this batch, if applicable
                    if (filterWrapper != null && batch != null)
                    {
                        try
                        {
                            // Apply batch filter to this input batch, to see whether we should deliver it to this consumer.
                            if (!batch.ShouldDeliver(
                                consumerData.StreamId,
                                filterWrapper.FilterData,
                                filterWrapper.ShouldReceive)) continue; // Skip this batch -- nothing to do
                        }
                        catch (Exception exc)
                        {
                            var message = string.Format("Ignoring exception while trying to evaluate subscription filter function {0} on stream {1} in PersistentStreamPullingAgentGrain.RunConsumerCursor", filterWrapper, consumerData.StreamId);
                            logger.Warn((int) ErrorCode.PersistentStreamPullingAgent_13, message, exc);
                        }
                    }

                    if (batch != null)
                    {
                        deliveryTask = AsyncExecutorWithRetries.ExecuteWithRetries(i => DeliverBatchToConsumer(consumerData, batch),
                            AsyncExecutorWithRetries.INFINITE_RETRIES, (exception, i) => !(exception is DataNotAvailableException), config.MaxEventDeliveryTime, DefaultBackoffProvider);
                    }
                    else if (ex == null)
                    {
                        deliveryTask = consumerData.StreamConsumer.CompleteStream(consumerData.SubscriptionId);
                    }
                    else
                    {
                        // If data is not avialable, bring cursor current
                        if (ex is DataNotAvailableException)
                        {
                            consumerData.Cursor = queueCache.GetCacheCursor(consumerData.StreamId.Guid,
                                consumerData.StreamId.Namespace, null);
                        }
                        // Notify client of error.
                        deliveryTask = DeliverErrorToConsumer(consumerData, ex, null);
                    }

                    try
                    {
                        numSentMessagesCounter.Increment();
                        await deliveryTask;
                    }
                    catch (Exception exc)
                    {
                        var message = string.Format("Exception while trying to deliver msgs to stream {0} in PersistentStreamPullingAgentGrain.RunConsumerCursor", consumerData.StreamId);
                        logger.Error((int)ErrorCode.PersistentStreamPullingAgent_14, message, exc);
                        deliveryFailed = true;
                    }
                    // if we failed to deliver a batch
                    if (deliveryFailed && batch != null)
                    {
                        // notify consumer of delivery error, if we can.
                        await OrleansTaskExtentions.ExecuteAndIgnoreException(() => DeliverErrorToConsumer(consumerData, new StreamEventDeliveryFailureException(consumerData.StreamId), batch));

                        // record that there was a delivery failure
                        await streamFailureHandler.OnDeliveryFailure(consumerData.SubscriptionId, streamProviderName,
                            consumerData.StreamId, batch.SequenceToken);
                        // if configured to fault on delivery failure and this is not an implicit subscription, fault and remove the subscription
                        if (streamFailureHandler.ShouldFaultSubsriptionOnError && !SubscriptionMarker.IsImplicitSubscription(consumerData.SubscriptionId.Guid))
                        {
                            try
                            {
                                // notify consumer of faulted subscription, if we can.
                                DeliverErrorToConsumer(consumerData, 
                                    new FaultedSubscriptionException(consumerData.SubscriptionId, consumerData.StreamId), batch)
                                    .Ignore();
                                // mark subscription as faulted.
                                await pubSub.FaultSubscription(consumerData.StreamId, consumerData.SubscriptionId);
                            }
                            finally
                            {
                                // remove subscription
                                RemoveSubscriber_Impl(consumerData.SubscriptionId, consumerData.StreamId);
                            }
                            return;
                        }
                    }
                }
                consumerData.State = StreamConsumerDataState.Inactive;
            }
            catch (Exception exc)
            {
                // RunConsumerCursor is fired with .Ignore so we should log if anything goes wrong, because there is no one to catch the exception
                logger.Error((int)ErrorCode.PersistentStreamPullingAgent_15, "Ignored RunConsumerCursor Error", exc);
                throw;
            }
        }

        private async Task DeliverBatchToConsumer(StreamConsumerData consumerData, IBatchContainer batch)
        {
            if (batch.RequestContext != null)
            {
                RequestContext.Import(batch.RequestContext);
            }
            try
            {
                StreamSequenceToken prevToken = consumerData.LastToken;
                StreamSequenceToken newToken = await consumerData.StreamConsumer.DeliverBatch(consumerData.SubscriptionId, batch.AsImmutable(), prevToken);
                if (newToken != null)
                {
                    consumerData.LastToken = newToken;
                    consumerData.Cursor = queueCache.GetCacheCursor(consumerData.StreamId.Guid,
                        consumerData.StreamId.Namespace, newToken);
                }
                else
                {
                    consumerData.LastToken = batch.SequenceToken; // this is the currently delivered token
                }
            }
            finally
            {
                if (batch.RequestContext != null)
                {
                    RequestContext.Clear();
                }
            }
        }

        private async Task DeliverErrorToConsumer(StreamConsumerData consumerData, Exception exc, IBatchContainer batch)
        {
            if (batch !=null && batch.RequestContext != null)
            {
                RequestContext.Import(batch.RequestContext);
            }
            try
            {
                await consumerData.StreamConsumer.ErrorInStream(consumerData.SubscriptionId, exc);
            }
            finally
            {
                if (batch != null && batch.RequestContext != null)
                {
                    RequestContext.Clear();
                }
            }
        }

        private async Task RegisterAsStreamProducer(StreamId streamId, StreamSequenceToken streamStartToken)
        {
            try
            {
                if (pubSub == null) throw new NullReferenceException("Found pubSub reference not set up correctly in RetreaveNewStream");

                IStreamProducerExtension meAsStreamProducer = this.AsReference<IStreamProducerExtension>();
                ISet<PubSubSubscriptionState> streamData = await pubSub.RegisterProducer(streamId, streamProviderName, meAsStreamProducer);
                if (logger.IsVerbose) logger.Verbose((int)ErrorCode.PersistentStreamPullingAgent_16, "Got back {0} Subscribers for stream {1}.", streamData.Count, streamId);

                var addSubscriptionTasks = new List<Task>(streamData.Count);
                foreach (PubSubSubscriptionState item in streamData)
                {
                    addSubscriptionTasks.Add(AddSubscriber_Impl(item.SubscriptionId, item.Stream, item.Consumer, streamStartToken, item.Filter));
                }
                await Task.WhenAll(addSubscriptionTasks);
            }
            catch (Exception exc)
            {
                // RegisterAsStreamProducer is fired with .Ignore so we should log if anything goes wrong, because there is no one to catch the exception
                logger.Error((int)ErrorCode.PersistentStreamPullingAgent_17, "Ignored RegisterAsStreamProducer Error", exc);
                throw;
            }
        }
    }
}
