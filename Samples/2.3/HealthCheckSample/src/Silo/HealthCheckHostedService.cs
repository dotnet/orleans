using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;

namespace Silo
{
    public class HealthCheckHostedService : IHostedService
    {
        private readonly IWebHost host;

        public HealthCheckHostedService(IServiceProvider provider)
        {
            host = new WebHostBuilder()
                .UseKestrel(options => options.ListenAnyIP(8880))
                .ConfigureServices(services =>
                {
                    services.AddHealthChecks()
                        .AddCheck<GrainHealthCheck>("GrainHealth")
                        .AddCheck<SiloHealthCheck>("SiloHealth");

                    services.AddSingleton(_ => provider.GetRequiredService<IClusterClient>());
                    services.AddTransient(_ => provider.GetRequiredService<IEnumerable<IHealthCheckParticipant>>());
                })
                .ConfigureLogging(builder =>
                {
                    builder.AddConsole();
                })
                .Configure(app =>
                {
                    app.UseHealthChecks("/health");
                })
                .Build();
        }

        public Task StartAsync(CancellationToken cancellationToken) => host.StartAsync(cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken) => host.StopAsync(cancellationToken);
    }
}
