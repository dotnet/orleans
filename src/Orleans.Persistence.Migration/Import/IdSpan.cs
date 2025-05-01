using System;
using System.Runtime.Serialization;
using System.Text;
using global::Orleans.CodeGeneration;
using global::Orleans.Concurrency;

#nullable enable
namespace Orleans.Runtime
{
    /// <summary>
    /// Primitive type for identities, representing a sequence of bytes.
    /// </summary>
    public readonly struct IdSpan : ISpanFormattable
    {
        /// <summary>
        /// The underlying value.
        /// </summary>
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
        }

        public static implicit operator IdSpan(string value)
        {
            int byteCount = Encoding.UTF8.GetByteCount(value);
            var bytes = new byte[byteCount];
            Encoding.UTF8.GetBytes(value, 0, value.Length, bytes, 0);
            return new IdSpan(bytes);
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
    }
}
