using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;

namespace Presence.Shared
{
    public sealed class ClusterClientHostedService : IHostedService, IAsyncDisposable, IDisposable
    {
        private readonly ILogger<ClusterClientHostedService> _logger;

        public ClusterClientHostedService(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<ClusterClientHostedService>();
            Client = new ClientBuilder()
                .UseLocalhostClustering()
                .ConfigureServices(services =>
                {
                    // Add logging from the host's container.
                    services.AddSingleton(loggerFactory);
                    services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
                })
                .Build();
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            var attempt = 0;
            var maxAttempts = 100;
            var delay = TimeSpan.FromSeconds(1);
            return Client.Connect(async error =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                if (++attempt < maxAttempts)
                {
                    _logger.LogWarning(error, "Failed to connect to Orleans cluster on attempt {Attempt} of {MaxAttempts}.", attempt, maxAttempts);

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
                    _logger.LogError(error, "Failed to connect to Orleans cluster on attempt {Attempt} of {MaxAttempts}.", attempt, maxAttempts); 
                    return false;
                }
            });
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Client.Close();
            }
            catch (OrleansException error)
            {
                _logger.LogWarning(error, "Error while gracefully disconnecting from Orleans cluster. Will ignore and continue to shutdown.");
            }
        }

        public void Dispose() => Client?.Dispose();

        public ValueTask DisposeAsync() => Client?.DisposeAsync() ?? default;

        public IClusterClient Client { get; }
    }
}
