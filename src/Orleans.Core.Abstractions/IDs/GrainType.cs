using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;

namespace Orleans.Runtime
{
    /// <summary>
    /// Represents the type of a grain.
    /// </summary>
    [Immutable]
    [Serializable]
    [StructLayout(LayoutKind.Auto)]
    [GenerateSerializer]
    public readonly struct GrainType : IEquatable<GrainType>, IComparable<GrainType>, ISerializable
    {
        /// <summary>
        /// Creates a new <see cref="GrainType"/> instance.
        /// </summary>
        public GrainType(IdSpan id) => Value = id;

        /// <summary>
        /// Creates a new <see cref="GrainType"/> instance.
        /// </summary>
        public GrainType(byte[] value) => Value = new IdSpan(value);

        /// <summary>
        /// Creates a new <see cref="GrainType"/> instance.
        /// </summary>
        private GrainType(SerializationInfo info, StreamingContext context)
        {
            Value = IdSpan.UnsafeCreate((byte[])info.GetValue("v", typeof(byte[])), info.GetInt32("h"));
        }

        /// <summary>
        /// The underlying value.
        /// </summary>
        [Id(1)]
        public IdSpan Value { get; }

        /// <summary>
        /// Returns a span representation of this instance.
        /// </summary>
        public ReadOnlySpan<byte> AsSpan() => this.Value.AsSpan();

        /// <summary>
        /// Creates a new <see cref="GrainType"/> instance.
        /// </summary>
        public static GrainType Create(string value) => new GrainType(Encoding.UTF8.GetBytes(value));

        /// <inheritdoc/>
        public static explicit operator IdSpan(GrainType kind) => kind.Value;

        /// <inheritdoc/>
        public static explicit operator GrainType(IdSpan id) => new GrainType(id);

        /// <summary>
        /// <see langword="true"/> if this instance is the default value, <see langword="false"/> if it is not.
        /// </summary>
        public bool IsDefault => Value.IsDefault;

        /// <inheritdoc/>
        public override bool Equals(object obj) => obj is GrainType kind && this.Equals(kind);

        /// <inheritdoc/>
        public bool Equals(GrainType obj) => Value.Equals(obj.Value);

        /// <inheritdoc/>
        public override int GetHashCode() => Value.GetHashCode();

        /// <summary>
        /// Generates uniform, stable hash code for GrainType
        /// </summary>
        public uint GetUniformHashCode() => Value.GetUniformHashCode();

        public static byte[] UnsafeGetArray(GrainType id) => IdSpan.UnsafeGetArray(id.Value);

        /// <inheritdoc/>
        public int CompareTo(GrainType other) => Value.CompareTo(other.Value);

        /// <inheritdoc/>
        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("v", IdSpan.UnsafeGetArray(Value));
            info.AddValue("h", Value.GetHashCode());
        }

        /// <inheritdoc/>
        public override string ToString() => this.ToStringUtf8();

        /// <summary>
        /// Returns a string representation of this instance, decoding the value as UTF8.
        /// </summary>
        public string ToStringUtf8() => Value.ToStringUtf8();

        /// <inheritdoc/>
        public static bool operator ==(GrainType left, GrainType right)
        {
            return left.Equals(right);
        }

        /// <inheritdoc/>
        public static bool operator !=(GrainType left, GrainType right)
        {
            return !(left == right);
        }

        /// <summary>
        /// An <see cref="IEqualityComparer{T}"/> and <see cref="IComparer{T}"/> implementation for <see cref="GrainType"/>.
        /// </summary>
        public sealed class Comparer : IEqualityComparer<GrainType>, IComparer<GrainType>
        {
            /// <summary>
            /// A singleton <see cref="Comparer"/> instance.
            /// </summary>
            public static Comparer Instance { get; } = new Comparer();

            /// <inheritdoc/>
            public int Compare(GrainType x, GrainType y) => x.CompareTo(y);

            /// <inheritdoc/>
            public bool Equals(GrainType x, GrainType y) => x.Equals(y);

            /// <inheritdoc/>
            public int GetHashCode(GrainType obj) => obj.GetHashCode();
        }
    }
}
