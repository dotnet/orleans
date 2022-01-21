using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    internal class SampleConsumerObserver<T> : IAsyncObserver<T>
    {
        private readonly SampleStreaming_ConsumerGrain hostingGrain;

        internal SampleConsumerObserver(SampleStreaming_ConsumerGrain hostingGrain)
        {
            this.hostingGrain = hostingGrain;
        }

        public Task OnNextAsync(T item, StreamSequenceToken token = null)
        {
            hostingGrain.logger.Info("OnNextAsync(item={0}, token={1})", item, token != null ? token.ToString() : "null");
            hostingGrain.numConsumedItems++;
            return Task.CompletedTask;
        }

        public Task OnCompletedAsync()
        {
            hostingGrain.logger.Info("OnCompletedAsync()");
            return Task.CompletedTask;
        }

        public Task OnErrorAsync(Exception ex)
        {
            hostingGrain.logger.Info("OnErrorAsync({0})", ex);
            return Task.CompletedTask;
        }
    }

    public class SampleStreaming_ProducerGrain : Grain, ISampleStreaming_ProducerGrain
    {
        private IAsyncStream<int> producer;
        private int numProducedItems;
        private IDisposable producerTimer;
        internal ILogger logger;
        internal readonly static string RequestContextKey = "RequestContextField";
        internal readonly static string RequestContextValue = "JustAString";

        public SampleStreaming_ProducerGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            logger.Info("OnActivateAsync");
            numProducedItems = 0;
            return Task.CompletedTask;
        }

        public Task BecomeProducer(Guid streamId, string streamNamespace, string providerToUse)
        {
            logger.Info("BecomeProducer");
            IStreamProvider streamProvider = this.GetStreamProvider(providerToUse);
            producer = streamProvider.GetStream<int>(streamId, streamNamespace);
            return Task.CompletedTask;
        }

        public Task StartPeriodicProducing()
        {
            logger.Info("StartPeriodicProducing");
            producerTimer = base.RegisterTimer(TimerCallback, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(10));
            return Task.CompletedTask;
        }

        public Task StopPeriodicProducing()
        {
            logger.Info("StopPeriodicProducing");
            producerTimer.Dispose();
            producerTimer = null;
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
            return producerTimer != null? Fire(): Task.CompletedTask;
        }

        private async Task Fire([CallerMemberName] string caller = null)
        {
            RequestContext.Set(RequestContextKey, RequestContextValue);
            await producer.OnNextAsync(numProducedItems);
            numProducedItems++;
            logger.Info("{0} (item={1})", caller, numProducedItems);
        }

        public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            logger.Info("OnDeactivateAsync");
            return Task.CompletedTask;
        }
    }

    public class SampleStreaming_ConsumerGrain : Grain, ISampleStreaming_ConsumerGrain
    {
        private IAsyncObservable<int> consumer;
        internal int numConsumedItems;
        internal ILogger logger;
        private IAsyncObserver<int> consumerObserver;
        private StreamSubscriptionHandle<int> consumerHandle;

        public SampleStreaming_ConsumerGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            logger.Info("OnActivateAsync");
            numConsumedItems = 0;
            consumerHandle = null;
            return Task.CompletedTask;
        }

        public async Task BecomeConsumer(Guid streamId, string streamNamespace, string providerToUse)
        {
            logger.Info("BecomeConsumer");
            consumerObserver = new SampleConsumerObserver<int>(this);
            IStreamProvider streamProvider = this.GetStreamProvider(providerToUse);
            consumer = streamProvider.GetStream<int>(streamId, streamNamespace);
            consumerHandle = await consumer.SubscribeAsync(consumerObserver);
        }

        public async Task StopConsuming()
        {
            logger.Info("StopConsuming");
            if (consumerHandle != null)
            {
                await consumerHandle.UnsubscribeAsync();
                consumerHandle = null;
            }
        }

        public Task<int> GetNumberConsumed()
        {
            return Task.FromResult(numConsumedItems);
        }

        public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            logger.Info("OnDeactivateAsync");
            return Task.CompletedTask;
        }
    }

    public class SampleStreaming_InlineConsumerGrain : Grain, ISampleStreaming_InlineConsumerGrain
    {
        private IAsyncObservable<int> consumer;
        internal int numConsumedItems;
        internal ILogger logger;
        private StreamSubscriptionHandle<int> consumerHandle;

        public SampleStreaming_InlineConsumerGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            logger.Info( "OnActivateAsync" );
            numConsumedItems = 0;
            consumerHandle = null;
            return Task.CompletedTask;
        }

        public async Task BecomeConsumer(Guid streamId, string streamNamespace, string providerToUse)
        {
            logger.Info( "BecomeConsumer" );
            IStreamProvider streamProvider = this.GetStreamProvider( providerToUse );
            consumer = streamProvider.GetStream<int>(streamId, streamNamespace);
            consumerHandle = await consumer.SubscribeAsync( OnNextAsync, OnErrorAsync, OnCompletedAsync );
        }

        public async Task StopConsuming()
        {
            logger.Info( "StopConsuming" );
            if ( consumerHandle != null )
            {
                await consumerHandle.UnsubscribeAsync();
                //consumerHandle.Dispose();
                consumerHandle = null;
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
            return Task.CompletedTask;
        }

        public Task OnCompletedAsync()
        {
            logger.Info( "OnCompletedAsync()" );
            return Task.CompletedTask;
        }

        public Task OnErrorAsync( Exception ex )
        {
            logger.Info( "OnErrorAsync({0})", ex );
            return Task.CompletedTask;
        }

        public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            logger.Info("OnDeactivateAsync");
            return Task.CompletedTask;
        }
    }
}
