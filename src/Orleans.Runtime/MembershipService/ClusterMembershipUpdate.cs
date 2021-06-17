using System;
using System.Collections.Immutable;

namespace Orleans.Runtime
{
    [Serializable]
    [GenerateSerializer]
    public sealed class ClusterMembershipUpdate
    {
        public ClusterMembershipUpdate(ClusterMembershipSnapshot snapshot, ImmutableArray<ClusterMember> changes)
        {
            this.Snapshot = snapshot;
            this.Changes = changes;
        }

        public bool HasChanges => !this.Changes.IsDefaultOrEmpty;

        [Id(1)]
        public ImmutableArray<ClusterMember> Changes { get; }
        [Id(2)]
        public ClusterMembershipSnapshot Snapshot { get; }
    }
}
