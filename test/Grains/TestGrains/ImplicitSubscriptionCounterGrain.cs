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
        private StreamSubscriptionHandle<byte[]>? streamHandle;

        [GenerateSerializer]
        public class MyState
        {
            [Id(0)]
            public int EventCounter { get; set; }
            [Id(1)]
            public int ErrorCounter { get; set; }
            [Id(2)]
            public StreamSequenceToken? Token { get; set; }
            [Id(3)]
            public StreamSequenceToken? FirstToken { get; set; }
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

        public Task<int> GetEventCounter(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(this.State.EventCounter);
        }

        public Task Deactivate()
        {
            this.DeactivateOnIdle();
            return Task.CompletedTask;
        }

        public async Task OnSubscribed(IStreamSubscriptionHandleFactory handleFactory)
        {
            this.logger.LogInformation($"OnSubscribed: {handleFactory.ProviderName}/{handleFactory.StreamId}");

            this.streamHandle = await handleFactory.Create<byte[]>().ResumeAsync(OnNext, OnError, OnCompleted, this.State.Token);

            async Task OnError(Exception ex)
            {
                this.logger.LogError("Error: {Exception}", ex);
                this.State.ErrorCounter++;
                await this.WriteStateAsync();
            }

            Task OnCompleted() => Task.CompletedTask;
        }

        private async Task OnNext(byte[] value, StreamSequenceToken? token)
        {
            this.logger.LogInformation("Received: [{Value} {Token}]", value, token);
            this.State.EventCounter++;
            this.State.FirstToken ??= token;
            this.State.Token = token;
            await this.WriteStateAsync();
            if (this.deactivateOnEvent)
            {
                this.DeactivateOnIdle();
            }
        }

        public Task DeactivateOnEvent(bool deactivate)
        {
            this.deactivateOnEvent = deactivate;
            return Task.CompletedTask;
        }

        public async Task RewindToFirstToken()
        {
            if (this.streamHandle is null || this.State.FirstToken is null)
            {
                throw new InvalidOperationException("The stream must deliver an event before it can rewind.");
            }

            this.streamHandle = await this.streamHandle.ResumeAsync(OnNext, this.State.FirstToken);
        }
    }

    [ImplicitStreamSubscription("FastSlowImplicitSubscriptionCounterGrain")]
    public class FastImplicitSubscriptionCounterGrain : ImplicitSubscriptionCounterGrain, IFastImplicitSubscriptionCounterGrain
    {
        public FastImplicitSubscriptionCounterGrain(ILoggerFactory loggerFactory) : base(loggerFactory)
        {
        }
    }

    [ImplicitStreamSubscription("FastSlowImplicitSubscriptionCounterGrain")]
    public class SlowImplicitSubscriptionCounterGrain : ImplicitSubscriptionCounterGrain, ISlowImplicitSubscriptionCounterGrain
    {
        public SlowImplicitSubscriptionCounterGrain(ILoggerFactory loggerFactory) : base(loggerFactory)
        {
        }

        public override async Task OnActivateAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(10_000);
            await base.OnActivateAsync(cancellationToken);
        }
    }
}