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
        private const uint MaxSupportedTimeoutMilliseconds = 0xfffffffe;
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
                IMembershipManager? membershipManager = null;
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
            if (clusterMembershipOptions.MaxDefunctSiloEntries < 0)
            {
                throw new OrleansConfigurationException($"{nameof(ClusterMembershipOptions)}.{nameof(ClusterMembershipOptions.MaxDefunctSiloEntries)} ({clusterMembershipOptions.MaxDefunctSiloEntries}) must be greater than or equal to 0, or null.");
            }

            if (clusterMembershipOptions.ProbeTimeout <= TimeSpan.Zero)
            {
                throw new OrleansConfigurationException($"{nameof(ClusterMembershipOptions)}.{nameof(ClusterMembershipOptions.ProbeTimeout)} ({clusterMembershipOptions.ProbeTimeout}) must be greater than 0.");
            }

            if (clusterMembershipOptions.ProbeTimeout.TotalMilliseconds > MaxSupportedTimeoutMilliseconds)
            {
                throw new OrleansConfigurationException($"{nameof(ClusterMembershipOptions)}.{nameof(ClusterMembershipOptions.ProbeTimeout)} ({clusterMembershipOptions.ProbeTimeout}) must be less than or equal to {TimeSpan.FromMilliseconds(MaxSupportedTimeoutMilliseconds)}.");
            }

            if (clusterMembershipOptions.MinProbeTimeout <= TimeSpan.Zero)
            {
                throw new OrleansConfigurationException($"{nameof(ClusterMembershipOptions)}.{nameof(ClusterMembershipOptions.MinProbeTimeout)} ({clusterMembershipOptions.MinProbeTimeout}) must be greater than 0.");
            }

            if (clusterMembershipOptions.MaxProbeTimeout < clusterMembershipOptions.MinProbeTimeout)
            {
                throw new OrleansConfigurationException($"{nameof(ClusterMembershipOptions)}.{nameof(ClusterMembershipOptions.MaxProbeTimeout)} ({clusterMembershipOptions.MaxProbeTimeout}) must be greater than or equal to {nameof(ClusterMembershipOptions)}.{nameof(ClusterMembershipOptions.MinProbeTimeout)} ({clusterMembershipOptions.MinProbeTimeout}).");
            }

            if (clusterMembershipOptions.MaxProbeTimeout.TotalMilliseconds > MaxSupportedTimeoutMilliseconds)
            {
                throw new OrleansConfigurationException($"{nameof(ClusterMembershipOptions)}.{nameof(ClusterMembershipOptions.MaxProbeTimeout)} ({clusterMembershipOptions.MaxProbeTimeout}) must be less than or equal to {TimeSpan.FromMilliseconds(MaxSupportedTimeoutMilliseconds)}.");
            }

            if (clusterMembershipOptions.ProbeTimeout < clusterMembershipOptions.MinProbeTimeout
                || clusterMembershipOptions.ProbeTimeout > clusterMembershipOptions.MaxProbeTimeout)
            {
                throw new OrleansConfigurationException($"{nameof(ClusterMembershipOptions)}.{nameof(ClusterMembershipOptions.ProbeTimeout)} ({clusterMembershipOptions.ProbeTimeout}) must be between {nameof(ClusterMembershipOptions)}.{nameof(ClusterMembershipOptions.MinProbeTimeout)} ({clusterMembershipOptions.MinProbeTimeout}) and {nameof(ClusterMembershipOptions)}.{nameof(ClusterMembershipOptions.MaxProbeTimeout)} ({clusterMembershipOptions.MaxProbeTimeout}).");
            }

            var failureDetectionTimeoutTicks = clusterMembershipOptions.NumMissedProbesLimit > 0
                ? clusterMembershipOptions.GetFailureDetectionTimeout().Ticks
                : 0L;

            if (clusterMembershipOptions.TableRefreshTimeout <= TimeSpan.Zero)
            {
                throw new OrleansConfigurationException($"{nameof(ClusterMembershipOptions)}.{nameof(ClusterMembershipOptions.TableRefreshTimeout)} ({clusterMembershipOptions.TableRefreshTimeout}) must be greater than 0.");
            }

            var tableRefreshTimeoutTicks = clusterMembershipOptions.TableRefreshTimeout.Ticks;
            if (tableRefreshTimeoutTicks > (TimeSpan.MaxValue.Ticks - failureDetectionTimeoutTicks) / 2)
            {
                throw new OrleansConfigurationException($"The failure detection timeout plus twice {nameof(ClusterMembershipOptions)}.{nameof(ClusterMembershipOptions.TableRefreshTimeout)} ({clusterMembershipOptions.TableRefreshTimeout}) must not exceed {TimeSpan.MaxValue}.");
            }

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
