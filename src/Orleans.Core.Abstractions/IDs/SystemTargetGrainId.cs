using System;

namespace Orleans.Runtime
{
    /// <summary>
    /// Represents the identity of a system target.
    /// </summary>
    public readonly struct SystemTargetGrainId : IEquatable<SystemTargetGrainId>, IComparable<SystemTargetGrainId>
    {
        private SystemTargetGrainId(GrainId grainId)
        {
            this.GrainId = grainId;
        }

        public GrainId GrainId { get; }

        public static SystemTargetGrainId Create(GrainType kind, SiloAddress address) => new SystemTargetGrainId(GrainId.Create(kind, address.ToParsableString()));

        public static SystemTargetGrainId Create(GrainType kind, SiloAddress address, string extraIdentifier)
        {
            if (extraIdentifier is string)
            {
                return new SystemTargetGrainId(GrainId.Create(kind, address.ToParsableString() + "+" + extraIdentifier));
            }

            return Create(kind, address);
        }

        private static bool IsSystemTarget(in GrainType type) => type.Value.Span.StartsWith(GrainTypePrefix.SystemTargetPrefixBytes.Span);

        public static bool IsSystemTarget(in GrainId id) => IsSystemTarget(id.Type);

        public static bool TryParse(GrainId grainId, out SystemTargetGrainId systemTargetId)
        {
            if (!IsSystemTarget(grainId))
            {
                systemTargetId = default;
                return false;
            }

            systemTargetId = new SystemTargetGrainId(grainId);
            return true;
        }

        public SystemTargetGrainId WithSiloAddress(SiloAddress address)
        {
            string extraIdentifier = null;
            var key = this.GrainId.Key.ToStringUtf8();
            if (key.IndexOf('+') is int index && index >= 0)
            {
                extraIdentifier = key.Substring(index + 1);
            }

            return Create(this.GrainId.Type, address, extraIdentifier);
        }

        public SiloAddress GetSiloAddress()
        {
            var key = this.GrainId.Key.ToStringUtf8();
            if (key.IndexOf('+') is int index && index >= 0)
            {
                key = key.Substring(0, index);
            }

            return SiloAddress.FromParsableString(key);
        }

        public static GrainId CreateGrainServiceGrainId(int typeCode, string grainSystemId, SiloAddress address)
        {
            var grainType = GrainType.Create($"{GrainTypePrefix.GrainServicePrefix}{typeCode:X8}{grainSystemId}");
            return GrainId.Create(grainType, address.ToParsableString());
        }

        public static GrainType CreateGrainType(string name) => GrainType.Create($"{GrainTypePrefix.SystemTargetPrefix}{name}");

        public bool Equals(SystemTargetGrainId other) => this.GrainId.Equals(other.GrainId);

        public override bool Equals(object obj) => obj is SystemTargetGrainId observer && this.Equals(observer);

        public override int GetHashCode() => this.GrainId.GetHashCode();

        public override string ToString() => this.GrainId.ToString();

        public int CompareTo(SystemTargetGrainId other) => this.GrainId.CompareTo(other.GrainId);
    }
}
