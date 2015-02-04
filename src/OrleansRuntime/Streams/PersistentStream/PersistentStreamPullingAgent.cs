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

﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Orleans.Runtime;
using Orleans.Concurrency;

namespace Orleans.Streams
{
    internal class PersistentStreamPullingAgent : SystemTarget, IPersistentStreamPullingAgent
    {

        private readonly string streamProviderName;
        private readonly IStreamProviderRuntime providerRuntime;
        private readonly IStreamPubSub pubSub;
        private readonly Dictionary<StreamId, StreamConsumerCollection> pubSubCache;
        private readonly SafeRandom safeRandom;
        private readonly TimeSpan queueGetPeriod;
        private readonly TimeSpan initQueueTimeout;
        private readonly Logger logger;
        private readonly CounterStatistic numReadMessagesCounter;
        private readonly CounterStatistic numSentMessagesCounter;
        private int numMessages;

        private IQueueAdapter queueAdapter;
        private IQueueAdapterReceiver receiver;
        private IDisposable timer;

        internal readonly QueueId QueueId;


        internal PersistentStreamPullingAgent(
            GrainId id, 
            string strProviderName,
            IStreamProviderRuntime runtime,
            QueueId queueId, 
            TimeSpan queueGetPeriod,
            TimeSpan initQueueTimeout)
            : base(id, runtime.ExecutingSiloAddress, true)
        {
            if (runtime == null) throw new ArgumentNullException("runtime", "PersistentStreamPullingAgent: runtime reference should not be null");

            QueueId = queueId;
            streamProviderName = strProviderName;
            providerRuntime = runtime;
            pubSub = runtime.PubSub(StreamPubSubType.GrainBased);
            pubSubCache = new Dictionary<StreamId, StreamConsumerCollection>();
            safeRandom = new SafeRandom();
            this.queueGetPeriod = queueGetPeriod;
            this.initQueueTimeout = initQueueTimeout;
            numMessages = 0;

            logger = providerRuntime.GetLogger(this.GrainId.ToString() + "-" + streamProviderName);
            logger.Info((int)ErrorCode.PersistentStreamPullingAgent_01, 
                "Created {0} {1} for Stream Provider {2} on silo {3} for Queue {4}.",
                this.GetType().Name, this.GrainId.ToDetailedString(), streamProviderName, base.Silo, QueueId.ToStringWithHashCode());

            numReadMessagesCounter = CounterStatistic.FindOrCreate(new StatisticName(StatisticNames.STREAMS_PERSISTENT_STREAM_NUM_READ_MESSAGES, strProviderName));
            numSentMessagesCounter = CounterStatistic.FindOrCreate(new StatisticName(StatisticNames.STREAMS_PERSISTENT_STREAM_NUM_SENT_MESSAGES, strProviderName));
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
        /// <returns></returns>
        public async Task Initialize(Immutable<IQueueAdapter> qAdapter)
        {
            if (qAdapter.Value == null) throw new ArgumentNullException("qAdapter", "Init: queueAdapter should not be null");

            logger.Info((int)ErrorCode.PersistentStreamPullingAgent_02, "Init of {0} {1} on silo {2} for queue {3}.",
                this.GetType().Name, this.GrainId.ToDetailedString(), base.Silo, QueueId.ToStringWithHashCode());
            
            // Remove cast once we cleanup
            queueAdapter = qAdapter.Value;
     
            try
            {
                receiver = queueAdapter.CreateReceiver(QueueId);
            }
            catch (Exception exc)
            {
                logger.Error((int)ErrorCode.PersistentStreamPullingAgent_02, String.Format("Exception while calling INewQueueAdapter.CreateNewReceiver."), exc);
                return;
            }

            try
            {
                var task = OrleansTaskExtentions.SafeExecute(() => receiver.Initialize(initQueueTimeout));
                task = task.LogException(logger, ErrorCode.PersistentStreamPullingAgent_03, String.Format("QueueAdapterReceiver {0} failed to Initialize.", QueueId.ToStringWithHashCode()));
                await task;
            }
            catch (Exception)
            {
                // Just ignore this exception and proceed as if Initialize has succeeded.
                // We already logged individual exceptions for individual calls to Initialize. No need to log again.
            }
            // Setup a reader for a new receiver. 
            // Even if the receiver failed to initialise, treat it as OK and start pumping it. It's receiver responsibility to retry initialization.
            var randomTimerOffset = safeRandom.NextTimeSpan(queueGetPeriod);
            timer = providerRuntime.RegisterTimer(AsyncTimerCallback, QueueId, randomTimerOffset, queueGetPeriod);

            logger.Info((int) ErrorCode.PersistentStreamPullingAgent_04, "Taking queue {0} under my responsibility.", QueueId.ToStringWithHashCode());
        }

        public async Task Shutdown()
        {
            // Stop pulling from queues that are not in my range anymore.
            logger.Info((int)ErrorCode.PersistentStreamPullingAgent_05, "Shutdown of {0} responsible for queue: {1}", this.GetType().Name, QueueId.ToStringWithHashCode());
            if (timer != null)
            {
                var tmp = timer;
                timer = null;
                tmp.Dispose();
            }

            var unregisterTasks = new List<Task>();
            var meAsStreamProducer = StreamProducerExtensionFactory.Cast(this.AsReference());
            foreach (var streamId in pubSubCache.Keys)
            {
                logger.Info((int)ErrorCode.PersistentStreamPullingAgent_06, "Unregister PersistentStreamPullingAgent Producer for stream {0}.", streamId);
                unregisterTasks.Add(pubSub.UnregisterProducer(streamId, streamProviderName, meAsStreamProducer));
            }

            try
            {
                var task = OrleansTaskExtentions.SafeExecute(() => receiver.Shutdown(initQueueTimeout));
                task = task.LogException(logger, ErrorCode.PersistentStreamPullingAgent_07,
                    String.Format("QueueAdapterReceiver {0} failed to Shutdown.", QueueId));
                await task;
            }
            catch (Exception)
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
            StreamId streamId,
            IStreamConsumerExtension streamConsumer,
            StreamSequenceToken token,
            IStreamFilterPredicateWrapper filter)
        {
            if (logger.IsVerbose) logger.Verbose((int)ErrorCode.PersistentStreamPullingAgent_09, "AddSubscriber: Stream={0} Subscriber={1} Token={2}.", streamId, streamConsumer, token);
            AddSubscriber_Impl(streamId, streamConsumer, token, filter);
            return TaskDone.Done;
        }

        // Called by rendezvous when new remote subscriber subscribes to this stream.
        private void AddSubscriber_Impl(
            StreamId streamId,
            IStreamConsumerExtension streamConsumer,
            StreamSequenceToken token,
            IStreamFilterPredicateWrapper filter)
        {
            StreamConsumerCollection streamDataCollection;
            if (!pubSubCache.TryGetValue(streamId, out streamDataCollection))
            {
                streamDataCollection = new StreamConsumerCollection();
                pubSubCache.Add(streamId, streamDataCollection);
            }

            StreamConsumerData data;
            if (!streamDataCollection.TryGetConsumer(streamConsumer, out data))
                data = streamDataCollection.AddConsumer(streamId, streamConsumer, token, filter);
            
            // Set cursor if not cursor is set, or if subscription provides new token
            if (data.Cursor == null || token != null)
                data.Cursor = this.receiver.GetCacheCursor(streamId.Guid, streamId.Namespace, token);
            
            if (data.State == StreamConsumerDataState.Inactive)
                RunConsumerCursor(data, filter).Ignore(); // Start delivering events if not actively doing so
        }

        public Task RemoveSubscriber(StreamId streamId, IStreamConsumerExtension streamConsumer)
        {
            RemoveSubscriber_Impl(streamId, streamConsumer);
            return TaskDone.Done;
        }

        public void RemoveSubscriber_Impl(StreamId streamId, IStreamConsumerExtension streamConsumer)
        {
            StreamConsumerCollection streamData;
            if (!pubSubCache.TryGetValue(streamId, out streamData)) return;

            // remove consumer
            bool removed = streamData.RemoveConsumer(streamConsumer);
            if (removed && logger.IsVerbose) logger.Verbose((int)ErrorCode.PersistentStreamPullingAgent_10, "Removed Consumer: subscriber={0}, for stream {1}.", streamConsumer, streamId);
            
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

                // loop through the queue until it is empty.
                while (true)
                {
                    // Retrive one multiBatch from the queue. Every multiBatch has an IEnumerable of IBatchContainers, each IBatchContainer may have multiple events.
                    IEnumerable<IBatchContainer> msgsEnumerable = await rcvr.GetQueueMessagesAsync();
                    List<IBatchContainer> multiBatch = null;
                    if (msgsEnumerable != null)
                        multiBatch = msgsEnumerable.ToList();
                    
                    if (multiBatch == null || multiBatch.Count == 0) return; // queue is empty. Exit the loop. Will attempt again in the next timer callback.
                    
                    rcvr.AddToCache(multiBatch);
                    numMessages += multiBatch.Count;
                    numReadMessagesCounter.IncrementBy(multiBatch.Count);
                    if (logger.IsVerbose2) logger.Verbose2((int)ErrorCode.PersistentStreamPullingAgent_11, "Got {0} messages from queue {1}. So far {2} msgs from this queue.",
                        multiBatch.Count, myQueueId.ToStringWithHashCode(), numMessages);
                    
                    foreach (var group in multiBatch.Where(m => m != null)
                        .GroupBy(container => new Tuple<Guid, string>(container.StreamGuid, container.StreamNamespace)))
                    {
                        var streamId = StreamId.GetStreamId(group.Key.Item1, queueAdapter.Name, group.Key.Item2);
                        StreamConsumerCollection streamData;
                        if (pubSubCache.TryGetValue(streamId, out streamData))
                            StartInactiveCursors(streamId, streamData); // if this is an existing stream, start any inactive cursors
                        else
                            RegisterStream(streamId, group.First().SequenceToken); // if this is a new stream register as producer of stream in pub sub system
                    }
                }
            }
            catch (Exception exc)
            {
                logger.Error((int)ErrorCode.PersistentStreamPullingAgent_12,
                    String.Format("Exception while PersistentStreamPullingAgentGrain.AsyncTimerCallback"), exc);
            }
        }

