using System.Collections.Immutable;
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
                memberBuilder[entry.SiloAddress] = entry.Metadata is { } metadata
                    ? new ClusterMember(entry.SiloAddress, entry.Status, entry.SiloName, new SiloMetadataModel(metadata))
                    : new ClusterMember(entry.SiloAddress, entry.Status, entry.SiloName);
            }

            return new ClusterMembershipSnapshot(memberBuilder.ToImmutable(), membership.Version);
        }
    }
}
