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
            // start polling
            _poll = await RegisterReactivePollAsync(
                () => GrainFactory.GetGrain<IAggregatorGrain>(GrainKey).GetAsync(),
                () => GrainFactory.GetGrain<IAggregatorGrain>(GrainKey).LongPollAsync(_cache.Version),
                result => result.IsValid,
                success =>
                {
                    _cache = success;
                    _logger.LogInformation(
                        "{@Time}: {@GrainType} {@GrainKey} updated value to {@Value} with version {@Version}",
                        DateTime.Now.TimeOfDay, GrainType, GrainKey, _cache.Value, _cache.Version);

                    return Task.CompletedTask;
                },
                failed =>
                {
                    _logger.LogWarning("The reactive poll timed out by returning a 'none' response before Orleans could break the promise.");
                    return Task.CompletedTask;
                });

            await base.OnActivateAsync();
        }

        public Task<int> GetAsync() => Task.FromResult(_cache.Value);
    }
}
