using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans.Hosting
{
    internal class SiloHostedService : IHostedService
    {
        private readonly ILogger logger;
        private readonly Silo silo;

        public SiloHostedService(
            Silo silo,
            IEnumerable<IConfigurationValidator> configurationValidators,
            ILogger<SiloHostedService> logger)
        {
            ValidateSystemConfiguration(configurationValidators);
            this.silo = silo;
            this.logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("Starting Orleans Silo.");
            await silo.StartAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Orleans Silo started.");
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("Stopping Orleans Silo");
            await silo.StopAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Orleans Silo stopped.");
        }

        private void ValidateSystemConfiguration(IEnumerable<IConfigurationValidator> configurationValidators)
        {
            foreach (var validator in configurationValidators)
            {
                validator.ValidateConfiguration();
            }
        }
    }
}