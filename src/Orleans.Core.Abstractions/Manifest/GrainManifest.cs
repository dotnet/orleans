using System;
using System.Collections.Immutable;
using Orleans.Runtime;

namespace Orleans.Metadata
{
    /// <summary>
    /// Information about available grains.
    /// </summary>
    [Serializable, GenerateSerializer, Immutable]
    public sealed class GrainManifest : IEquatable<GrainManifest>
    {
        [NonSerialized]
        private int? _hashCode;

        /// <summary>
        /// Initializes a new instance of the <see cref="GrainManifest"/> class.
        /// </summary>
        /// <param name="grains">
        /// The grain properties.
        /// </param>
        /// <param name="interfaces">
        /// The interface properties.
        /// </param>
        public GrainManifest(
            ImmutableDictionary<GrainType, GrainProperties> grains,
            ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties> interfaces)
        {
            this.Interfaces = interfaces;
            this.Grains = grains;
        }

        /// <summary>
        /// Gets the interfaces available on this silo.
        /// </summary>
        [Id(0)]
        public ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties> Interfaces { get; }

        /// <summary>
        /// Gets the grain types available on this silo.
        /// </summary>
        [Id(1)]
        public ImmutableDictionary<GrainType, GrainProperties> Grains { get; }

        public override int GetHashCode()
        {
            if (!_hashCode.HasValue)
            {
                var hashCode = new HashCode();
                hashCode.Add(Interfaces.Count);
                foreach (var (key, value) in Interfaces)
                {
                    hashCode.Add(key);
                    hashCode.Add(value);
                }

                hashCode.Add(Grains.Count);
                foreach (var (key, value) in Grains)
                {
                    hashCode.Add(key);
                    hashCode.Add(value);
                }

                _hashCode = hashCode.ToHashCode();
            }

            return _hashCode.Value;
        }

        public override bool Equals(object? obj) => obj is GrainManifest other && Equals(other);

        public bool Equals(GrainManifest? other)
        {
            if (ReferenceEquals(this, other)) return true;
            if (other is null) return false;
            if (Interfaces.Count != other.Interfaces.Count) return false;
            if (Grains.Count != other.Grains.Count) return false;
            foreach (var (key, value) in Interfaces)
            {
                if (!other.Interfaces.TryGetValue(key, out var otherValue)) return false;
                if (!value.Equals(otherValue)) return false;
            }

            foreach (var (key, value) in Grains)
            {
                if (!other.Grains.TryGetValue(key, out var otherValue)) return false;
                if (!value.Equals(otherValue)) return false;
            }

            return true;
        }
    }
}
