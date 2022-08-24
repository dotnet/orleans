using System;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

#nullable enable
namespace Orleans.Runtime
{
    /// <summary>
    /// Identifies a grain.
    /// </summary>
    [Immutable]
    [Serializable]
    [JsonConverter(typeof(GrainIdJsonConverter))]
    [StructLayout(LayoutKind.Auto)]
    [GenerateSerializer]
    public readonly struct GrainId : IEquatable<GrainId>, IComparable<GrainId>, ISerializable, ISpanFormattable
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
        private GrainId(SerializationInfo info, StreamingContext context)
        {
            Type = new GrainType(IdSpan.UnsafeCreate((byte[]?)info.GetValue("tv", typeof(byte[])), info.GetInt32("th")));
            Key = IdSpan.UnsafeCreate((byte[]?)info.GetValue("kv", typeof(byte[])), info.GetInt32("kh"));
        }

        /// <summary>
        /// Gets the grain type.
        /// </summary>
        [Id(1)]
        public GrainType Type { get; }

        /// <summary>
        /// Gets the grain key.
        /// </summary>
        [Id(2)]
        public IdSpan Key { get; }

        /// <summary>
        /// Creates a new <see cref="GrainType"/> instance.
        /// </summary>
        public static GrainId Create(string type, string key) => Create(GrainType.Create(type), key);

        /// <summary>
        /// Creates a new <see cref="GrainType"/> instance.
        /// </summary>
        public static GrainId Create(GrainType type, string key) => new GrainId(type, IdSpan.Create(key));

        /// <summary>
        /// Creates a new <see cref="GrainType"/> instance.
        /// </summary>
        public static GrainId Create(GrainType type, IdSpan key) => new GrainId(type, key);

        /// <summary>
        /// Creates a new <see cref="GrainType"/> instance.
        /// </summary>
        public static GrainId Parse(string value)
        {
            if (!TryParse(value, out var result))
            {
                ThrowInvalidGrainId(value);

                static void ThrowInvalidGrainId(string value) => throw new ArgumentException($"Unable to parse \"{value}\" as a grain id");
            }

            return result;
        }

        /// <summary>
        /// Creates a new <see cref="GrainType"/> instance.
        /// </summary>
        public static bool TryParse(string? value, out GrainId grainId)
        {
            int i;
            if (value is null || (i = value.IndexOf('/')) < 0)
            {
                grainId = default;
                return false;
            }

            grainId = new(new GrainType(Encoding.UTF8.GetBytes(value, 0, i)), new IdSpan(Encoding.UTF8.GetBytes(value, i + 1, value.Length - i - 1)));
            return true;
        }

        /// <summary>
        /// <see langword="true"/> if this instance is the default value, <see langword="false"/> if it is not.
        /// </summary>
        public bool IsDefault => Type.IsDefault && Key.IsDefault;

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is GrainId id && Equals(id);

        /// <inheritdoc/>
        public bool Equals(GrainId other) => Type.Equals(other.Type) && Key.Equals(other.Key);

        /// <inheritdoc/>
        public override int GetHashCode() => HashCode.Combine(Type, Key);

        /// <summary>
        /// Generates a uniform, stable hash code for a grain id.
        /// </summary>
        public uint GetUniformHashCode()
        {
            // This value must be stable for a given id and equal for all nodes in a cluster.
            // HashCode.Combine does not currently offer stability with respect to its inputs.
            unchecked
            {
                var hash = 17u;
                hash = hash * 31 + Type.GetUniformHashCode();
                hash = hash * 31 + Key.GetUniformHashCode();
                return hash;
            }
        }

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

        /// <summary>
        /// Compares the provided operands for equality.
        /// </summary>
        /// <param name="left">The left operand.</param>
        /// <param name="right">The right operand.</param>
        /// <returns><see langword="true"/> if the provided values are equal, otherwise <see langword="false"/>.</returns>
        public static bool operator ==(GrainId left, GrainId right) => left.Equals(right);

        /// <summary>
        /// Compares the provided operands for inequality.
        /// </summary>
        /// <param name="left">The left operand.</param>
        /// <param name="right">The right operand.</param>
        /// <returns><see langword="true"/> if the provided values are not equal, otherwise <see langword="false"/>.</returns>
        public static bool operator !=(GrainId left, GrainId right) => !left.Equals(right);

        /// <inheritdoc/>
        public override string ToString() => $"{Type}/{Key}";

        string IFormattable.ToString(string? format, IFormatProvider? formatProvider) => ToString();

        bool ISpanFormattable.TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
            => destination.TryWrite($"{Type}/{Key}", out charsWritten);
    }

    /// <summary>
    /// Functionality for converting a <see cref="GrainId"/> to and from a JSON string.
    /// </summary>
    public sealed class GrainIdJsonConverter : JsonConverter<GrainId>
    {
        /// <inheritdoc />
        public override GrainId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => GrainId.Parse(reader.GetString()!);

        /// <inheritdoc />
        public override void Write(Utf8JsonWriter writer, GrainId value, JsonSerializerOptions options)
        {
            var type = value.Type.AsSpan();
            var key = value.Key.AsSpan();
            Span<byte> buf = stackalloc byte[type.Length + key.Length + 1];

            type.CopyTo(buf);
            buf[type.Length] = (byte)'/';
            key.CopyTo(buf[(type.Length + 1)..]);

            writer.WriteStringValue(buf);
        }
    }
}
