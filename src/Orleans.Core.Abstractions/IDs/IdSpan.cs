using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;

namespace Orleans.Runtime
{
    /// <summary>
    /// Primitive type for identities, representing a sequence of bytes.
    /// </summary>
    [Immutable]
    [Serializable]
    [StructLayout(LayoutKind.Auto)]
    [GenerateSerializer]
    public readonly struct IdSpan : IEquatable<IdSpan>, IComparable<IdSpan>, ISerializable
    {
        /// <summary>
        /// The stable hash of the underlying value.
        /// </summary>
        [Id(0)]
        private readonly int _hashCode;

        /// <summary>
        /// The underlying value.
        /// </summary>
        [Id(1)]
        private readonly byte[] _value;

        /// <summary>
        /// Initializes a new instance of the <see cref="IdSpan"/> struct.
        /// </summary>
        /// <param name="value">
        /// The value.
        /// </param>
        public IdSpan(byte[] value)
        {
            _value = value;
            _hashCode = GetHashCode(value);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="IdSpan"/> struct.
        /// </summary>
        /// <param name="value">
        /// The value.
        /// </param>
        /// <param name="hashCode">
        /// The hash code of the value.
        /// </param>
        private IdSpan(byte[] value, int hashCode)
        {
            _value = value;
            _hashCode = hashCode;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="IdSpan"/> struct.
        /// </summary>
        /// <param name="info">
        /// The serialization info.
        /// </param>
        /// <param name="context">
        /// The context.
        /// </param>
        private IdSpan(SerializationInfo info, StreamingContext context)
        {
            _value = (byte[])info.GetValue("v", typeof(byte[]));
            _hashCode = info.GetInt32("h");
        }

        /// <summary>
        /// Gets the underlying value.
        /// </summary>
        public ReadOnlyMemory<byte> Value => _value;

        /// <summary>
        /// Gets a value indicating whether this instance is the default value.
        /// </summary>
        public bool IsDefault => _value is null || _value.Length == 0;

        /// <summary>
        /// Creates a new <see cref="IdSpan"/> instance from the provided value.
        /// </summary>
        /// <returns>
        /// A new <see cref="IdSpan"/> corresponding to the provided id.
        /// </returns>
        public static IdSpan Create(string id) => id is string idString ? new IdSpan(Encoding.UTF8.GetBytes(idString)) : default;

        /// <summary>
        /// Returns a span representation of this instance.
        /// </summary>
        /// <returns>
        /// A span representation fo this instance.
        /// </returns>
        public ReadOnlySpan<byte> AsSpan() => _value;

        /// <inheritdoc/>
        public override bool Equals(object obj)
        {
            return obj is IdSpan kind && this.Equals(kind);
        }

        /// <inheritdoc/>
        public bool Equals(IdSpan obj)
        {
            if (object.ReferenceEquals(_value, obj._value)) return true;
            if (_value is null || obj._value is null)
            {
                if (_value is { Length: 0 } || obj._value is { Length: 0 })
                {
                    return true;
                }

                return false;
            }

            return _value.AsSpan().SequenceEqual(obj._value);
        }

        /// <inheritdoc/>
        public override int GetHashCode() => _hashCode;

        /// <summary>
        /// Returns a uniform, stable hash code for an <see cref="IdSpan"/>.
        /// </summary>
        /// <returns>
        /// The hash code of this instance.
        /// </returns>
        public uint GetUniformHashCode() => unchecked((uint)_hashCode);

        /// <inheritdoc/>
        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("v", _value);
            info.AddValue("h", _hashCode);
        }

        /// <summary>
        /// Creates an instance, specifying both the hash code and the value.
        /// </summary>
        /// <remarks>
        /// This method is intended for use by serializers and other low-level libraries.
        /// </remarks>
        /// <param name="value">
        /// The underlying value.
        /// </param>
        /// <param name="hashCode">
        /// The hash of the underlying value.
        /// </param>
        /// <returns>
        /// An <see cref="IdSpan"/> instance.
        /// </returns>
        public static IdSpan UnsafeCreate(byte[] value, int hashCode) => new IdSpan(value, hashCode);

        /// <summary>
        /// Gets the underlying array from this instance.
        /// </summary>
        /// <param name="id">The id span.</param>
        /// <returns>The underlying array from this instance.</returns>
        public static byte[] UnsafeGetArray(IdSpan id) => id._value;

        /// <inheritdoc/>
        public int CompareTo(IdSpan other) => _value.AsSpan().SequenceCompareTo(other._value.AsSpan());

        /// <inheritdoc/>
        public override string ToString() => this.ToStringUtf8();

        /// <summary>
        /// Returns a string representation of this instance, decoding the value as UTF8.
        /// </summary>
        /// <returns>
        /// A string representation fo this instance.
        /// </returns>
        public string ToStringUtf8()
        {
            if (_value is object) return Encoding.UTF8.GetString(_value);
            return null;
        }

        /// <summary>
        /// Compares the provided operands for equality.
        /// </summary>
        /// <param name="left">The left operand.</param>
        /// <param name="right">The right operand.</param>
        /// <returns><see langword="true"/> if the provided values are equal, otherwise <see langword="false"/>.</returns>
        public static bool operator ==(IdSpan left, IdSpan right) => left.Equals(right);

        /// <summary>
        /// Compares the provided operands for inequality.
        /// </summary>
        /// <param name="left">The left operand.</param>
        /// <param name="right">The right operand.</param>
        /// <returns><see langword="true"/> if the provided values are not equal, otherwise <see langword="false"/>.</returns>
        public static bool operator !=(IdSpan left, IdSpan right) => !left.Equals(right);

        /// <summary>
        /// Gets a hashed representation of the provided value.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <returns>A hashed representation of the provided value.</returns>
        private static int GetHashCode(byte[] value) => (int)JenkinsHash.ComputeHash(value);

        /// <summary>
        /// An <see cref="IEqualityComparer{T}"/> and <see cref="IComparer{T}"/> implementation for <see cref="IdSpan"/>.
        /// </summary>
        public sealed class Comparer : IEqualityComparer<IdSpan>, IComparer<IdSpan>
        {
            /// <summary>
            /// Gets the singleton <see cref="Comparer"/> instance.
            /// </summary>
            public static Comparer Instance { get; } = new Comparer();

            /// <inheritdoc/>
            public int Compare(IdSpan x, IdSpan y) => x.CompareTo(y);

            /// <inheritdoc/>
            public bool Equals(IdSpan x, IdSpan y) => x.Equals(y);

            /// <inheritdoc/>
            public int GetHashCode(IdSpan obj) => obj.GetHashCode();
        }
    }
}
