using System;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Configuration.Validators;
using Orleans.Runtime.MembershipService;

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
            var clusterOptions = this.serviceProvider.GetRequiredService<IOptions<ClusterOptions>>();
            if (string.IsNullOrWhiteSpace(clusterOptions.Value.ClusterId))
            {
                throw new OrleansConfigurationException(ClientClusteringValidator.ClusterIdNotConfigured);
            }

            var clusteringProvider = this.serviceProvider.GetService<IMembershipOracle>();
            var clusteringTableProvider = this.serviceProvider.GetService<IMembershipTable>();
            var storageBackedWithNoStorage = clusteringProvider is MembershipOracle && clusteringTableProvider == null;
            if (clusteringProvider == null || storageBackedWithNoStorage)
            {
                throw new OrleansConfigurationException(ClientClusteringValidator.ClusteringNotConfigured);
            }
        }
    }
}