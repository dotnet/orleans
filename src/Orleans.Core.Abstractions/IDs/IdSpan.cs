using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;
using Orleans.Concurrency;

namespace Orleans.Runtime
{
    /// <summary>
    /// Primitive type for identities, representing a sequence of bytes.
    /// </summary>
    [Immutable]
    [Serializable]
    [StructLayout(LayoutKind.Auto)]
    public readonly struct IdSpan : IEquatable<IdSpan>, IComparable<IdSpan>, ISerializable
    {
        private readonly byte[] _value;
        private readonly int _hashCode;

        /// <summary>
        /// Creates a new <see cref="IdSpan"/> instance from the provided value.
        /// </summary>
        internal IdSpan(byte[] value)
        {
            _value = value;
            _hashCode = GetHashCode(value);
        }

        /// <summary>
        /// Creates a new <see cref="IdSpan"/> instance from the provided value.
        /// </summary>
        private IdSpan(byte[] value, int hashCode)
        {
            _value = value;
            _hashCode = hashCode;
        }

        /// <summary>
        /// Creates a new <see cref="IdSpan"/> instance from the provided value.
        /// </summary>
        private IdSpan(SerializationInfo info, StreamingContext context)
        {
            _value = (byte[])info.GetValue("v", typeof(byte[]));
            _hashCode = info.GetInt32("h");
        }

        public ReadOnlyMemory<byte> Value => _value;

        /// <summary>
        /// <see langword="true"/> if this instance is the default value, <see langword="false"/> if it is not.
        /// </summary>
        public bool IsDefault => _value is null || _value.Length == 0;

        /// <summary>
        /// Creates a new <see cref="IdSpan"/> instance from the provided value.
        /// </summary>
        public static IdSpan Create(string id) => id is string idString ? new IdSpan(Encoding.UTF8.GetBytes(idString)) : default;

        /// <summary>
        /// Returns a span representation of this instance.
        /// </summary>
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
            if (_value is null || obj._value is null) return false;
            return _value.AsSpan().SequenceEqual(obj._value);
        }

        /// <inheritdoc/>
        public override int GetHashCode() => _hashCode;

        /// <summary>
        /// Return uniform, stable hash code for IdSpan
        /// </summary>
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
        public static IdSpan UnsafeCreate(byte[] value, int hashCode) => new IdSpan(value, hashCode);

        /// <inheritdoc/>
        public static byte[] UnsafeGetArray(IdSpan id) => id._value;

        /// <inheritdoc/>
        public int CompareTo(IdSpan other) => _value.AsSpan().SequenceCompareTo(other._value.AsSpan());

        /// <inheritdoc/>
        public override string ToString() => this.ToStringUtf8();

        /// <summary>
        /// Returns a string representation of this instance, decoding the value as UTF8.
        /// </summary>
        public string ToStringUtf8()
        {
            if (_value is object) return Encoding.UTF8.GetString(_value);
            return null;
        }

        /// <inheritdoc/>
        public static bool operator ==(IdSpan a, IdSpan b) => a.Equals(b);

        /// <inheritdoc/>
        public static bool operator !=(IdSpan a, IdSpan b) => !a.Equals(b);

        private static int GetHashCode(byte[] value) => (int)JenkinsHash.ComputeHash(value);

        /// <summary>
        /// An <see cref="IEqualityComparer{T}"/> and <see cref="IComparer{T}"/> implementation for <see cref="IdSpan"/>.
        /// </summary>
        public sealed class Comparer : IEqualityComparer<IdSpan>, IComparer<IdSpan>
        {
            /// <summary>
            /// A singleton <see cref="Comparer"/> instance.
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
