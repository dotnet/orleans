using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Orleans.Hosting
{
    internal class SiloHostedService : IHostedService
    {
        private readonly IServiceProvider serviceProvider;
        private readonly ILogger logger;
        private readonly ISiloHost siloHost;

        public SiloHostedService(IServiceProvider serviceProvider)
        {
            this.serviceProvider = serviceProvider;
            this.ValidateSystemConfiguration();

            this.siloHost = serviceProvider.GetRequiredService<ISiloHost>();
            this.logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<SiloHostedService>();
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            this.logger.LogInformation("Starting Orleans Silo.");
            await this.siloHost.StartAsync(cancellationToken).ConfigureAwait(false);
            this.logger.LogInformation("Orleans Silo started.");
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            this.logger.LogInformation("Stopping Orleans Silo");
            await this.siloHost.StopAsync(cancellationToken).ConfigureAwait(false);
            this.logger.LogInformation("Orleans Silo stopped.");
        }

        private void ValidateSystemConfiguration()
        {
            var validators = this.serviceProvider.GetServices<IConfigurationValidator>();
            foreach (var validator in validators)
            {
                validator.ValidateConfiguration();
            }
        }
    }
}