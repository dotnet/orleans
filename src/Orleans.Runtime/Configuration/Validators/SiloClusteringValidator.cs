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
            // Check for either IMembershipTable (traditional) or IMembershipManager (native integration).
            // External membership providers like RapidCluster may register IMembershipManager directly
            // without providing an IMembershipTable.
            var clusteringTableProvider = this.serviceProvider.GetService<IMembershipTable>();
            var membershipManager = this.serviceProvider.GetService<IMembershipManager>();

            // If MembershipTableManager is registered, it requires IMembershipTable.
            // But if a custom IMembershipManager is registered (not MembershipTableManager),
            // then IMembershipTable is not required.
            var hasMembershipTableManager = membershipManager is MembershipTableManager;
            var hasCustomMembershipManager = membershipManager != null && !hasMembershipTableManager;

            if (clusteringTableProvider == null && !hasCustomMembershipManager)
            {
                throw new OrleansConfigurationException(ClientClusteringValidator.ClusteringNotConfigured);
            }

            var clusterMembershipOptions = this.serviceProvider.GetRequiredService<IOptions<ClusterMembershipOptions>>().Value;
            if (clusterMembershipOptions.LivenessEnabled)
            {
                if (clusterMembershipOptions.NumVotesForDeathDeclaration > clusterMembershipOptions.NumProbedSilos)
                {
                    throw new OrleansConfigurationException($"{nameof(ClusterMembershipOptions)}.{nameof(ClusterMembershipOptions.NumVotesForDeathDeclaration)} ({clusterMembershipOptions.NumVotesForDeathDeclaration}) must be less than or equal to {nameof(ClusterMembershipOptions)}.{nameof(ClusterMembershipOptions.NumProbedSilos)} ({clusterMembershipOptions.NumProbedSilos}).");
                }

                if (clusterMembershipOptions.NumVotesForDeathDeclaration <= 0)
                {
                    throw new OrleansConfigurationException($"{nameof(ClusterMembershipOptions)}.{nameof(ClusterMembershipOptions.NumVotesForDeathDeclaration)} ({clusterMembershipOptions.NumVotesForDeathDeclaration}) must be greater than 0.");
                }
            }
        }
    }
}
