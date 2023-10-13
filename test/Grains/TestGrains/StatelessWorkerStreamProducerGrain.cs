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

        private readonly ILogger logger;

        public StatelessWorkerStreamProducerGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public async Task Produce(Guid streamId, string providerToUse, string message)
        {
            var stream = this.GetStreamProvider(providerToUse).GetStream<string>(StreamNamespace, streamId);
            await stream.OnNextAsync(message);
        }
    }
}
