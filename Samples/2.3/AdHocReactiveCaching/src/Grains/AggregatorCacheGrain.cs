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
    public class AggregatorCacheGrain : Grain, IAggregatorCacheGrain
    {
        private readonly ILogger<AggregatorCacheGrain> _logger;
        private VersionedValue<int> _cache;
        private IDisposable _pollTimer;

        public AggregatorCacheGrain(ILogger<AggregatorCacheGrain> logger)
        {
            _logger = logger;
        }

        private string GrainType => nameof(AggregatorCacheGrain);
        private string GrainKey => this.GetPrimaryKeyString();

        public override async Task OnActivateAsync()
        {
            // hydrate the cache with whatever value is available right now
            _cache = await GrainFactory.GetGrain<IAggregatorGrain>(GrainKey).GetAsync();

            // start polling
            _pollTimer = RegisterTimer(_ => PollAsync(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(1));

            await base.OnActivateAsync();
        }

        private async Task PollAsync()
        {
            try
            {
                _cache = await GrainFactory.GetGrain<IAggregatorGrain>(GrainKey).LongPollAsync(_cache.Version);
            }
            catch (TimeoutException error)
            {
                _logger.LogDebug(error, "{@GrainType} {@GrainKey} long polling broken. Polling again...");
            }
        }

        public Task<int> GetAsync() => Task.FromResult(_cache.Value);
    }
}
