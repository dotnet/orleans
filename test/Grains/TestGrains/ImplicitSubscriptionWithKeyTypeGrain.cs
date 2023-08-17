using Microsoft.Extensions.Logging;
using Orleans.Streams;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    [ImplicitStreamSubscription(nameof(IImplicitSubscriptionLongKeyGrain))]
    public class ImplicitSubscriptionWithLongKeyGrain : Grain, IImplicitSubscriptionLongKeyGrain
    {
        private readonly ILogger logger;
        private int value;

        public ImplicitSubscriptionWithLongKeyGrain(ILoggerFactory loggerFactory)
        {
            logger = loggerFactory.CreateLogger($"{nameof(ImplicitSubscriptionWithLongKeyGrain)} {IdentityString}");
        }

        public override async Task OnActivateAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("OnActivateAsync");

            value = 0;
            IStreamProvider streamProvider = this.GetStreamProvider(ImplicitStreamTestConstants.StreamProviderName);
            IAsyncStream<int> stream = streamProvider.GetStream<int>(nameof(IImplicitSubscriptionLongKeyGrain), this.GetPrimaryKeyLong());

            await stream.SubscribeAsync(
                (data, token) =>
                {
                    logger.LogInformation("Received event {Event}", data);
                    value = data;
                    return Task.CompletedTask;
                });
        }

        public Task<int> GetValue() => Task.FromResult(value);
    }
}