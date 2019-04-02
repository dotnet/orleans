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
            // start long polling
            _poll = await RegisterReactivePollAsync(
                () => GrainFactory.GetGrain<IProducerGrain>(GrainKey).GetAsync(),
                () => GrainFactory.GetGrain<IProducerGrain>(GrainKey).LongPollAsync(_cache.Version),
                result => result.IsValid,
                apply =>
                {
                    _cache = apply;
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
