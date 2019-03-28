using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Stocks.Interfaces;

namespace SiloHost
{
    public class StocksHostedService : IHostedService
    {
        private readonly ILogger<StocksHostedService> _logger;
        private readonly IClusterClient _client;
        private readonly IApplicationLifetime _lifetime;

        public StocksHostedService(ILogger<StocksHostedService> logger, IClusterClient client, IApplicationLifetime lifetime)
        {
            _logger = logger;
            _client = client;
            _lifetime = lifetime;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            // wait until the host is ready to start background work
            _lifetime.ApplicationStarted.Register(() =>
            {
                Task.Run(async () =>
                {
                    try
                    {
                        var stockGrain = _client.GetGrain<IStockGrain>("MSFT");
                        var price = await stockGrain.GetPrice();
                        Console.WriteLine("Price is \n{0}", price);

                        Console.WriteLine("Press Ctrl+C to terminate...");
                    }
                    catch (Exception error)
                    {
                        _logger.LogError(error, error.ToString());
                    }
                });
            });

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
