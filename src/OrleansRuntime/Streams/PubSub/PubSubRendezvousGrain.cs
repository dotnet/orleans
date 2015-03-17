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
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;

using Orleans.Runtime;

namespace Orleans.Streams
{
    internal interface IPubSubGrainState : IGrainState
    {
        HashSet<PubSubPublisherState> Producers { get; set; }
        HashSet<PubSubSubscriptionState> Consumers { get; set; }
    }

    [Orleans.Providers.StorageProvider(ProviderName = "PubSubStore")]
    internal class PubSubRendezvousGrain : Grain<IPubSubGrainState>, IPubSubRendezvousGrain
    {
        private Logger logger;
        private const bool DEBUG_PUB_SUB = false;

        private static readonly CounterStatistic counterProducersAdded;
        private static readonly CounterStatistic counterProducersRemoved;
        private static readonly CounterStatistic counterProducersTotal;
        private static readonly CounterStatistic counterConsumersAdded;
        private static readonly CounterStatistic counterConsumersRemoved;
        private static readonly CounterStatistic counterConsumersTotal;

        static PubSubRendezvousGrain()
        {
            counterProducersAdded   = CounterStatistic.FindOrCreate(StatisticNames.STREAMS_PUBSUB_PRODUCERS_ADDED);
            counterProducersRemoved = CounterStatistic.FindOrCreate(StatisticNames.STREAMS_PUBSUB_PRODUCERS_REMOVED);
            counterProducersTotal   = CounterStatistic.FindOrCreate(StatisticNames.STREAMS_PUBSUB_PRODUCERS_TOTAL);
            counterConsumersAdded   = CounterStatistic.FindOrCreate(StatisticNames.STREAMS_PUBSUB_CONSUMERS_ADDED);
            counterConsumersRemoved = CounterStatistic.FindOrCreate(StatisticNames.STREAMS_PUBSUB_CONSUMERS_REMOVED);
            counterConsumersTotal   = CounterStatistic.FindOrCreate(StatisticNames.STREAMS_PUBSUB_CONSUMERS_TOTAL);
        }

        public override async Task OnActivateAsync()
        {
            logger = GetLogger(this.GetType().Name + "-" + RuntimeIdentity + "-" + IdentityString);
            LogPubSubCounts("OnActivateAsync");

            int numRemoved = RemoveDeadProducers();
            if (numRemoved > 0)
                await State.WriteStateAsync();
            if (State.Consumers == null)
                State.Consumers = new HashSet<PubSubSubscriptionState>();
            if (State.Producers == null)
                State.Producers = new HashSet<PubSubPublisherState>();
            if (logger.IsVerbose)
                logger.Info("OnActivateAsync-Done");
        }

        private int RemoveDeadProducers()
        {
            // Remove only those we know for sure are Dead.
            int numRemoved = 0;
            if (State.Producers != null && State.Producers.Count > 0)
                numRemoved = State.Producers.RemoveWhere(producerState => IsDeadProducer(producerState.Producer));
            
            if (numRemoved > 0)
            {
                LogPubSubCounts("RemoveDeadProducers: removed {0} outdated producers", numRemoved);
                counterProducersRemoved.Increment();
                counterProducersTotal.DecrementBy(numRemoved);
            }
            return numRemoved;
        }

        /// accept and notify only Active producers.
        private static bool IsActiveProducer(IStreamProducerExtension producer)
        {
            var grainRef = producer as GrainReference;
            if (grainRef !=null && grainRef.GrainId.IsSystemTarget && grainRef.IsInitializedSystemTarget)
                return RuntimeClient.Current.GetSiloStatus(grainRef.SystemTargetSilo).Equals(SiloStatus.Active);
            
            return true;
        }

        private static bool IsDeadProducer(IStreamProducerExtension producer)
        {
            var grainRef = producer as GrainReference;
            if (grainRef != null && grainRef.GrainId.IsSystemTarget && grainRef.IsInitializedSystemTarget)
                return RuntimeClient.Current.GetSiloStatus(grainRef.SystemTargetSilo).Equals(SiloStatus.Dead);
            
            return false;
        }

