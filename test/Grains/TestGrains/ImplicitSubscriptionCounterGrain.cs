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
            logger = loggerFactory.CreateLogger($"{nameof(ImplicitSubscriptionCounterGrain)} {IdentityString}");
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("OnActivateAsync");
            return base.OnActivateAsync(cancellationToken);
        }

        public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            logger.LogInformation($"OnDeactivateAsync: {reason}");
            return base.OnDeactivateAsync(reason, cancellationToken);
        }

        public Task<int> GetErrorCounter() => Task.FromResult(State.ErrorCounter);

        public Task<int> GetEventCounter() => Task.FromResult(State.EventCounter);

        public Task Deactivate()
        {
            DeactivateOnIdle();
            return Task.CompletedTask;
        }

        public async Task OnSubscribed(IStreamSubscriptionHandleFactory handleFactory)
        {
            logger.LogInformation($"OnSubscribed: {handleFactory.ProviderName}/{handleFactory.StreamId}");

            await handleFactory.Create<byte[]>().ResumeAsync(OnNext, OnError, OnCompleted, State.Token);

            async Task OnNext(byte[] value, StreamSequenceToken token)
            {
                logger.LogInformation("Received: [{Value} {Token}]", value, token);
                State.EventCounter++;
                State.Token = token;
                await WriteStateAsync();
                if (deactivateOnEvent)
                {
                    DeactivateOnIdle();
                }
            }

            async Task OnError(Exception ex)
            {
                logger.LogError("Error: {Exception}", ex);
                State.ErrorCounter++;
                await WriteStateAsync();
            }

            Task OnCompleted() => Task.CompletedTask;
        }

        public Task DeactivateOnEvent(bool deactivate)
        {
            deactivateOnEvent = deactivate;
            return Task.CompletedTask;
        }
    }
}