        private void RegisterStream(StreamId streamId, StreamSequenceToken firstToken)
        {
            var streamData = new StreamConsumerCollection();
            pubSubCache.Add(streamId, streamData);
            RegisterAsStreamProducer(streamId, firstToken).Ignore();
        }

        private void StartInactiveCursors(StreamId streamId, StreamConsumerCollection streamData)
        {
            // if stream is already registered, just wake inactive consumers
            // get list of inactive consumers
            var streamConsumers = streamData.AllConsumersForStream(streamId)
                .Where(consumer => consumer.State == StreamConsumerDataState.Inactive)
                .ToList();

            // for each inactive stream
            foreach (StreamConsumerData consumerData in streamConsumers)
                RunConsumerCursor(consumerData, consumerData.Filter).Ignore();
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
                        deliveryTask = consumerData.StreamConsumer
                            .DeliverBatch(consumerData.StreamId, batch.AsImmutable());
                    }
                    else if (ex == null)
                    {
                        deliveryTask = consumerData.StreamConsumer.CompleteStream(consumerData.StreamId);
                    }
                    else
                    {
                        deliveryTask = consumerData.StreamConsumer.ErrorInStream(consumerData.StreamId, ex);
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

        private async Task RegisterAsStreamProducer(StreamId streamId, StreamSequenceToken streamStartToken)
        {
            try
            {
                if (pubSub == null) throw new NullReferenceException("Found pubSub reference not set up correctly in RetreaveNewStream");
                
                IStreamProducerExtension meAsStreamProducer = StreamProducerExtensionFactory.Cast(this.AsReference());
                ISet<PubSubSubscriptionState> streamData = await pubSub.RegisterProducer(streamId, streamProviderName, meAsStreamProducer);
                if (logger.IsVerbose) logger.Verbose((int)ErrorCode.PersistentStreamPullingAgent_16, "Got back {0} Subscribers for stream {1}.", streamData.Count, streamId);
                
                foreach (PubSubSubscriptionState item in streamData)
                {
                    var token = item.StreamSequenceToken ?? streamStartToken;
                    AddSubscriber_Impl(item.Stream, item.Consumer, token, item.FilterWrapper);
                }
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