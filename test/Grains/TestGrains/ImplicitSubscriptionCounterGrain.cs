using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Streams;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    [ImplicitStreamSubscription(nameof(IImplicitSubscriptionCounterGrain))]
    public class ImplicitSubscriptionCounterGrain : Grain, IImplicitSubscriptionCounterGrain
    {
        private readonly ILogger logger;
        private int eventCounter = 0;
        private int errorCounter = 0;

        public ImplicitSubscriptionCounterGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{nameof(ImplicitSubscriptionCounterGrain)} {this.IdentityString}");
        }

        public override async Task OnActivateAsync()
        {
            this.logger.LogInformation("OnActivateAsync");

            var stream = this
                .GetStreamProvider("StreamingCacheMissTests")
                .GetStream<byte[]>(this.GetPrimaryKey(), nameof(IImplicitSubscriptionCounterGrain));
            await stream.SubscribeAsync(OnNext, OnError, OnCompleted);

            Task OnNext(byte[] value, StreamSequenceToken token)
            {
                this.logger.LogInformation("Received: [{Value} {Token}]", value, token);
                this.eventCounter++;
                return Task.CompletedTask;
            }

            Task OnError(Exception ex)
            {
                this.logger.LogError("Error: {Exception}", ex);
                this.errorCounter++;
                return Task.CompletedTask;
            }

            Task OnCompleted() => Task.CompletedTask;
        }

        public Task<int> GetErrorCounter() => Task.FromResult(this.errorCounter);

        public Task<int> GetEventCounter() => Task.FromResult(this.eventCounter);

        public Task Deactivate()
        {
            this.DeactivateOnIdle();
            return Task.CompletedTask;
        }
    }
}