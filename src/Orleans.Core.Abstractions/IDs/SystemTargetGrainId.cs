using System;
using System.Collections.Generic;

namespace Orleans.Runtime
{
    /// <summary>
    /// Identifies a system target.
    /// </summary>
    public readonly struct SystemTargetGrainId : IEquatable<SystemTargetGrainId>, IComparable<SystemTargetGrainId>
    {
        private const char SegmentSeparator = '+';

        /// <summary>
        /// Creates a new <see cref="SystemTargetGrainId"/> instance.
        /// </summary>
        private SystemTargetGrainId(GrainId grainId)
        {
            this.GrainId = grainId;
        }

        /// <summary>
        /// Gets the underlying identity.
        /// </summary>
        public GrainId GrainId { get; }

        /// <summary>
        /// Creates a new <see cref="SystemTargetGrainId"/> instance.
        /// </summary>
        public static SystemTargetGrainId Create(GrainType kind, SiloAddress address) => new SystemTargetGrainId(GrainId.Create(kind, address.ToParsableString()));

        /// <summary>
        /// Creates a new <see cref="SystemTargetGrainId"/> instance.
        /// </summary>
        public static SystemTargetGrainId Create(GrainType kind, SiloAddress address, string extraIdentifier)
        {
            if (extraIdentifier is string)
            {
                return new SystemTargetGrainId(GrainId.Create(kind, address.ToParsableString() + SegmentSeparator + extraIdentifier));
            }

            return Create(kind, address);
        }

        /// <summary>
        /// Returns <see langword="true"/> if the provided instance represents a system target, <see langword="false"/> if otherwise.
        /// </summary>
        public static bool IsSystemTargetGrainId(in GrainId id) => id.Type.AsSpan().StartsWith(GrainTypePrefix.SystemTargetPrefixBytes.Span);

        /// <summary>
        /// Converts the provided <see cref="GrainId"/> to a <see cref="SystemTargetGrainId"/>. A return value indicates whether the operation succeeded.
        /// </summary>
        public static bool TryParse(GrainId grainId, out SystemTargetGrainId systemTargetId)
        {
            if (!IsSystemTargetGrainId(grainId))
            {
                systemTargetId = default;
                return false;
            }

            systemTargetId = new SystemTargetGrainId(grainId);
            return true;
        }

        /// <summary>
        /// Returns a new <see cref="SystemTargetGrainId"/> targeting the provided address.
        /// </summary>
        public SystemTargetGrainId WithSiloAddress(SiloAddress siloAddress)
        {
            string extraIdentifier = null;
            var key = this.GrainId.Key.ToStringUtf8();
            if (key.IndexOf(SegmentSeparator) is int index && index >= 0)
            {
                extraIdentifier = key.Substring(index + 1);
            }

            return Create(this.GrainId.Type, siloAddress, extraIdentifier);
        }

        /// <summary>
        /// Gets the <see cref="SiloAddress"/> of the system target.
        /// </summary>
        public SiloAddress GetSiloAddress()
        {
            var key = this.GrainId.Key.ToStringUtf8();
            if (key.IndexOf(SegmentSeparator) is int index && index >= 0)
            {
                key = key.Substring(0, index);
            }

            return SiloAddress.FromParsableString(key);
        }

        /// <summary>
        /// Creates a <see cref="GrainId"/> for a grain service.
        /// </summary>
        public static GrainId CreateGrainServiceGrainId(int typeCode, string grainSystemId, SiloAddress address)
        {
            var grainType = GrainType.Create($"{GrainTypePrefix.GrainServicePrefix}{typeCode:X8}{grainSystemId}");
            return GrainId.Create(grainType, address.ToParsableString());
        }

        /// <summary>
        /// Creates a system target <see cref="GrainType"/> with the provided name.
        /// </summary>
        public static GrainType CreateGrainType(string name) => GrainType.Create($"{GrainTypePrefix.SystemTargetPrefix}{name}");

        /// <inheritdoc/>
        public bool Equals(SystemTargetGrainId other) => this.GrainId.Equals(other.GrainId);

        /// <inheritdoc/>
        public override bool Equals(object obj) => obj is SystemTargetGrainId observer && this.Equals(observer);

        /// <inheritdoc/>
        public override int GetHashCode() => this.GrainId.GetHashCode();

        /// <inheritdoc/>
        public override string ToString() => this.GrainId.ToString();

        /// <inheritdoc/>
        public int CompareTo(SystemTargetGrainId other) => this.GrainId.CompareTo(other.GrainId);

        /// <inheritdoc/>
        public static bool operator ==(SystemTargetGrainId left, SystemTargetGrainId right) => left.Equals(right);

        /// <inheritdoc/>
        public static bool operator !=(SystemTargetGrainId left, SystemTargetGrainId right) => !(left == right);

        /// <inheritdoc/>
        public static bool operator <(SystemTargetGrainId left, SystemTargetGrainId right) => left.CompareTo(right) < 0;

        /// <inheritdoc/>
        public static bool operator <=(SystemTargetGrainId left, SystemTargetGrainId right) => left.CompareTo(right) <= 0;

        /// <inheritdoc/>
        public static bool operator >(SystemTargetGrainId left, SystemTargetGrainId right) => left.CompareTo(right) > 0;

        /// <inheritdoc/>
        public static bool operator >=(SystemTargetGrainId left, SystemTargetGrainId right) => left.CompareTo(right) >= 0;

        /// <summary>
        /// An <see cref="IEqualityComparer{T}"/> and <see cref="IComparer{T}"/> implementation for <see cref="SystemTargetGrainId"/>.
        /// </summary>
        public sealed class Comparer : IEqualityComparer<SystemTargetGrainId>, IComparer<SystemTargetGrainId>
        {
            /// <summary>
            /// A singleton <see cref="Comparer"/> instance.
            /// </summary>
            public static Comparer Instance { get; } = new Comparer();

            /// <inheritdoc/>
            public int Compare(SystemTargetGrainId x, SystemTargetGrainId y) => x.CompareTo(y);

            /// <inheritdoc/>
            public bool Equals(SystemTargetGrainId x, SystemTargetGrainId y) => x.Equals(y);

            /// <inheritdoc/>
            public int GetHashCode(SystemTargetGrainId obj) => obj.GetHashCode();
        }
    }
}
