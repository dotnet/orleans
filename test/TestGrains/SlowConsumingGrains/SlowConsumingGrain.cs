﻿using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    /// <summary>
    /// SlowConsumingGrain keep asking to rewind to the first item it received to mimic slow consuming behavior
    /// </summary>
    public class SlowConsumingGrain : Grain, ISlowConsumingGrain
    {
        private Logger logger;
        public SlowObserver<int> ConsumerObserver { get; private set; }
        public StreamSubscriptionHandle<int> ConsumerHandle { get; set; }
        public override Task OnActivateAsync()
        {
            logger = base.GetLogger(this.GetType().Name + base.IdentityString);
            logger.Info("OnActivateAsync");
            ConsumerHandle = null;
            return Task.CompletedTask;
        }

        public Task<int> GetNumberConsumed()
        {
            return Task.FromResult(this.ConsumerObserver.NumConsumed);
        }

        public async Task BecomeConsumer(Guid streamId, string streamNamespace, string providerToUse)
        {
            logger.Info("BecomeConsumer");
            ConsumerObserver = new SlowObserver<int>(this, logger);
            IStreamProvider streamProvider = base.GetStreamProvider(providerToUse);
            var consumer = streamProvider.GetStream<int>(streamId, streamNamespace);
            ConsumerHandle = await consumer.SubscribeAsync(ConsumerObserver);
        }

        public async Task StopConsuming()
        {
            logger.Info("StopConsuming");
            if (ConsumerHandle != null)
            {
                await ConsumerHandle.UnsubscribeAsync();
                ConsumerHandle = null;
            }
        }
    }

    /// <summary>
    /// SlowObserver keep rewind to the first item it received, to mimic slow consuming behavior
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class SlowObserver<T> : IAsyncObserver<T>
    {
        public int NumConsumed { get; private set; }
        private Logger logger;
        private SlowConsumingGrain slowConsumingGrain;
        internal SlowObserver(SlowConsumingGrain grain, Logger logger)
        {
            NumConsumed = 0;
            this.slowConsumingGrain = grain;
            this.logger = logger.GetSubLogger(this.GetType().Name);
        }

        public async Task OnNextAsync(T item, StreamSequenceToken token = null)
        {
            NumConsumed++;
            // slow consumer keep asking for the first item it received to mimic slow consuming behavior
            this.slowConsumingGrain.ConsumerHandle = await this.slowConsumingGrain.ConsumerHandle.ResumeAsync(this.slowConsumingGrain.ConsumerObserver, token);
            this.logger.Info($"Consumer {this.GetHashCode()} OnNextAsync() received item {item.ToString()}, with NumConsumed {NumConsumed}");
        }

        public Task OnCompletedAsync()
        {
            this.logger.Info($"Consumer {this.GetHashCode()} OnCompletedAsync()");
            return Task.CompletedTask;
        }

        public Task OnErrorAsync(Exception ex)
        {
            this.logger.Info($"Consumer {this.GetHashCode()} OnErrorAsync({ex})");
            return Task.CompletedTask;
        }
    }

}
