using System;
using System.Collections.Generic;
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
            ArgumentNullException.ThrowIfNull(grains);
            ArgumentNullException.ThrowIfNull(interfaces);
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

        public override int GetHashCode() => _hashCode ??= HashCode.Combine(
            ComputeHashCode(Interfaces),
            ComputeHashCode(Grains));

        public override bool Equals(object? obj) => obj is GrainManifest other && Equals(other);

        public bool Equals(GrainManifest? other)
        {
            if (ReferenceEquals(this, other)) return true;
            if (other is null) return false;
            return DictionariesEqual(Interfaces, other.Interfaces) && DictionariesEqual(Grains, other.Grains);
        }

        private static bool DictionariesEqual<TKey, TValue>(
            ImmutableDictionary<TKey, TValue> left,
            ImmutableDictionary<TKey, TValue> right)
            where TKey : notnull
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left.Count != right.Count)
            {
                return false;
            }

            var rightDictionary = new Dictionary<TKey, TValue>(right.Count);
            foreach (var entry in right)
            {
                if (!rightDictionary.TryAdd(entry.Key, entry.Value))
                {
                    return false;
                }
            }

            var comparer = EqualityComparer<TValue>.Default;
            foreach (var entry in left)
            {
                if (!rightDictionary.TryGetValue(entry.Key, out var value)
                    || !comparer.Equals(entry.Value, value))
                {
                    return false;
                }
            }

            return true;
        }

        private static int ComputeHashCode<TKey, TValue>(ImmutableDictionary<TKey, TValue> dictionary)
            where TKey : notnull
        {
            var hash = 0;
            foreach (var entry in dictionary)
            {
                hash ^= HashCode.Combine(entry.Key, entry.Value);
            }

            return hash;
        }
    }
}
