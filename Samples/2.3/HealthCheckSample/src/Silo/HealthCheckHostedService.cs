using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans;

namespace Silo
{
    public class HealthCheckHostedService : IHostedService
    {
        private IWebHost host;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            host = new WebHostBuilder()
                .UseKestrel(options => options.ListenAnyIP(8880))
                .ConfigureServices(services =>
                {
                    services.AddHealthChecks();
                })
                .Configure(app =>
                {
                    app.UseHealthChecks("/health");
                })
                .Build();

            return host.StartAsync(cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken) => host.StopAsync(cancellationToken);
    }
}
