using Microsoft.Extensions.Logging;
using Orleans.Streams;
using Orleans.Streams.Core;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    [ImplicitStreamSubscription(nameof(IImplicitSubscriptionCounterGrain))]
    public class ImplicitSubscriptionCounterGrain : Grain<ImplicitSubscriptionCounterGrain.MyState>, IImplicitSubscriptionCounterGrain, IStreamSubscriptionObserver
    {
        private readonly ILogger logger;
        private bool deactivateOnEvent;

        [GenerateSerializer]
        public class MyState
        {
            [Id(0)]
            public int EventCounter { get; set; }
            [Id(1)]
            public int ErrorCounter { get; set; }
            [Id(2)]
            public StreamSequenceToken Token { get; set; }
        }

        public ImplicitSubscriptionCounterGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{nameof(ImplicitSubscriptionCounterGrain)} {this.IdentityString}");
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            this.logger.LogInformation("OnActivateAsync");
            return base.OnActivateAsync(cancellationToken);
        }

        public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            this.logger.LogInformation($"OnDeactivateAsync: {reason}");
            return base.OnDeactivateAsync(reason, cancellationToken);
        }

        public Task<int> GetErrorCounter() => Task.FromResult(this.State.ErrorCounter);

        public Task<int> GetEventCounter() => Task.FromResult(this.State.EventCounter);

        public Task Deactivate()
        {
            this.DeactivateOnIdle();
            return Task.CompletedTask;
        }

        public async Task OnSubscribed(IStreamSubscriptionHandleFactory handleFactory)
        {
            this.logger.LogInformation($"OnSubscribed: {handleFactory.ProviderName}/{handleFactory.StreamId}");

            await handleFactory.Create<byte[]>().ResumeAsync(OnNext, OnError, OnCompleted, this.State.Token);

            async Task OnNext(byte[] value, StreamSequenceToken token)
            {
                this.logger.LogInformation("Received: [{Value} {Token}]", value, token);
                this.State.EventCounter++;
                this.State.Token = token;
                await this.WriteStateAsync();
                if (this.deactivateOnEvent)
                {
                    this.DeactivateOnIdle();
                }
            }

            async Task OnError(Exception ex)
            {
                this.logger.LogError("Error: {Exception}", ex);
                this.State.ErrorCounter++;
                await this.WriteStateAsync();
            }

            Task OnCompleted() => Task.CompletedTask;
        }

        public Task DeactivateOnEvent(bool deactivate)
        {
            this.deactivateOnEvent = deactivate;
            return Task.CompletedTask;
        }
    }
}