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
        private readonly CancellationTokenSource _workloadCancellation = new CancellationTokenSource();

        public CacheTesterHostedService(ILogger<CacheTesterHostedService> logger, IOptions<CacheTesterOptions> options, IClusterClient client)
        {
            _logger = logger;
            _options = options.Value;
            _client = client;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            // output the current value of the configured publisher cache grain every second
            if (_options.PublisherCacheGrainKey != null)
            {
                Task.Run(async () =>
                {
                    while (!_workloadCancellation.IsCancellationRequested)
                    {
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(1), _workloadCancellation.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }

                        var value = await _client.GetGrain<IProducerCacheGrain>(_options.PublisherCacheGrainKey).GetAsync();
                        _logger.LogInformation(
                            "{@Time}: {@GrainType} {@GrainKey} returned value {@Value}",
                            DateTime.Now.TimeOfDay, nameof(IProducerCacheGrain), _options.PublisherCacheGrainKey, value);
                    }
                }).Ignore();
            }

            // output the current value of the configured aggregator grain every second
            if (_options.AggregatorCacheGrainKey != null)
            {
                Task.Run(async () =>
                {
                    while (!_workloadCancellation.IsCancellationRequested)
                    {
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(1), _workloadCancellation.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }

                        var value = await _client.GetGrain<IAggregatorCacheGrain>(_options.AggregatorCacheGrainKey).GetAsync();
                        _logger.LogInformation(
                            "{@Time}: {@GrainType} {@GrainKey} returned value {@Value}",
                            DateTime.Now.TimeOfDay, nameof(IAggregatorCacheGrain), _options.AggregatorCacheGrainKey, value);
                    }
                }).Ignore();
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }
    }
}
