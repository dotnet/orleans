using System;
using System.Threading;
using System.Threading.Tasks;
using FasterSample.Grains;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;

namespace FasterSample.WebApp
{
    public class OrleansClientService : IHostedService
    {
        private readonly OrleansClusterClientServiceOptions _options;
        private readonly ILogger _logger;
        private readonly IClusterClient _client;

        public OrleansClientService(IOptions<OrleansClusterClientServiceOptions> options, ILogger<OrleansClientService> logger)
        {
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _client = new ClientBuilder()
                .UseLocalhostClustering()
                .ConfigureApplicationParts(manager => manager.AddApplicationPart(typeof(IDictionaryFrequencyGrain).Assembly).WithReferences())
                .Build();
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var attempts = _options.ConnectionAttempts;
            await _client
                .Connect(async ex =>
                {
                    if (--attempts > 0)
                    {
                        _logger.LogWarning(ex, ex.Message);
                        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                        return true;
                    }

                    _logger.LogError(ex, ex.Message);
                    return false;
                })
                .ConfigureAwait(false);
        }

        public Task StopAsync(CancellationToken cancellationToken) => _client.Close();

        public IGrainFactory GrainFactory => _client;
    }
}