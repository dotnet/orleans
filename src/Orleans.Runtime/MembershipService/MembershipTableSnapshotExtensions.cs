using System.Collections.Immutable;
using System.Linq;

namespace Orleans.Runtime.MembershipService
{
    internal static class MembershipTableSnapshotExtensions
    {
        internal static ClusterMembershipSnapshot CreateClusterMembershipSnapshot(this MembershipTableSnapshot membership)
        {
            var memberBuilder = ImmutableDictionary.CreateBuilder<SiloAddress, ClusterMember>();
            foreach (var member in membership.Entries)
            {
                var entry = member.Value;
                memberBuilder[entry.SiloAddress] = new ClusterMember(
                    entry.SiloAddress,
                    entry.Status,
                    entry.SiloName,
                    entry.Status == SiloStatus.Dead
                        && entry.SuspectTimes is { } suspectTimes
                        && suspectTimes.Any(suspect => !suspect.Item1.Equals(entry.SiloAddress)));
            }

            return new ClusterMembershipSnapshot(memberBuilder.ToImmutable(), membership.Version);
        }
    }
}
