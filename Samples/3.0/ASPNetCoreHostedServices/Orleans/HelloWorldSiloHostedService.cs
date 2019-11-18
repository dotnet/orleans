using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;

namespace ASPNetCoreHostedServices.Internal
{
    public class HelloWorldHostedService : IHostedService

    {
        public ISiloHost Silo { get; }
        private readonly ILogger<HelloWorldHostedService> _logger;
        public HelloWorldHostedService(ILogger<HelloWorldHostedService> logger) {
            this._logger = logger;

            var silo = new SiloHostBuilder()
                .UseLocalhostClustering();
        }
        public async Task StartAsync(CancellationToken cancellationToken) => throw new System.NotImplementedException();

        public async Task StopAsync(CancellationToken cancellationToken) => throw new System.NotImplementedException();
    }
}