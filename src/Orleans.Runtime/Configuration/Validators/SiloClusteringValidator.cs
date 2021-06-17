using System;

using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration.Validators;

namespace Orleans.Runtime.Configuration
{
    /// <summary>
    /// Validates basic cluster membership configuration.
    /// </summary>
    internal class SiloClusteringValidator : IConfigurationValidator
    {
        private readonly IServiceProvider serviceProvider;

        public SiloClusteringValidator(IServiceProvider serviceProvider)
        {
            this.serviceProvider = serviceProvider;
        }

        /// <inheritdoc />
        public void ValidateConfiguration()
        {
            var clusteringTableProvider = this.serviceProvider.GetService<IMembershipTable>();
            if (clusteringTableProvider == null)
            {
                throw new OrleansConfigurationException(ClientClusteringValidator.ClusteringNotConfigured);
            }
        }
    }
}