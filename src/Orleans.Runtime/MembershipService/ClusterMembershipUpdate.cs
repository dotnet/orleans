using System;
using System.Collections.Immutable;

namespace Orleans.Runtime
{
    /// <summary>
    /// Represents a cluster membership snapshot and changes from a previous snapshot.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public sealed class ClusterMembershipUpdate
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ClusterMembershipUpdate"/> class.
        /// </summary>
        /// <param name="snapshot">The snapshot.</param>
        /// <param name="changes">The changes.</param>
        public ClusterMembershipUpdate(ClusterMembershipSnapshot snapshot, ImmutableArray<ClusterMember> changes)
        {
            this.Snapshot = snapshot;
            this.Changes = changes;
        }

        /// <summary>
        /// Gets a value indicating whether this instance has changes.
        /// </summary>
        /// <value><see langword="true"/> if this instance has changes; otherwise, <see langword="false"/>.</value>
        public bool HasChanges => !this.Changes.IsDefaultOrEmpty;

        /// <summary>
        /// Gets the changes.
        /// </summary>
        /// <value>The changes.</value>
        [Id(1)]
        public ImmutableArray<ClusterMember> Changes { get; }

        /// <summary>
        /// Gets the snapshot.
        /// </summary>
        /// <value>The snapshot.</value>
        [Id(2)]
        public ClusterMembershipSnapshot Snapshot { get; }
    }
}
