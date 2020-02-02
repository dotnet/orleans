using System;
using System.Collections.Immutable;

namespace Orleans.Runtime
{
    [Serializable]
    public sealed class ClusterMembershipUpdate
    {
        public ClusterMembershipUpdate(ClusterMembershipSnapshot snapshot, ImmutableArray<ClusterMember> changes)
        {
            this.Snapshot = snapshot;
            this.Changes = changes;
        }

        public bool HasChanges => !this.Changes.IsDefaultOrEmpty;
        public ImmutableArray<ClusterMember> Changes { get; }
        public ClusterMembershipSnapshot Snapshot { get; }
    }
}
