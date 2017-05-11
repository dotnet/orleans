using System.Threading.Tasks;
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

        public override async Task OnActivateAsync()
        {
            var logger = GetLogger($"{nameof(FilteredImplicitSubscriptionWithExtensionGrain)} {IdentityString}");
            logger.Info("OnActivateAsync");
            var streamProvider = GetStreamProvider("SMSProvider");

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