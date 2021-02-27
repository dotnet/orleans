using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains.ProgrammaticSubscribe
{
    public class TypedProducerGrain<T> : Grain, ITypedProducerGrain
    {
        private IAsyncStream<T> producer;
        protected int numProducedItems;
        private IDisposable producerTimer;
        internal ILogger logger;
        private static readonly TimeSpan defaultFirePeriod = TimeSpan.FromMilliseconds(10);
        private readonly List<Exception> producerExceptions = new();

        public TypedProducerGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public override Task OnActivateAsync()
        {
            logger.Info("OnActivateAsync");
            numProducedItems = 0;
            return Task.CompletedTask;
        }

        public Task BecomeProducer(Guid streamId, string streamNamespace, string providerToUse)
        {
            logger.Info("BecomeProducer");
            IStreamProvider streamProvider = this.GetStreamProvider(providerToUse);
            producer = streamProvider.GetStream<T>(streamId, streamNamespace);
            return Task.CompletedTask;
        }

        public Task StartPeriodicProducing(TimeSpan? firePeriod = null)
        {
            logger.Info("StartPeriodicProducing");
            var period = (firePeriod == null)? defaultFirePeriod : firePeriod;
            producerTimer = base.RegisterTimer(TimerCallback, null, TimeSpan.Zero, period.Value);
            return Task.CompletedTask;
        }

        public Task StopPeriodicProducing()
        {
            logger.Info("StopPeriodicProducing");
            producerTimer.Dispose();
            producerTimer = null;
            if (producerExceptions is { Count: > 0 } exceptions)
            {
                throw new AggregateException("Exceptions occurred while producing messages to stream", exceptions.ToArray());
            }

            return Task.CompletedTask;
        }

        public Task<int> GetNumberProduced()
        {
            logger.Info("GetNumberProduced {0}", numProducedItems);
            return Task.FromResult(numProducedItems);
        }

        public Task ClearNumberProduced()
        {
            numProducedItems = 0;
            return Task.CompletedTask;
        }

        public Task Produce()
        {
            return Fire();
        }

        private Task TimerCallback(object state)
        {
            return producerTimer != null ? Fire() : Task.CompletedTask;
        }

        protected virtual async Task ProducerOnNextAsync(IAsyncStream<T> theProducer)
        {
            try
            {
                await theProducer.OnNextAsync(Activator.CreateInstance<T>());
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Exception producing to stream {StreamId}", theProducer.StreamId);
                producerExceptions.Add(exception);
            }
        }

        private async Task Fire([CallerMemberName] string caller = null)
        {
            numProducedItems++;
            await ProducerOnNextAsync(this.producer);
            logger.Info("{0} (item={1})", caller, numProducedItems);
        }

        public override Task OnDeactivateAsync()
        {
            logger.Info("OnDeactivateAsync");
            return Task.CompletedTask;
        }
    }
    public class TypedProducerGrainProducingInt : TypedProducerGrain<int>, ITypedProducerGrainProducingInt
    {
        public TypedProducerGrainProducingInt(ILoggerFactory loggerFactory) : base(loggerFactory)
        {
        }

        protected override Task ProducerOnNextAsync(IAsyncStream<int> theProducer)
        {
            return theProducer.OnNextAsync(this.numProducedItems);
        }
    }

    public class TypedProducerGrainProducingApple : TypedProducerGrain<Apple>, ITypedProducerGrainProducingApple
    {
        public TypedProducerGrainProducingApple(ILoggerFactory loggerFactory) : base(loggerFactory)
        {
        }

        protected override Task ProducerOnNextAsync(IAsyncStream<Apple> theProducer)
        {
            return theProducer.OnNextAsync(new Apple(this.numProducedItems));
        }
    }

    [GenerateSerializer]
    public class Apple : IFruit
    {
        [Id(0)]
        int number;

        public Apple(int number)
        {
            this.number = number;
        }

        public int GetNumber()
        {
            return number;
        }
    }
}
