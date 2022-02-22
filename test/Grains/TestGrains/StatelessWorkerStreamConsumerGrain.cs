using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Concurrency;
using Orleans.Streams;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    [StatelessWorker(MaxLocalWorkers)]
    public class StatelessWorkerStreamConsumerGrain : Grain, IStatelessWorkerStreamConsumerGrain
    {
        internal const int MaxLocalWorkers = 1;
        internal const string StreamNamespace = "StatelessWorkerStreamingNamespace";

        private ILogger logger;

        public StatelessWorkerStreamConsumerGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public Task OnCompletedAsync() => Task.CompletedTask;

        public Task OnErrorAsync(Exception ex) => Task.CompletedTask;

        public Task OnNextAsync(string item, StreamSequenceToken token = null) => Task.CompletedTask;

        public async Task BecomeConsumer(Guid streamId, string providerToUse)
        {
            var stream = this.GetStreamProvider(providerToUse).GetStream<string>(streamId, StreamNamespace);
            _ = await stream.SubscribeAsync(OnNextAsync, OnErrorAsync, OnCompletedAsync);
        }
    }
}
