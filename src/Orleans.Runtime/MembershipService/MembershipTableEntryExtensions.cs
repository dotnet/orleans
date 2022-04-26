using System;
using Orleans.Configuration;

namespace Orleans.Runtime.MembershipService
{
    internal static class MembershipTableEntryExtensions
    {
        public static DateTime? HasMissedIAmAlivesSince(this MembershipEntry entry, ClusterMembershipOptions options, DateTime time)
        {
            var lastIAmAlive = entry.IAmAliveTime;

            if (entry.IAmAliveTime.Equals(default))
            {
                // Since it has not written first IAmAlive yet, use its start time instead.
                lastIAmAlive = entry.StartTime;
            }

            if (time - lastIAmAlive <= options.AllowedIAmAliveMissPeriod) return default;

            return lastIAmAlive;
        }
    }
}
