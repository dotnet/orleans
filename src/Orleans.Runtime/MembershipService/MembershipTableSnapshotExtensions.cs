using System.Collections.Immutable;
using System.Linq;
using SiloMetadataModel = Orleans.Runtime.MembershipService.SiloMetadata.SiloMetadata;

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
                var wasDeclaredDead = entry.Status == SiloStatus.Dead
                    && entry.SuspectTimes is { Count: > 0 } suspectTimes
                    && suspectTimes.All(suspect => !suspect.Item1.Equals(entry.SiloAddress));
                memberBuilder[entry.SiloAddress] = entry.Metadata is { } metadata
                    ? new ClusterMember(entry.SiloAddress, entry.Status, entry.SiloName, new SiloMetadataModel(metadata), wasDeclaredDead)
                    : new ClusterMember(entry.SiloAddress, entry.Status, entry.SiloName, wasDeclaredDead);
            }

            return new ClusterMembershipSnapshot(memberBuilder.ToImmutable(), membership.Version);
        }
    }
}
