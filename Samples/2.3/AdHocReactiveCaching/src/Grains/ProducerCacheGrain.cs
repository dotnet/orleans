using System;
using System.Threading;
using System.Threading.Tasks;
using Grains.Models;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Concurrency;

namespace Grains
{
    [Reentrant]
    [StatelessWorker]
    public class ProducerCacheGrain : Grain, IProducerCacheGrain
    {
        private readonly ILogger<ProducerCacheGrain> _logger;
        private readonly CancellationTokenSource _token = new CancellationTokenSource();
        private VersionedValue<int> _cache;
        private Task _pollTask;

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

            // start polling
            _pollTask = PollAsync();

            await base.OnActivateAsync();
        }

        public override Task OnDeactivateAsync()
        {
            _token.Cancel();

            return base.OnDeactivateAsync();
        }

        private async Task PollAsync()
        {
            while (!_token.IsCancellationRequested)
            {
                try
                {
                    _cache = await GrainFactory.GetGrain<IProducerGrain>(GrainKey).LongPollAsync(_cache.Version);
                }
                catch (TimeoutException error)
                {
                    _logger.LogDebug(error, "{@GrainType} {@GrainKey} long polling broken. Polling again...");
                }
            }
        }

        public Task<int> GetAsync() => Task.FromResult(_cache.Value);
    }
}
