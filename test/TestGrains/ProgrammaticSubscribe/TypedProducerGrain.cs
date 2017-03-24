﻿using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains.ProgrammaticSubscribe
{

    public class TypedProducerGrain<T> : Grain, ITypedProducerGrain
    {
        private IAsyncStream<T> producer;
        private int numProducedItems;
        private IDisposable producerTimer;
        internal Logger logger;
        private static readonly TimeSpan defaultFirePeriod = TimeSpan.FromMilliseconds(10);
        public override Task OnActivateAsync()
        {
            logger = base.GetLogger(this.GetType() + base.IdentityString);
            logger.Info("OnActivateAsync");
            numProducedItems = 0;
            return TaskDone.Done;
        }

        public Task BecomeProducer(Guid streamId, string streamNamespace, string providerToUse)
        {
            logger.Info("BecomeProducer");
            IStreamProvider streamProvider = base.GetStreamProvider(providerToUse);
            producer = streamProvider.GetStream<T>(streamId, streamNamespace);
            return TaskDone.Done;
        }

        public Task StartPeriodicProducing(TimeSpan? firePeriod = null)
        {
            logger.Info("StartPeriodicProducing");
            var period = (firePeriod == null)? defaultFirePeriod : firePeriod;
            producerTimer = base.RegisterTimer(TimerCallback, null, TimeSpan.Zero, period.Value);
            return TaskDone.Done;
        }

        public Task StopPeriodicProducing()
        {
            logger.Info("StopPeriodicProducing");
            producerTimer.Dispose();
            producerTimer = null;
            return TaskDone.Done;
        }

        public Task<int> GetNumberProduced()
        {
            logger.Info("GetNumberProduced {0}", numProducedItems);
            return Task.FromResult(numProducedItems);
        }

        public Task ClearNumberProduced()
        {
            numProducedItems = 0;
            return TaskDone.Done;
        }

        public Task Produce()
        {
            return Fire();
        }

        private Task TimerCallback(object state)
        {
            return producerTimer != null ? Fire() : TaskDone.Done;
        }

        protected virtual Task ProducerOnNextAsync(IAsyncStream<T> theProducer)
        {
            return theProducer.OnNextAsync(Activator.CreateInstance<T>());
        }

        private async Task Fire([CallerMemberName] string caller = null)
        {
            await ProducerOnNextAsync(this.producer);
            numProducedItems++;
            logger.Info("{0} (item={1})", caller, numProducedItems);
        }

        public override Task OnDeactivateAsync()
        {
            logger.Info("OnDeactivateAsync");
            return TaskDone.Done;
        }
    }
    public class TypedProducerGrainProducingInt : TypedProducerGrain<int>, ITypedProducerGrainProducingInt
    {
        protected override Task ProducerOnNextAsync(IAsyncStream<int> theProducer)
        {
            return theProducer.OnNextAsync(0);
        }
    }

    public class TypedProducerGrainProducingString : TypedProducerGrain<string>, ITypedProducerGrainProducingString
    {
        protected override Task ProducerOnNextAsync(IAsyncStream<string> theProducer)
        {
            return theProducer.OnNextAsync("o");
        }
    }
}
