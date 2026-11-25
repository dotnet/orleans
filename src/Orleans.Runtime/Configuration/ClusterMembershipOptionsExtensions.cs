using System;
using Orleans.Runtime;

namespace Orleans.Configuration;

internal static class ClusterMembershipOptionsExtensions
{
    internal static TimeSpan GetFailureDetectionTimeout(this ClusterMembershipOptions options)
    {
        try
        {
            return TimeSpan.FromTicks(checked(options.MaxProbeTimeout.Ticks * options.NumMissedProbesLimit));
        }
        catch (OverflowException exception)
        {
            throw new OrleansConfigurationException(
                $"The maximum probe cycle time ({options.MaxProbeTimeout}) multiplied by {nameof(ClusterMembershipOptions)}.{nameof(ClusterMembershipOptions.NumMissedProbesLimit)} ({options.NumMissedProbesLimit}) must not exceed {TimeSpan.MaxValue}.",
                exception);
        }
    }
}
