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
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;

namespace UnitTests.SampleStreaming
{
    internal class SampleConsumerObserver<T> : IAsyncObserver<T>
    {
        private SampleStreaming_ConsumerGrain hostingGrain;

        internal SampleConsumerObserver(SampleStreaming_ConsumerGrain _hostingGrain)
        {
            hostingGrain = _hostingGrain;
        }

        public Task OnNextAsync(T item, StreamSequenceToken token = null)
        {
            hostingGrain.logger.Info("OnNextAsync({0}{1})", item, token != null ? token.ToString() : "null");
            hostingGrain.numConsumedItems++;
            return TaskDone.Done;
        }

        public Task OnCompletedAsync()
        {
            hostingGrain.logger.Info("OnCompletedAsync()");
            return TaskDone.Done;
        }

        public Task OnErrorAsync(Exception ex)
        {
            hostingGrain.logger.Info("OnErrorAsync({0})", ex);
            return TaskDone.Done;
        }
    }

    public class SampleStreaming_ProducerGrain : Grain, ISampleStreaming_ProducerGrain
    {
        private IAsyncStream<int> producer;
        private int numProducedItems;
        private IDisposable producerTimer;
        internal Logger logger;
        internal static readonly string StreamNamespace = "SampleStreamNamespace";

        public override Task OnActivateAsync()
        {
            logger = base.GetLogger("SampleStreaming_ProducerGrain " + base.IdentityString);
            logger.Info("OnActivateAsync");
            numProducedItems = 0;
            return TaskDone.Done;
        }

        public Task BecomeProducer(Guid streamId, string providerToUse)
        {
            logger.Info("BecomeProducer");
            IStreamProvider streamProvider = base.GetStreamProvider(providerToUse);
            producer = streamProvider.GetStream<int>(streamId, SampleStreaming_ProducerGrain.StreamNamespace);
            return TaskDone.Done;
        }

        public Task StartPeriodicProducing()
        {
            logger.Info("StartProducing");
            producerTimer = base.RegisterTimer(TimerCallback, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(10));
            return TaskDone.Done;
        }

        public Task StopPeriodicProducing()
        {
            logger.Info("StopProducing");
            producerTimer.Dispose();
            producerTimer = null;
            return TaskDone.Done;
        }

        public Task<int> GetNumberProduced()
        {
            return Task.FromResult(numProducedItems);
        }

        private Task TimerCallback(object state)
        {
            if (producerTimer != null)
            {
                numProducedItems++;
                logger.Info("TimerCallback ({0})", numProducedItems);
                return producer.OnNextAsync(numProducedItems);
            }
            return TaskDone.Done;
        }
    }

    public class SampleStreaming_ConsumerGrain : Grain, ISampleStreaming_ConsumerGrain
    {
        private IAsyncObservable<int> consumer;
        internal int numConsumedItems;
        internal Logger logger;
        private IAsyncObserver<int> consumerObserver;
        private StreamSubscriptionHandle<int> consumerInterface;

        public override Task OnActivateAsync()
        {
            logger = base.GetLogger("SampleStreaming_ConsumerGrain " + base.IdentityString);
            logger.Info("OnActivateAsync");
            numConsumedItems = 0;
            consumerInterface = null;
            return TaskDone.Done;
        }

        public async Task BecomeConsumer(Guid streamId, string providerToUse)
        {
            logger.Info("BecomeConsumer");
            consumerObserver = new SampleConsumerObserver<int>(this);
            IStreamProvider streamProvider = base.GetStreamProvider(providerToUse);
            consumer = streamProvider.GetStream<int>(streamId, SampleStreaming_ProducerGrain.StreamNamespace);
            consumerInterface = await consumer.SubscribeAsync(consumerObserver);
        }

        public async Task StopConsuming()
        {
            logger.Info("StopConsuming");
            if (consumerInterface != null)
            {
                await consumer.UnsubscribeAsync(consumerInterface);
                //consumerInterface.Dispose();
                consumerInterface = null;
            }
        }

        public Task<int> GetNumberConsumed()
        {
            return Task.FromResult(numConsumedItems);
        }
    }

    public class SampleStreaming_InlineConsumerGrain : Grain, ISampleStreaming_InlineConsumerGrain
    {
        private IAsyncObservable<int> consumer;
        internal int numConsumedItems;
        internal Logger logger;
        private StreamSubscriptionHandle<int> consumerInterface;

        public override Task OnActivateAsync()
        {
            logger = base.GetLogger( "SampleStreaming_InlineConsumerGrain " + base.IdentityString );
            logger.Info( "OnActivateAsync" );
            numConsumedItems = 0;
            consumerInterface = null;
            return TaskDone.Done;
        }

        public async Task BecomeConsumer( Guid streamId, string providerToUse )
        {
            logger.Info( "BecomeConsumer" );
            IStreamProvider streamProvider = base.GetStreamProvider( providerToUse );
            consumer = streamProvider.GetStream<int>( streamId, SampleStreaming_ProducerGrain.StreamNamespace );
            consumerInterface = await consumer.SubscribeAsync( OnNextAsync, OnErrorAsync, OnCompletedAsync );
        }

        public async Task StopConsuming()
        {
            logger.Info( "StopConsuming" );
            if ( consumerInterface != null )
            {
                await consumer.UnsubscribeAsync( consumerInterface );
                //consumerInterface.Dispose();
                consumerInterface = null;
            }
        }

        public Task<int> GetNumberConsumed()
        {
            return Task.FromResult( numConsumedItems );
        }

        public Task OnNextAsync( int item, StreamSequenceToken token = null )
        {
            logger.Info( "OnNextAsync({0}{1})", item, token != null ? token.ToString() : "null" );
            numConsumedItems++;
            return TaskDone.Done;
        }

        public Task OnCompletedAsync()
        {
            logger.Info( "OnCompletedAsync()" );
            return TaskDone.Done;
        }

        public Task OnErrorAsync( Exception ex )
        {
            logger.Info( "OnErrorAsync({0})", ex );
            return TaskDone.Done;
        }
    }
}