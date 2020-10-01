using System;
using System.Threading.Tasks;
using Common;
using GrainInterfaces;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Streams;
using Orleans.Streams.Core;

namespace Grains
{
    // ImplicitStreamSubscription attribute here is to subscribe implicitely to all stream within
    // a given namespace: whenever some data is pushed to the streams of namespace Constants.StreamNamespace,
    // a grain of type ConsumerGrain with the same guid of the stream will receive the message.
    // Even if no activations of the grain currently exist, the runtime will automatically
    // create a new one and send the message to it.
    [ImplicitStreamSubscription(Constants.StreamNamespace)]
    public class ConsumerGrain : Grain, IConsumerGrain, IStreamSubscriptionObserver
    {
        private readonly ILogger<IConsumerGrain> logger;

        private readonly LoggerObserver observer;

        /// <summary>
        /// Class that will log streaming events
        /// </summary>
        private class LoggerObserver : IAsyncObserver<int>
        {
            private ILogger<IConsumerGrain> logger;

            public LoggerObserver(ILogger<IConsumerGrain> logger)
            {
                this.logger = logger;
            }

            public Task OnCompletedAsync()
            {
                this.logger.LogInformation("OnCompletedAsync");
                return Task.CompletedTask;
            }

            public Task OnErrorAsync(Exception ex)
            {
                this.logger.LogInformation("OnErrorAsync: {Exception}", ex);
                return Task.CompletedTask;
            }

            public Task OnNextAsync(int item, StreamSequenceToken token = null)
            {
                this.logger.LogInformation("OnNextAsync: item: {Item}, token = {Token}", item, token);
                return Task.CompletedTask;
            }
        }

        public ConsumerGrain(ILogger<IConsumerGrain> logger)
        {
            this.logger = logger;
            this.observer = new LoggerObserver(this.logger);
        }

        // Called when a subscription is added
        public async Task OnSubscribed(IStreamSubscriptionHandleFactory handleFactory)
        {
            // Plug our LoggerObserver to the stream
            var handle = handleFactory.Create<int>();
            await handle.ResumeAsync(this.observer);
        }

        public override Task OnActivateAsync()
        {
            this.logger.LogInformation("OnActivateAsync");
            return Task.CompletedTask;
        }
    }
}
