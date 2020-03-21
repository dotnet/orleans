using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Runtime;

namespace Silo
{
    public class HealthCheckHostedService : IHostedService
    {
        private readonly IWebHost host;

        public HealthCheckHostedService(IClusterClient client, IMembershipOracle oracle, IOptions<HealthCheckHostedServiceOptions> myOptions)
        {
            host = new WebHostBuilder()
                .UseKestrel(options => options.ListenAnyIP(myOptions.Value.Port))
                .ConfigureServices(services =>
                {
                    services.AddHealthChecks()
                        .AddCheck<GrainHealthCheck>("GrainHealth")
                        .AddCheck<SiloHealthCheck>("SiloHealth")
                        .AddCheck<StorageHealthCheck>("StorageHealth")
                        .AddCheck<ClusterHealthCheck>("ClusterHealth");

                    services.AddSingleton<IHealthCheckPublisher, LoggingHealthCheckPublisher>()
                        .Configure<HealthCheckPublisherOptions>(options =>
                        {
                            options.Period = TimeSpan.FromSeconds(1);
                        });

                    services.AddSingleton(client);
                    services.AddSingleton(Enumerable.AsEnumerable(new IHealthCheckParticipant[] { oracle }));
                })
                .ConfigureLogging(builder =>
                {
                    builder.AddConsole();
                })
                .Configure(app =>
                {
                    app.UseHealthChecks(myOptions.Value.PathString);
                })
                .Build();
        }

        public Task StartAsync(CancellationToken cancellationToken) => host.StartAsync(cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken) => host.StopAsync(cancellationToken);
    }
}
