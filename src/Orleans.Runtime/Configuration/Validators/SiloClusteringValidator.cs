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
            var clusteringTableProvider = this.serviceProvider.GetService<IMembershipTable>();

            if (clusteringTableProvider is null)
            {
                // No IMembershipTable configured. A custom IMembershipManager must be present
                // (MembershipTableManager requires IMembershipTable, so it cannot be used).
                IMembershipManager membershipManager = null;
                try
                {
                    membershipManager = this.serviceProvider.GetService<IMembershipManager>();
                }
                catch
                {
                    // Resolution failed — MembershipTableManager requires IMembershipTable.
                }

                if (membershipManager is null or MembershipTableManager)
                {
                    throw new OrleansConfigurationException(ClientClusteringValidator.ClusteringNotConfigured);
                }
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
