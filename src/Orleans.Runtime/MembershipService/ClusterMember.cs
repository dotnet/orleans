using System;
using Orleans.Runtime.MembershipService.SiloMetadata;

#nullable disable
namespace Orleans.Runtime
{
    /// <summary>
    /// Represents a cluster member.
    /// </summary>
    [Serializable, GenerateSerializer, Immutable]
    public sealed class ClusterMember : IEquatable<ClusterMember>
    {                
        /// <summary>
        /// Initializes a new instance of the <see cref="ClusterMember"/> class.
        /// </summary>                
        /// <param name="siloAddress">
        /// The silo address.
        /// </param>
        /// <param name="status">
        /// The silo status.
        /// </param>
        /// <param name="name">
        /// The silo name.
        /// </param>
        public ClusterMember(SiloAddress siloAddress, SiloStatus status, string name)
            : this(siloAddress, status, name, metadata: null, metadataAvailable: false)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ClusterMember"/> class.
        /// </summary>
        /// <param name="siloAddress">
        /// The silo address.
        /// </param>
        /// <param name="status">
        /// The silo status.
        /// </param>
        /// <param name="name">
        /// The silo name.
        /// </param>
        /// <param name="metadata">
        /// The silo metadata.
        /// </param>
        public ClusterMember(SiloAddress siloAddress, SiloStatus status, string name, SiloMetadata metadata)
            : this(siloAddress, status, name, metadata ?? throw new ArgumentNullException(nameof(metadata)), metadataAvailable: true)
        {
        }

        private ClusterMember(SiloAddress siloAddress, SiloStatus status, string name, SiloMetadata metadata, bool metadataAvailable)
        {
            this.SiloAddress = siloAddress ?? throw new ArgumentNullException(nameof(siloAddress));
            this.Status = status;
            this.Name = name;
            this.Metadata = metadataAvailable ? metadata : null;
        }

        /// <summary>
        /// Gets the silo address.
        /// </summary>
        /// <value>The silo address.</value>
        [Id(0)]
        public SiloAddress SiloAddress { get; }

        /// <summary>
        /// Gets the silo status.
        /// </summary>
        /// <value>The silo status.</value>
        [Id(1)]
        public SiloStatus Status { get; }

        /// <summary>
        /// Gets the silo name.
        /// </summary>
        /// <value>The silo name.</value>
        [Id(2)]
        public string Name { get; }

        /// <summary>
        /// Gets the silo metadata, if it is available from the membership table.
        /// </summary>
        /// <remarks>
        /// A <see langword="null"/> value indicates that metadata is unavailable. An empty
        /// <see cref="SiloMetadata"/> value indicates that metadata is available and empty.
        /// </remarks>
        [Id(3)]
        public SiloMetadata Metadata { get; }

        /// <summary>
        /// Gets a value indicating whether metadata is available for this member.
        /// </summary>
        public bool IsMetadataAvailable => this.Metadata is not null;

        /// <inheritdoc/>
        public override bool Equals(object obj) => this.Equals(obj as ClusterMember);

        /// <inheritdoc/>
        public bool Equals(ClusterMember other) => other != null
            && this.SiloAddress.Equals(other.SiloAddress)
            && this.Status == other.Status
            && string.Equals(this.Name, other.Name, StringComparison.Ordinal)
            && MetadataEquals(this.Metadata, other.Metadata);

        /// <inheritdoc/>
        public override int GetHashCode() => this.SiloAddress.GetConsistentHashCode();

        /// <inheritdoc/>
        public override string ToString() => $"{this.SiloAddress}/{this.Name}/{this.Status}/MetadataAvailable={this.IsMetadataAvailable}/MetadataCount={this.Metadata?.Metadata.Count ?? 0}";

        private static bool MetadataEquals(SiloMetadata left, SiloMetadata right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left is null || right is null || left.Metadata.Count != right.Metadata.Count)
            {
                return false;
            }

            foreach (var (key, value) in left.Metadata)
            {
                if (!right.Metadata.TryGetValue(key, out var otherValue) || !string.Equals(value, otherValue, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
