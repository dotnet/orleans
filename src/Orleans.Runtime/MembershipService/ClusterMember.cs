using System;

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
        {
            SiloAddress = siloAddress ?? throw new ArgumentNullException(nameof(siloAddress));
            Status = status;
            Name = name;
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

        /// <inheritdoc/>
        public override bool Equals(object obj) => Equals(obj as ClusterMember);

        /// <inheritdoc/>
        public bool Equals(ClusterMember other) => other != null
            && SiloAddress.Equals(other.SiloAddress)
            && Status == other.Status
            && string.Equals(Name, other.Name, StringComparison.Ordinal);

        /// <inheritdoc/>
        public override int GetHashCode() => SiloAddress.GetConsistentHashCode();

        /// <inheritdoc/>
        public override string ToString() => $"{SiloAddress}/{Name}/{Status}";
    }
}
