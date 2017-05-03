using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{

    [ImplicitStreamSubscription("red")]
    [ImplicitStreamSubscription("blue")]
    public class MultipleImplicitSubscriptionGrain : Grain, IMultipleImplicitSubscriptionGrain
    {
        private Logger logger;
        private IAsyncStream<int> redStream, blueStream;
        private int redCounter, blueCounter;

        public override async Task OnActivateAsync()
        {
            logger = base.GetLogger("MultipleImplicitSubscriptionGrain " + base.IdentityString);
            logger.Info("OnActivateAsync");

            var streamProvider = GetStreamProvider("SMSProvider");
            redStream = streamProvider.GetStream<int>(this.GetPrimaryKey(), "red");
            blueStream = streamProvider.GetStream<int>(this.GetPrimaryKey(), "blue");

            await redStream.SubscribeAsync(
                (e, t) =>
                {
                    logger.Info("Received a red event {0}", e);
                    redCounter++;
                    return Task.CompletedTask;
                });

            await blueStream.SubscribeAsync(
                (e, t) =>
                {
                    logger.Info("Received a blue event {0}", e);
                    blueCounter++;
                    return Task.CompletedTask;
                });
        }

        public Task<Tuple<int, int>> GetCounters()
        {
            return Task.FromResult(new Tuple<int, int>(redCounter, blueCounter));
        }
    }
}