        public async Task<ISet<PubSubSubscriptionState>> RegisterProducer(StreamId streamId, IStreamProducerExtension streamProducer)
        {
            if (!IsActiveProducer(streamProducer))
                throw new ArgumentException(String.Format("Trying to register non active IStreamProducerExtension: {0}", streamProducer.ToString()), "streamProducer");
            
            RemoveDeadProducers();
            
            var publisherState = new PubSubPublisherState(streamId, streamProducer);
            State.Producers.Add(publisherState);
            counterProducersAdded.Increment();
            counterProducersTotal.Increment();
            LogPubSubCounts("RegisterProducer {0}", streamProducer);
            await State.WriteStateAsync();
            return State.Consumers;
        }

        public async Task UnregisterProducer(StreamId streamId, IStreamProducerExtension streamProducer)
        {
            int numRemoved = State.Producers.RemoveWhere(s => s.Equals(streamId, streamProducer));
            counterProducersRemoved.Increment();
            counterProducersTotal.DecrementBy(numRemoved);
            LogPubSubCounts("UnregisterProducer {0} NumRemoved={1}", streamProducer, numRemoved);

            if (numRemoved > 0)
                await State.WriteStateAsync();
            
            if (State.Producers.Count == 0 && State.Consumers.Count == 0)
                DeactivateOnIdle(); // No producers or consumers left now, so flag ourselves to expedite Deactivation
        }

