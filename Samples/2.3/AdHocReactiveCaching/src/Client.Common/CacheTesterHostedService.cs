using System;
using System.Threading;
using System.Threading.Tasks;
using Grains;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;

namespace Client.Common
{
    public class CacheTesterHostedService : IHostedService
    {
        private readonly ILogger<CacheTesterHostedService> _logger;
        private readonly IClusterClient _client;
        private readonly CacheTesterOptions _options;
        private Task _workload;

        public CacheTesterHostedService(ILogger<CacheTesterHostedService> logger, IOptions<CacheTesterOptions> options, IClusterClient client)
        {
            _logger = logger;
            _options = options.Value;
            _client = client;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            // output the current value of the configured cached grain every second
            _workload = Task.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1));

                    var value = await _client.GetGrain<IProducerCacheGrain>(_options.CacheGrainKey).GetAsync();
                    _logger.LogInformation(
                        "{@GrainType} {@GrainKey} returned value {@Value}",
                        nameof(IProducerCacheGrain), _options.CacheGrainKey, value);
                }
            });

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }
    }
}
