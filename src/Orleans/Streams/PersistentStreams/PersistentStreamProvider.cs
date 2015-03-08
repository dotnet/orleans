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
using System.Threading.Tasks;

using Orleans.Runtime;
using Orleans.Concurrency;
using Orleans.Runtime.Configuration;
using Orleans.Streams;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Persistent stream provider that uses an adapter for persistence
    /// </summary>
    /// <typeparam name="TAdapterFactory"></typeparam>
    public class PersistentStreamProvider<TAdapterFactory> : IStreamProvider, IStreamProviderImpl
        where TAdapterFactory : IQueueAdapterFactory, new()
    {
        private Logger                  logger;
        private IStreamProviderRuntime  providerRuntime;
        private IQueueAdapter           queueAdapter;
        private TimeSpan                getQueueMsgsTimerPeriod;
        private TimeSpan                initQueueTimeout;
        private StreamQueueBalancerType balancerType;

        private const string GET_QUEUE_MESSAGES_TIMER_PERIOD = "GetQueueMessagesTimerPeriod";
        private const string INIT_QUEUE_TIMEOUT = "InitQueueTimeout";
        private const string QUEUE_BALANCER_TYPE = "QueueBalancerType";
        private readonly TimeSpan DEFAULT_GET_QUEUE_MESSAGES_TIMER_PERIOD = TimeSpan.FromMilliseconds(100);
        private readonly TimeSpan DEFAULT_INIT_QUEUE_TIMEOUT = TimeSpan.FromSeconds(5);
        private const StreamQueueBalancerType DEFAULT_STREAM_QUEUE_BALANCER_TYPE = StreamQueueBalancerType.ConsistentRingBalancer;

        public string                   Name { get; private set; }
        public bool IsRewindable { get { return queueAdapter.IsRewindable; } }

        public async Task Init(string name, IProviderRuntime providerUtilitiesManager, IProviderConfiguration config)
        {
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException("name");
            if (providerUtilitiesManager == null) throw new ArgumentNullException("providerUtilitiesManager");
            if (config == null) throw new ArgumentNullException("config");
            
            Name = name;
            providerRuntime = (IStreamProviderRuntime)providerUtilitiesManager;
            logger = providerRuntime.GetLogger(this.GetType().Name);
            IQueueAdapterFactory factory = new TAdapterFactory();
            factory.Init(config, Name, logger);
            queueAdapter = await factory.CreateAdapter();

            string timePeriod;
            if (!config.Properties.TryGetValue(GET_QUEUE_MESSAGES_TIMER_PERIOD, out timePeriod))
                getQueueMsgsTimerPeriod = DEFAULT_GET_QUEUE_MESSAGES_TIMER_PERIOD;
            else
                getQueueMsgsTimerPeriod = ConfigUtilities.ParseTimeSpan(timePeriod, 
                    "Invalid time value for the " + GET_QUEUE_MESSAGES_TIMER_PERIOD + " property in the provider config values.");
            
            string timeout;
            if (!config.Properties.TryGetValue(INIT_QUEUE_TIMEOUT, out timeout))
                initQueueTimeout = DEFAULT_INIT_QUEUE_TIMEOUT;
            else
                initQueueTimeout = ConfigUtilities.ParseTimeSpan(timeout,
                    "Invalid time value for the " + INIT_QUEUE_TIMEOUT + " property in the provider config values.");
            
            string balanceTypeString;
            balancerType = !config.Properties.TryGetValue(QUEUE_BALANCER_TYPE, out balanceTypeString)
                ? DEFAULT_STREAM_QUEUE_BALANCER_TYPE
                : (StreamQueueBalancerType)Enum.Parse(typeof(StreamQueueBalancerType), balanceTypeString);

            logger.Info("Initialized PersistentStreamProvider<{0}> with name {1}, {2} = {3}, {4} = {5} and with Adapter {6}.",
                typeof(TAdapterFactory).Name, Name, 
                GET_QUEUE_MESSAGES_TIMER_PERIOD, getQueueMsgsTimerPeriod,
                INIT_QUEUE_TIMEOUT, this.initQueueTimeout, 
                queueAdapter.Name);
        }

        public async Task Start()
        {
            if (queueAdapter.Direction.Equals(StreamProviderDirection.ReadOnly) ||
                queueAdapter.Direction.Equals(StreamProviderDirection.ReadWrite))
            {
                await providerRuntime.StartPullingAgents(Name, balancerType, queueAdapter, getQueueMsgsTimerPeriod, initQueueTimeout);
            }
        }

        public IAsyncStream<T> GetStream<T>(Guid id, string streamNamespace)
        {
            var streamId = StreamId.GetStreamId(id, Name, streamNamespace);
            return providerRuntime.GetStreamDirectory().GetOrAddStream<T>(
                streamId, () => new StreamImpl<T>(streamId, this, IsRewindable));
        }

        public IAsyncBatchObserver<T> GetProducerInterface<T>(IAsyncStream<T> stream)
        {
            return new PersistentStreamProducer<T>((StreamImpl<T>)stream, providerRuntime, queueAdapter, IsRewindable);
        }

        public IAsyncObservable<T> GetConsumerInterface<T>(IAsyncStream<T> stream)
        {
            return new StreamConsumer<T>((StreamImpl<T>)stream, Name, providerRuntime, providerRuntime.PubSub(StreamPubSubType.GrainBased), IsRewindable);
        }
    }
}
