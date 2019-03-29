using System;
using System.Threading.Tasks;
using Grains.Models;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Concurrency;

namespace Grains
{
    [Reentrant]
    [StatelessWorker]
    public class ProducerCacheGrain : ReactiveGrain, IProducerCacheGrain
    {
        private readonly ILogger<ProducerCacheGrain> _logger;
        private VersionedValue<int> _cache;
        private IDisposable _poll;

        public ProducerCacheGrain(ILogger<ProducerCacheGrain> logger)
        {
            _logger = logger;
        }

        private string GrainType => nameof(ProducerCacheGrain);
        private string GrainKey => this.GetPrimaryKeyString();

        public override async Task OnActivateAsync()
        {
            // hydrate the cache with whatever value is available right now
            _cache = await GrainFactory.GetGrain<IProducerGrain>(GrainKey).GetAsync();

            // start long polling
            _poll = RegisterReactivePoll(async () =>
            {
                _cache = await GrainFactory.GetGrain<IProducerGrain>(GrainKey).LongPollAsync(_cache.Version);
            });

            await base.OnActivateAsync();
        }

        public Task<int> GetAsync() => Task.FromResult(_cache.Value);
    }
}
