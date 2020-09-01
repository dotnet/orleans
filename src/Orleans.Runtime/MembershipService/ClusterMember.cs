using System;

namespace Orleans.Runtime
{
    [Serializable]
    public sealed class ClusterMember : IEquatable<ClusterMember>
    {
        public ClusterMember(SiloAddress siloAddress, SiloStatus status, string name)
        {
            this.SiloAddress = siloAddress ?? throw new ArgumentNullException(nameof(siloAddress));
            this.Status = status;
            this.Name = name;
        }

        public SiloAddress SiloAddress { get; }
        public SiloStatus Status { get; }
        public string Name { get; }

        public override bool Equals(object obj) => this.Equals(obj as ClusterMember);

        public bool Equals(ClusterMember other) => other != null
            && this.SiloAddress.Equals(other.SiloAddress)
            && this.Status == other.Status
            && string.Equals(this.Name, other.Name, StringComparison.Ordinal);

        public override int GetHashCode() => this.SiloAddress.GetConsistentHashCode();

        public override string ToString() => $"{this.SiloAddress}/{this.Name}/{this.Status}";
    }
}
