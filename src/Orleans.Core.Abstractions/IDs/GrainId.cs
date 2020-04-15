using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;
using Orleans.Concurrency;

namespace Orleans.Runtime
{
    /// <summary>
    /// Identifies a grain.
    /// </summary>
    [Immutable]
    [Serializable]
    [StructLayout(LayoutKind.Auto)]
    public readonly struct GrainId : IEquatable<GrainId>, IComparable<GrainId>, ISerializable
    {
        /// <summary>
        /// Creates a new <see cref="GrainType"/> instance.
        /// </summary>
        public GrainId(GrainType type, IdSpan key)
        {
            Type = type;
            Key = key;
        }

        /// <summary>
        /// Creates a new <see cref="GrainType"/> instance.
        /// </summary>
        public GrainId(byte[] type, byte[] key) : this(new GrainType(type), new IdSpan(key))
        {
        }

        /// <summary>
        /// Creates a new <see cref="GrainType"/> instance.
        /// </summary>
        public GrainId(GrainType type, byte[] key) : this(type, new IdSpan(key))
        {
        }

        /// <summary>
        /// Creates a new <see cref="GrainType"/> instance.
        /// </summary>
        private GrainId(SerializationInfo info, StreamingContext context)
        {
            Type = new GrainType(IdSpan.UnsafeCreate((byte[])info.GetValue("tv", typeof(byte[])), info.GetInt32("th")));
            Key = IdSpan.UnsafeCreate((byte[])info.GetValue("kv", typeof(byte[])), info.GetInt32("kh"));
        }

        /// <summary>
        /// The grain type.
        /// </summary>
        public GrainType Type { get; }

        /// <summary>
        /// The key.
        /// </summary>
        public IdSpan Key { get; }

        // TODO: remove implicit conversion (potentially make explicit to start with)
        public static implicit operator LegacyGrainId(GrainId id) => LegacyGrainId.FromGrainId(id);

        /// <summary>
        /// Creates a new <see cref="GrainType"/> instance.
        /// </summary>
        public static GrainId Create(string type, string key) => Create(GrainType.Create(type), key);

        /// <summary>
        /// Creates a new <see cref="GrainType"/> instance.
        /// </summary>
        public static GrainId Create(GrainType type, string key) => new GrainId(type, Encoding.UTF8.GetBytes(key));

        /// <summary>
        /// Creates a new <see cref="GrainType"/> instance.
        /// </summary>
        public static GrainId Create(GrainType type, IdSpan key) => new GrainId(type, key);

        /// <summary>
        /// <see langword="true"/> if this instance is the default value, <see langword="false"/> if it is not.
        /// </summary>
        public bool IsDefault => Type.IsDefault && Key.IsDefault;

        /// <inheritdoc/>
        public override bool Equals(object obj) => obj is GrainId id && this.Equals(id);

        /// <inheritdoc/>
        public bool Equals(GrainId other) => this.Type.Equals(other.Type) && this.Key.Equals(other.Key);

        /// <inheritdoc/>
        public override int GetHashCode() => HashCode.Combine(Type, Key);

        /// <inheritdoc/>
        public uint GetUniformHashCode() => unchecked((uint)this.GetHashCode());

        /// <inheritdoc/>
        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("tv", GrainType.UnsafeGetArray(Type));
            info.AddValue("th", Type.GetHashCode());
            info.AddValue("kv", IdSpan.UnsafeGetArray(Key));
            info.AddValue("kh", Key.GetHashCode());
        }

        /// <inheritdoc/>
        public int CompareTo(GrainId other)
        {
            var typeComparison = Type.CompareTo(other.Type);
            if (typeComparison != 0) return typeComparison;

            return Key.CompareTo(other.Key);
        }

        /// <inheritdoc/>
        public static bool operator ==(GrainId a, GrainId b) => a.Equals(b);

        /// <inheritdoc/>
        public static bool operator !=(GrainId a, GrainId b) => !a.Equals(b);

        /// <inheritdoc/>
        public static bool operator >(GrainId a, GrainId b) => a.CompareTo(b) > 0;

        /// <inheritdoc/>
        public static bool operator <(GrainId a, GrainId b) => a.CompareTo(b) < 0;

        /// <inheritdoc/>
        public override string ToString() => $"{Type.ToStringUtf8()}/{Key.ToStringUtf8()}";

        /// <summary>
        /// An <see cref="IEqualityComparer{T}"/> and <see cref="IComparer{T}"/> implementation for <see cref="GrainId"/>.
        /// </summary>
        public sealed class Comparer : IEqualityComparer<GrainId>, IComparer<GrainId>
        {
            /// <summary>
            /// A singleton <see cref="Comparer"/> instance.
            /// </summary>
            public static Comparer Instance { get; } = new Comparer();

            /// <inheritdoc/>
            public int Compare(GrainId x, GrainId y) => x.CompareTo(y);

            /// <inheritdoc/>
            public bool Equals(GrainId x, GrainId y) => x.Equals(y);

            /// <inheritdoc/>
            public int GetHashCode(GrainId obj) => obj.GetHashCode();
        }
    }
}
