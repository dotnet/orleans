using System;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Diagnostics;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;

#nullable enable
namespace Orleans.Runtime
{
    /// <summary>
    /// Uniquely identifies a grain activation.
    /// </summary>
    [Serializable, Immutable]
    [GenerateSerializer]
    [JsonConverter(typeof(ActivationIdConverter))]
    public readonly struct ActivationId : IEquatable<ActivationId>, ISpanFormattable
    {
        [DataMember(Order = 0)]
        [Id(0)]
        internal readonly Guid Key;

        /// <summary>
        /// Initializes a new instance of the <see cref="ActivationId"/> struct.
        /// </summary>
        /// <param name="key">The activation id.</param>
        public ActivationId(Guid key) => Key = key;

        /// <summary>
        /// Gets a value indicating whether the instance is the default instance.
        /// </summary>
        public bool IsDefault => Key == default;

        /// <summary>
        /// Returns a new, random activation id.
        /// </summary>
        /// <returns>A new, random activation id.</returns>
        public static ActivationId NewId() => new(Guid.NewGuid());

        /// <summary>
        /// Returns an activation id which has been computed deterministically and reproducibly from the provided grain id.
        /// </summary>
        /// <param name="grain">The grain id.</param>
        /// <returns>An activation id which has been computed deterministically and reproducibly from the provided grain id.</returns>
        public static ActivationId GetDeterministic(GrainId grain)
        {
            Span<byte> temp = stackalloc byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(temp, grain.Type.GetUniformHashCode());
            BinaryPrimitives.WriteUInt64LittleEndian(temp[8..], grain.Key.GetUniformHashCode());
            var key = new Guid(temp);
            return new ActivationId(key);
        }

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is ActivationId other && Key.Equals(other.Key);

        /// <inheritdoc />
        public bool Equals(ActivationId other) => Key.Equals(other.Key);

        /// <inheritdoc />
        public override int GetHashCode() => Key.GetHashCode();

        /// <inheritdoc />
        public override string ToString() => $"@{Key:N}";

        string IFormattable.ToString(string? format, IFormatProvider? formatProvider) => ToString();

        bool ISpanFormattable.TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
            => destination.TryWrite($"@{Key:N}", out charsWritten);

        /// <summary>
        /// Returns a string representation of this activation id which can be parsed by <see cref="FromParsableString"/>.
        /// </summary>
        /// <returns>A string representation of this activation id which can be parsed by <see cref="FromParsableString"/>.</returns>
        public string ToParsableString() => ToString();

        /// <summary>
        /// Parses a string representation of an activation id which was created using <see cref="ToParsableString"/>.
        /// </summary>
        /// <param name="activationId">The string representation of the activation id.</param>
        /// <returns>The activation id.</returns>
        public static ActivationId FromParsableString(string activationId)
        {
            var span = activationId.AsSpan();
            return span.Length == 33 && span[0] == '@' ? new(Guid.ParseExact(span[1..], "N")) : throw new FormatException($"Invalid activation id: {activationId}");
        }

        /// <summary>
        /// Compares the provided operands for equality.
        /// </summary>
        /// <param name="left">The left operand.</param>
        /// <param name="right">The right operand.</param>
        /// <returns><see langword="true"/> if the provided values are equal, otherwise <see langword="false"/>.</returns>
        public static bool operator ==(ActivationId left, ActivationId right) => left.Equals(right);

        /// <summary>
        /// Compares the provided operands for inequality.
        /// </summary>
        /// <param name="left">The left operand.</param>
        /// <param name="right">The right operand.</param>
        /// <returns><see langword="true"/> if the provided values are not equal, otherwise <see langword="false"/>.</returns>
        public static bool operator !=(ActivationId left, ActivationId right) => !(left == right);
    }

    /// <summary>
    /// Functionality for converting <see cref="ActivationId"/> instances to and from their JSON representation.
    /// </summary>
    public sealed class ActivationIdConverter : JsonConverter<ActivationId>
    {
        /// <inheritdoc />
        public override ActivationId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => ActivationId.FromParsableString(reader.GetString()!);

        /// <inheritdoc />
        public override void Write(Utf8JsonWriter writer, ActivationId value, JsonSerializerOptions options)
        {
            Span<byte> buf = stackalloc byte[33];
            buf[0] = (byte)'@';
            Utf8Formatter.TryFormat(value.Key, buf[1..], out var len, 'N');
            Debug.Assert(len == 32);
            writer.WriteStringValue(buf);
        }
    }
}
