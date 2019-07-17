using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Client.Hosting
{

    public class OrleansClientHostedService : IHostedService
    {
        private readonly ILogger<OrleansClientHostedService> _logger;
        private OrleansClientStore clientStore;
        private IOptions<OrleansClientHostedOptions> clientOptions;

        public OrleansClientHostedService(
            ILogger<OrleansClientHostedService> logger,
            IOptions<OrleansClientHostedOptions> clientOptions,
            OrleansClientStore clientStore)
        {
            _logger = logger;
            this.clientStore = clientStore;
            this.clientOptions = clientOptions;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            this.clientStore.Client = new ClientBuilder()
                .UseLocalhostClustering(clientOptions.Value.Gateways)
                .Configure<ClusterOptions>(options =>
                {
                    options.ClusterId = "dev";
                    options.ServiceId = "OrleansBasics";
                })
                .ConfigureLogging(logging => logging.AddConsole())
                .Build();

            var attempt = 0;
            var maxAttempts = 100;
            var delay = TimeSpan.FromSeconds(1);
            return this.clientStore.Client.Connect(async error =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                if (++attempt < maxAttempts)
                {
                    _logger.LogWarning(error,
                        "Failed to connect to Orleans cluster on attempt {@Attempt} of {@MaxAttempts}.",
                        attempt, maxAttempts);

                    try
                    {
                        await Task.Delay(delay, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return false;
                    }

                    return true;
                }
                else
                {
                    _logger.LogError(error,
                        "Failed to connect to Orleans cluster on attempt {@Attempt} of {@MaxAttempts}.",
                        attempt, maxAttempts);

                    return false;
                }
            });
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            try
            {
                await this.clientStore.Client.Close();
            }
            catch (OrleansException error)
            {
                _logger.LogWarning(error, "Error while gracefully disconnecting from Orleans cluster. Will ignore and continue to shutdown.");
            }
        }
    }
}
