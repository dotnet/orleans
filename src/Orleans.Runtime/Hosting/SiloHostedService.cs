using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans.Hosting
{
    internal partial class SiloHostedService : IHostedService
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
            LogInformationStartingSilo(logger);
            await this.silo.StartAsync(cancellationToken).ConfigureAwait(false);
            LogInformationSiloStarted(logger);
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            LogInformationStoppingSilo(logger);
            await this.silo.StopAsync(cancellationToken).ConfigureAwait(false);
            LogInformationSiloStopped(logger);
        }

        private static void ValidateSystemConfiguration(IEnumerable<IConfigurationValidator> configurationValidators)
        {
            foreach (var validator in configurationValidators)
            {
                validator.ValidateConfiguration();
            }
        }

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Starting Orleans Silo."
        )]
        private static partial void LogInformationStartingSilo(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Orleans Silo started."
        )]
        private static partial void LogInformationSiloStarted(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Stopping Orleans Silo"
        )]
        private static partial void LogInformationStoppingSilo(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Orleans Silo stopped."
        )]
        private static partial void LogInformationSiloStopped(ILogger logger);
    }
}