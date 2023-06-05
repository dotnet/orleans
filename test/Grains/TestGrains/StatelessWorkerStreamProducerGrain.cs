using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.Streams;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    [StatelessWorker(MaxLocalWorkers)]
    public class StatelessWorkerStreamProducerGrain : Grain, IStatelessWorkerStreamProducerGrain
    {
        internal const int MaxLocalWorkers = 1;
        internal const string StreamNamespace = "StatelessWorkerStreamingNamespace";

        private ILogger logger;

        public StatelessWorkerStreamProducerGrain(ILoggerFactory loggerFactory)
        {
            logger = loggerFactory.CreateLogger($"{GetType().Name}-{IdentityString}");
        }

        public async Task Produce(Guid streamId, string providerToUse, string message)
        {
            var stream = this.GetStreamProvider(providerToUse).GetStream<string>(StreamNamespace, streamId);
            await stream.OnNextAsync(message);
        }
    }
}
