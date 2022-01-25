using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    [ImplicitStreamSubscription(typeof(RedStreamNamespacePredicate))]
    public class FilteredImplicitSubscriptionWithExtensionGrain : Grain, IFilteredImplicitSubscriptionWithExtensionGrain
    {
        private int counter;
        private readonly ILogger logger;

        public FilteredImplicitSubscriptionWithExtensionGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{nameof(FilteredImplicitSubscriptionWithExtensionGrain)} {IdentityString}");
        }

        public override async Task OnActivateAsync(CancellationToken cancellationToken)
        {
            logger.Info("OnActivateAsync");
            var streamProvider = this.GetStreamProvider("SMSProvider");

            var streamIdentity = this.GetImplicitStreamIdentity();
            var stream = streamProvider.GetStream<int>(streamIdentity.Guid, streamIdentity.Namespace);
            await stream.SubscribeAsync(
                (e, t) =>
                {
                    logger.Info($"Received a {streamIdentity.Namespace} event {e}");
                    ++counter;
                    return Task.CompletedTask;
                });
        }

        public Task<int> GetCounter()
        {
            return Task.FromResult(counter);
        }
    }
}