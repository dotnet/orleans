using System;
using Orleans.Internal;

namespace Orleans.Configuration;

internal static class ClusterMembershipOptionsExtensions
{
    internal static TimeSpan GetMaxProbeCycleTime(this ClusterMembershipOptions options)
    {
        var cycleTime = options.ProbeInterval > options.MaxProbeTimeout
            ? options.ProbeInterval
            : options.MaxProbeTimeout;
        return cycleTime;
    }

    internal static TimeSpan GetFailureDetectionTimeout(this ClusterMembershipOptions options) =>
        options.GetMaxProbeCycleTime().Multiply(options.NumMissedProbesLimit);
}
