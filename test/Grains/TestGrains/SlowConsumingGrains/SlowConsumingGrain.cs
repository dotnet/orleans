using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using System;
using System.Threading;
using System.Threading.Tasks;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    /// <summary>
    /// SlowConsumingGrain keep asking to rewind to the first item it received to mimic slow consuming behavior
    /// </summary>
    public class SlowConsumingGrain : Grain, ISlowConsumingGrain
    {
        private ILogger logger;
        public SlowObserver<int> ConsumerObserver { get; private set; }
        public StreamSubscriptionHandle<int> ConsumerHandle { get; set; }

        public SlowConsumingGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("OnActivateAsync");
            ConsumerHandle = null;
            return Task.CompletedTask;
        }

        public Task<int> GetNumberConsumed()
        {
            return Task.FromResult(this.ConsumerObserver.NumConsumed);
        }

        public async Task BecomeConsumer(Guid streamId, string streamNamespace, string providerToUse)
        {
            logger.LogInformation("BecomeConsumer");
            ConsumerObserver = new SlowObserver<int>(this, logger);
            IStreamProvider streamProvider = this.GetStreamProvider(providerToUse);
            var consumer = streamProvider.GetStream<int>(streamId, streamNamespace);
            ConsumerHandle = await consumer.SubscribeAsync(ConsumerObserver);
        }

        public async Task StopConsuming()
        {
            logger.LogInformation("StopConsuming");
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
        private ILogger logger;
        private SlowConsumingGrain slowConsumingGrain;
        private StreamSequenceToken firstToken;

        internal SlowObserver(SlowConsumingGrain grain, ILogger logger)
        {
            NumConsumed = 0;
            this.slowConsumingGrain = grain;
            this.logger = logger;
        }

        public async Task OnNextAsync(T item, StreamSequenceToken token = null)
        {
            NumConsumed++;

            if (firstToken == null)
            {
                firstToken = token;
            }
            else
            {
                // slow consumer keep asking for the first item it received to mimic slow consuming behavior
                this.slowConsumingGrain.ConsumerHandle = await this.slowConsumingGrain.ConsumerHandle.ResumeAsync(this.slowConsumingGrain.ConsumerObserver, firstToken);
            }

            this.logger.LogInformation("Consumer {HashCode} OnNextAsync() received item {Item}, with NumConsumed {NumConsumed}", this.GetHashCode(), item, NumConsumed);
        }

        public Task OnCompletedAsync()
        {
            this.logger.LogInformation("Consumer {HashCode} OnCompletedAsync()", this.GetHashCode());
            return Task.CompletedTask;
        }

        public Task OnErrorAsync(Exception ex)
        {
            this.logger.LogInformation(ex, "Consumer {HashCode} OnErrorAsync()", this.GetHashCode());
            return Task.CompletedTask;
        }
    }

}
