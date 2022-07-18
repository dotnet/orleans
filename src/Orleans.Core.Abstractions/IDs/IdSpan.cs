using System;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;

#nullable enable
namespace Orleans.Runtime
{
    /// <summary>
    /// Primitive type for identities, representing a sequence of bytes.
    /// </summary>
    [Immutable]
    [Serializable]
    [StructLayout(LayoutKind.Auto)]
    [GenerateSerializer]
    public readonly struct IdSpan : IEquatable<IdSpan>, IComparable<IdSpan>, ISerializable, ISpanFormattable
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
        private readonly byte[]? _value;

        /// <summary>
        /// Initializes a new instance of the <see cref="IdSpan"/> struct.
        /// </summary>
        /// <param name="value">
        /// The value.
        /// </param>
        public IdSpan(byte[] value)
        {
            _value = value;
            _hashCode = (int)JenkinsHash.ComputeHash(value);
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
        private IdSpan(byte[]? value, int hashCode)
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
            _value = (byte[]?)info.GetValue("v", typeof(byte[]));
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
        public static IdSpan Create(string? id) => id is string idString ? new IdSpan(Encoding.UTF8.GetBytes(idString)) : default;

        /// <summary>
        /// Returns a span representation of this instance.
        /// </summary>
        /// <returns>
        /// A span representation fo this instance.
        /// </returns>
        public ReadOnlySpan<byte> AsSpan() => _value;

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is IdSpan kind && Equals(kind);

        /// <inheritdoc/>
        public bool Equals(IdSpan obj) => _value == obj._value || _value.AsSpan().SequenceEqual(obj._value);

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
        public static IdSpan UnsafeCreate(byte[]? value, int hashCode) => new(value, hashCode);

        /// <summary>
        /// Gets the underlying array from this instance.
        /// </summary>
        /// <param name="id">The id span.</param>
        /// <returns>The underlying array from this instance.</returns>
        public static byte[]? UnsafeGetArray(IdSpan id) => id._value;

        /// <inheritdoc/>
        public int CompareTo(IdSpan other) => _value.AsSpan().SequenceCompareTo(other._value.AsSpan());

        /// <summary>
        /// Returns a string representation of this instance, decoding the value as UTF8.
        /// </summary>
        /// <returns>
        /// A string representation fo this instance.
        /// </returns>
        public override string? ToString() => _value is null ? null : Encoding.UTF8.GetString(_value);

        public bool TryFormat(Span<char> destination, out int charsWritten)
        {
            if (_value is null)
            {
                charsWritten = 0;
                return true;
            }

            var len = Encoding.UTF8.GetCharCount(_value);
            if (destination.Length < len)
            {
                charsWritten = 0;
                return false;
            }

            charsWritten = Encoding.UTF8.GetChars(_value, destination);
            return true;
        }

        string IFormattable.ToString(string? format, IFormatProvider? formatProvider) => ToString() ?? "";

        bool ISpanFormattable.TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) => TryFormat(destination, out charsWritten);

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
    }
}