        public async Task RegisterConsumer(
            StreamId streamId, 
            IStreamConsumerExtension streamConsumer, 
            StreamSequenceToken token, 
            IStreamFilterPredicateWrapper filter)
        {
            // This Where clause will return either zero or one PubSubSubscriptionState
            var found = State.Consumers.Where(s => s.Equals(streamId, streamConsumer)).ToArray();
            PubSubSubscriptionState pubSubState;
            if (found.Length == 0)
            {
                pubSubState = new PubSubSubscriptionState(streamId, streamConsumer, token, filter);
                State.Consumers.Add(pubSubState);
            }
            else
            {
                pubSubState = found[0];
                if (filter != null)
                    pubSubState.AddFilter(filter);
            }
            
            counterConsumersAdded.Increment();
            counterConsumersTotal.Increment();

            LogPubSubCounts("RegisterConsumer {0}", streamConsumer);
            await State.WriteStateAsync();

            int numProducers = State.Producers.Count;
            if (numProducers > 0)
            {
                if (logger.IsVerbose)
                    logger.Info("Notifying {0} existing producer(s) about new consumer {1}. Producers={2}", 
                        numProducers, streamConsumer, Utils.EnumerableToString(State.Producers));
                
                // Notify producers about a new streamConsumer.
                var tasks = new List<Task>();
                var producers = State.Producers.ToList();
                bool someProducersRemoved = false;

                foreach (var producerState in producers)
                {
                    PubSubPublisherState producer = producerState; // Capture loop variable

                    if (!IsActiveProducer(producer.Producer))
                    {
                        // Producer is not active (could be stopping / shutting down) so skip
                        if (logger.IsVerbose) logger.Verbose("Producer {0} on stream {1} is not active - skipping.", producer, streamId);
                        continue;
                    }

                    Task addSubscriberPromise = producer.Producer.AddSubscriber(streamId, streamConsumer, token, filter)
                        .ContinueWith(t =>
                        {
                            if (t.IsFaulted)
                            {
                                var exc = t.Exception.GetBaseException();
                                if (exc is GrainExtensionNotInstalledException)
                                {
                                    logger.Warn((int) ErrorCode.Stream_ProducerIsDead,
                                        "Producer {0} on stream {1} is no longer active - discarding.", 
                                        producer, streamId);

                                    // This publisher has gone away, so we should cleanup pub-sub state.
                                    bool removed = State.Producers.Remove(producer);
                                    someProducersRemoved = true; // Re-save state changes at end
                                    counterProducersRemoved.Increment();
                                    counterProducersTotal.DecrementBy(removed ? 1 : 0);

                                    // And ignore this error
                                }
                                else
                                {
                                    throw exc;
                                }
                            }
                        }, TaskContinuationOptions.OnlyOnFaulted);
                    tasks.Add(addSubscriberPromise);
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

                if (someProducersRemoved)
                    await State.WriteStateAsync();

                if (exception != null)
                    throw exception;
            }
        }

        public async Task UnregisterConsumer(StreamId streamId, IStreamConsumerExtension streamConsumer)
        {
            int numRemoved = State.Consumers.RemoveWhere(c => c.Equals(streamId, streamConsumer));
            counterConsumersRemoved.Increment();
            counterConsumersTotal.DecrementBy(numRemoved);

            LogPubSubCounts("UnregisterConsumer {0} NumRemoved={1}", streamConsumer, numRemoved);
            await State.WriteStateAsync();

            int numProducers = State.Producers.Count;
            if (numProducers > 0)
            {
                if (logger.IsVerbose) logger.Verbose("Notifying {0} existing producers about unregistered consumer.", numProducers);
                
                // Notify producers about unregistered consumer.
                var tasks = new List<Task>();
                foreach (var producerState in State.Producers
                    .Where(producerState => IsActiveProducer(producerState.Producer)))
                        tasks.Add(producerState.Producer.RemoveSubscriber(streamId, streamConsumer));
                
                await Task.WhenAll(tasks);
            }
            else if (State.Consumers.Count == 0) // + we already know that numProducers == 0 from previous if-clause
            {
                // No producers or consumers left now, so flag ourselves to expedite Deactivation
                DeactivateOnIdle();
            }
        }

        public Task<int> ProducerCount(StreamId streamId)
        {
            return Task.FromResult(State.Producers.Count);
        }

        public Task<int> ConsumerCount(StreamId streamId)
        {
            return Task.FromResult(GetConsumersForStream(streamId).Length);
        }

        public Task<PubSubSubscriptionState[]> DiagGetConsumers(StreamId streamId)
        {
            return Task.FromResult(GetConsumersForStream(streamId));
        }

        private PubSubSubscriptionState[] GetConsumersForStream(StreamId streamId)
        {
            return State.Consumers.Where(c => c.Stream.Equals(streamId)).ToArray();
        }

        private void LogPubSubCounts(string fmt, params object[] args)
        {
            if (logger.IsVerbose)
            {
                int numProducers = 0;
                int numConsumers = 0;
                if (State != null && State.Producers != null)
                    numProducers = State.Producers.Count;
                if (State != null && State.Consumers != null)
                    numConsumers = State.Consumers.Count;
                
                string when = args != null && args.Length != 0 ? String.Format(fmt, args) : fmt;
                logger.Info("{0}. Now have total of {1} producers and {2} consumers. All Consumers = {3}, All Producers = {4}",
                    when, numProducers, numConsumers, Utils.EnumerableToString(State.Consumers), Utils.EnumerableToString(State.Producers));
            }
        }

        // Check that what we have cached locally matches what is in the persistent table.
        public async Task Validate()
        {
            var captureProducers = State.Producers;
            var captureConsumers = State.Consumers;

            await State.ReadStateAsync();
            
            if (captureProducers.Count != State.Producers.Count)
            {
                throw new Exception(String.Format("State mismatch between PubSubRendezvousGrain and its persistent state. captureProducers.Count={0}, State.Producers.Count={1}",
                    captureProducers.Count, State.Producers.Count));
            }

            foreach (var producer in captureProducers)
                if (!State.Producers.Contains(producer))
                    throw new Exception(String.Format("State mismatch between PubSubRendezvousGrain and its persistent state. captureProducers={0}, State.Producers={1}",
                        Utils.EnumerableToString(captureProducers), Utils.EnumerableToString(State.Producers)));

            if (captureConsumers.Count != State.Consumers.Count)
            {
                LogPubSubCounts("Validate: Consumer count mismatch");
                throw new Exception(String.Format("State mismatch between PubSubRendezvousGrain and its persistent state. captureConsumers.Count={0}, State.Consumers.Count={1}",
                        captureConsumers.Count, State.Consumers.Count));
            }

            foreach (PubSubSubscriptionState consumer in captureConsumers)
                if (!State.Consumers.Contains(consumer))
                    throw new Exception(String.Format("State mismatch between PubSubRendezvousGrain and its persistent state. captureConsumers={0}, State.Consumers={1}",
                        Utils.EnumerableToString(captureConsumers), Utils.EnumerableToString(State.Consumers)));
        }
    }
}