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
    public class AggregatorCacheGrain : ReactiveGrain, IAggregatorCacheGrain
    {
        private readonly ILogger<AggregatorCacheGrain> _logger;
        private VersionedValue<int> _cache;
        private IDisposable _poll;

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
            _poll = RegisterReactivePoll(async () =>
            {
                var update = await GrainFactory.GetGrain<IAggregatorGrain>(GrainKey).LongPollAsync(_cache.Version);
                if (update.IsValid)
                {
                    _cache = update;
                }
                else
                {
                    _logger.LogWarning("The reactive poll timed out by returning a 'none' response before Orleans could break the promise.");
                }
                _logger.LogInformation(
                    "{@Time}: {@GrainType} {@GrainKey} updated value to {@Value} with version {@Version}",
                    DateTime.Now.TimeOfDay, GrainType, GrainKey, _cache.Value, _cache.Version);
            });

            await base.OnActivateAsync();
        }

        public Task<int> GetAsync() => Task.FromResult(_cache.Value);
    }
}
