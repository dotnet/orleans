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
        /// Initializes a new instance of the <see cref="GrainType"/> struct. 
        /// </summary>
        /// <param name="id">
        /// The id.
        /// </param>
        public GrainType(IdSpan id) => Value = id;

        /// <summary>
        /// Initializes a new instance of the <see cref="GrainType"/> struct. 
        /// </summary>
        /// <param name="value">
        /// The raw id value.
        /// </param>
        public GrainType(byte[] value) => Value = new IdSpan(value);

        /// <summary>
        /// Initializes a new instance of the <see cref="GrainType"/> struct. 
        /// </summary>
        /// <param name="info">
        /// The serialization info.
        /// </param>
        /// <param name="context">
        /// The context.
        /// </param>
        private GrainType(SerializationInfo info, StreamingContext context)
        {
            Value = IdSpan.UnsafeCreate((byte[])info.GetValue("v", typeof(byte[])), info.GetInt32("h"));
        }

        /// <summary>
        /// Gets the underlying value.
        /// </summary>
        [Id(1)]
        public IdSpan Value { get; }

        /// <summary>
        /// Returns a span representation of this instance.
        /// </summary>
        /// <returns>
        /// A <see cref="ReadOnlySpan{Byte}"/> representation of the value.
        /// </returns>
        public ReadOnlySpan<byte> AsSpan() => this.Value.AsSpan();

        /// <summary>
        /// Creates a new <see cref="GrainType"/> instance.
        /// </summary>
        /// <param name="value">
        /// The value.
        /// </param>
        /// <returns>
        /// The newly created <see cref="GrainType"/> instance.
        /// </returns>
        public static GrainType Create(string value) => new GrainType(Encoding.UTF8.GetBytes(value));

        /// <summary>
        /// Converts a <see cref="GrainType"/> to a <see cref="IdSpan"/>.
        /// </summary>
        /// <param name="kind">The grain type to convert.</param>
        /// <returns>The corresponding <see cref="IdSpan"/>.</returns>
        public static explicit operator IdSpan(GrainType kind) => kind.Value;

        /// <summary>
        /// Converts a <see cref="IdSpan"/> to a <see cref="GrainType"/>.
        /// </summary>
        /// <param name="id">The id span to convert.</param>
        /// <returns>The corresponding <see cref="GrainType"/>.</returns>
        public static explicit operator GrainType(IdSpan id) => new GrainType(id);

        /// <summary>
        /// Gets a value indicating whether this instance is the default value.
        /// </summary>
        public bool IsDefault => Value.IsDefault;

        /// <inheritdoc/>
        public override bool Equals(object obj) => obj is GrainType kind && this.Equals(kind);

        /// <inheritdoc/>
        public bool Equals(GrainType obj) => Value.Equals(obj.Value);

        /// <inheritdoc/>
        public override int GetHashCode() => Value.GetHashCode();

        /// <summary>
        /// Generates a uniform, stable hash code for this grain type. 
        /// </summary>
        /// <returns>
        /// A uniform, stable hash of this instance.
        /// </returns>
        public uint GetUniformHashCode() => Value.GetUniformHashCode();

        /// <summary>
        /// Returns the array underlying a grain type instance.
        /// </summary>
        /// <param name="id">The grain type.</param>
        /// <returns>The array underlying a grain type instance.</returns>
        /// <remarks>
        /// The returned array must not be modified.
        /// </remarks>
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
        /// <returns>
        /// A <see cref="string"/> representation of this instance.
        /// </returns>
        public string ToStringUtf8() => Value.ToStringUtf8();

        /// <summary>
        /// Compares the provided operands for equality.
        /// </summary>
        /// <param name="left">The left operand.</param>
        /// <param name="right">The right operand.</param>
        /// <returns><see langword="true"/> if the provided values are equal, otherwise <see langword="false"/>.</returns>
        public static bool operator ==(GrainType left, GrainType right)
        {
            return left.Equals(right);
        }

        /// <summary>
        /// Compares the provided operands for inequality.
        /// </summary>
        /// <param name="left">The left operand.</param>
        /// <param name="right">The right operand.</param>
        /// <returns><see langword="true"/> if the provided values are not equal, otherwise <see langword="false"/>.</returns>
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
            /// Gets the singleton <see cref="Comparer"/> instance.
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
