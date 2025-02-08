using System;
using Orleans.Configuration;

namespace Orleans.Runtime.MembershipService;

internal static class MembershipTableEntryExtensions
{
    public static bool HasMissedIAmAlives(this MembershipEntry entry, ClusterMembershipOptions options, DateTimeOffset time)
        => time - entry.EffectiveIAmAliveTime > options.AllowedIAmAliveMissPeriod;
}
