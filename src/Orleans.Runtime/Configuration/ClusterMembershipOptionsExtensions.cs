using System;
using Orleans.Internal;

namespace Orleans.Configuration;

internal static class ClusterMembershipOptionsExtensions
{
    internal static TimeSpan GetFailureDetectionTimeout(this ClusterMembershipOptions options) =>
        options.MaxProbeTimeout.Multiply(options.NumMissedProbesLimit);
}
