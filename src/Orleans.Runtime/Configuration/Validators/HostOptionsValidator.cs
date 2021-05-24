using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
using System;

namespace Orleans.Configuration
{
    /// <summary>
    /// Validator for <see cref="HostOptions"/>
    /// </summary>
    public class HostOptionsValidator : IConfigurationValidator
    {
        private HostOptions hostOptions;
        private GrainCollectionOptions grainOptions;

        public HostOptionsValidator(IOptions<HostOptions> hostOptions, IOptions<GrainCollectionOptions> grainOptions)
        {
            this.hostOptions = hostOptions.Value;
            this.grainOptions = grainOptions.Value;

        }

        public void ValidateConfiguration()
        {
            if (this.hostOptions.ShutdownTimeout > this.grainOptions.DeactivationTimeout) {
                throw new OrleansConfigurationException(
                    $"Configuration for HostOptions.ShutdownTimeout can't greater than GrainCollectionOptions.DeactivationTimeout. " +
                    $"Please configure GrainCollectionOptions.DeactivationTimeout as well. " +
                    $"See {Constants.TroubleshootingHelpLink} for more information.");
            }
        }
    }
}
