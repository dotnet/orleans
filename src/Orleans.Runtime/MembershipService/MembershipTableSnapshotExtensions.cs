using System.Collections.Immutable;

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
                memberBuilder[entry.SiloAddress] = new ClusterMember(entry.SiloAddress, entry.Status);
            }

            return new ClusterMembershipSnapshot(memberBuilder.ToImmutable(), membership.Version);
        }
    }
}
