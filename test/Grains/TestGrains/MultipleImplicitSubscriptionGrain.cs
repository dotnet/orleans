using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
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
        private ILogger logger;
        private IAsyncStream<int> redStream, blueStream;
        private int redCounter, blueCounter;

        public MultipleImplicitSubscriptionGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger("MultipleImplicitSubscriptionGrain " + base.IdentityString);
        }

        public override async Task OnActivateAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("OnActivateAsync");

            var streamProvider = this.GetStreamProvider("SMSProvider");
            redStream = streamProvider.GetStream<int>(this.GetPrimaryKey(), "red");
            blueStream = streamProvider.GetStream<int>(this.GetPrimaryKey(), "blue");

            await redStream.SubscribeAsync(
                (e, t) =>
                {
                    logger.LogInformation("Received a red event {Event}", e);
                    redCounter++;
                    return Task.CompletedTask;
                });

            await blueStream.SubscribeAsync(
                (e, t) =>
                {
                    logger.LogInformation("Received a blue event {Event}", e);
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
