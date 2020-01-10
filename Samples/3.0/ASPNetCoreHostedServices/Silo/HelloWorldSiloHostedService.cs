using System.Net;
using System.Threading;
using System.Threading.Tasks;
using AspNetCoreHostedServices.Grains;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;

namespace AspNetCoreHostedServices.Silo
{
    public class HelloWorldSiloHostedService : IHostedService
    {
        public ISiloHost Silo { get; }
        private readonly ILogger<HelloWorldSiloHostedService> _logger;

        public HelloWorldSiloHostedService(ILogger<HelloWorldSiloHostedService> logger, ILoggerProvider loggerProvider)
        {
            this._logger = logger;

            var silo = new SiloHostBuilder()
                .UseLocalhostClustering()
                .Configure<ClusterOptions>(opts =>
                {
                    opts.ClusterId = "dev";
                    opts.ServiceId = "HellowWorldAPIService";
                })
                .Configure<EndpointOptions>(opts =>
                {
                    opts.AdvertisedIPAddress = IPAddress.Loopback;
                })
                .ConfigureApplicationParts(manager =>
                {
                    manager.AddApplicationPart(typeof(HelloWorldGrain).Assembly).WithReferences();
                })
                .ConfigureLogging(l => l.AddProvider(loggerProvider));

            this.Silo = silo.Build();
        }

        public Task StartAsync(CancellationToken cancellationToken) => this.Silo.StartAsync(cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken) => this.Silo.StopAsync(cancellationToken);
    }
}