using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orleans.Runtime
{
    /// <summary>
    /// A unique identifier based on a <see cref="Guid"/>.
    /// </summary>
    [Serializable]
    [Immutable]
    [GenerateSerializer]
    [JsonConverter(typeof(GuidIdConverter))]
    public sealed class GuidId : IEquatable<GuidId>, IComparable<GuidId>, ISerializable
    {
        private static readonly Interner<Guid, GuidId> guidIdInternCache = new Interner<Guid, GuidId>(InternerConstants.SIZE_LARGE);

        /// <summary>
        /// The underlying <see cref="Guid"/>.
        /// </summary>
        [Id(0)]
        public readonly Guid Guid;

        /// <summary>
        /// Initializes a new instance of the <see cref="GuidId"/> class.
        /// </summary>
        /// <param name="guid">
        /// The underlying <see cref="Guid"/>.
        /// </param>
        private GuidId(Guid guid)
        {
            this.Guid = guid;
        }

        /// <summary>
        /// Returns a new, randomly generated <see cref="GuidId"/>.
        /// </summary>
        /// <returns>A new, randomly generated <see cref="GuidId"/>.</returns>
        public static GuidId GetNewGuidId()
        {
            return FindOrCreateGuidId(Guid.NewGuid());
        }

        /// <summary>
        /// Returns a <see cref="GuidId"/> instance corresponding to the provided <see cref="Guid"/>.
        /// </summary>
        /// <param name="guid">The guid.</param>
        /// <returns>A <see cref="GuidId"/> instance corresponding to the provided <see cref="Guid"/>.</returns>
        public static GuidId GetGuidId(Guid guid)
        {
            return FindOrCreateGuidId(guid);
        }

        /// <summary>
        /// Returns a <see cref="GuidId"/> instance corresponding to the provided <see cref="Guid"/>.
        /// </summary>
        /// <param name="guid">The <see cref="Guid"/>.</param>
        /// <returns>A <see cref="GuidId"/> instance corresponding to the provided <see cref="Guid"/>.</returns>
        private static GuidId FindOrCreateGuidId(Guid guid)
        {
            return guidIdInternCache.FindOrCreate(guid, g => new GuidId(g));
        }

        /// <inheritdoc />
        public int CompareTo(GuidId? other) => other is null ? 1 : Guid.CompareTo(other.Guid);

        /// <inheritdoc />
        public bool Equals(GuidId? other) => other is not null && Guid.Equals(other.Guid);

        /// <inheritdoc />
        public override bool Equals(object? obj) => Equals(obj as GuidId);

        /// <inheritdoc />
        public override int GetHashCode() => Guid.GetHashCode();

        /// <inheritdoc />
        public override string ToString() => Guid.ToString();

        /// <summary>
        /// Compares the provided operands for equality.
        /// </summary>
        /// <param name="left">The left operand.</param>
        /// <param name="right">The right operand.</param>
        /// <returns><see langword="true"/> if the provided values are equal, otherwise <see langword="false"/>.</returns>
        public static bool operator ==(GuidId? left, GuidId? right) => ReferenceEquals(left, right) || (left?.Equals(right) ?? false);

        /// <summary>
        /// Compares the provided operands for inequality.
        /// </summary>
        /// <param name="left">The left operand.</param>
        /// <param name="right">The right operand.</param>
        /// <returns><see langword="true"/> if the provided values are not equal, otherwise <see langword="false"/>.</returns>
        public static bool operator !=(GuidId? left, GuidId? right) => !(left == right);

        /// <inheritdoc />
        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("Guid", Guid, typeof(Guid));
        }

        private GuidId(SerializationInfo info, StreamingContext context)
        {
            // ! This is an older pattern which is not compatible with nullable reference types.
            Guid = (Guid)info.GetValue("Guid", typeof(Guid))!;
        }
    }

    public sealed class GuidIdConverter : JsonConverter<GuidId>
    {
        public override GuidId? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => ReadCore(ref reader);

        public override void Write(Utf8JsonWriter writer, GuidId value, JsonSerializerOptions options)
        {
            Span<byte> buffer = stackalloc byte[32];
            if (!value.Guid.TryFormat(buffer, out var written, "N"))
            {
                throw new JsonException($"Could not serialize {nameof(GuidId)}.");
            }

            writer.WriteStringValue(buffer[..written]);
        }

        public override GuidId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => ReadCore(ref reader);

        private static GuidId ReadCore(ref Utf8JsonReader reader)
        {
            if (reader.TokenType is not JsonTokenType.String and not JsonTokenType.PropertyName)
            {
                throw new JsonException($"Could not deserialize {nameof(GuidId)}.");
            }

            Span<char> buffer = !reader.HasValueSequence || reader.ValueSequence.Length <= 32
                                ? stackalloc char[32]
                                : new char[checked((int)reader.ValueSequence.Length)];

            var read = reader.CopyString(buffer);
            if (!Guid.TryParseExact(buffer[..read], "N", out var value))
            {
                throw new JsonException($"Could not deserialize {nameof(GuidId)}.");
            }

            return GuidId.GetGuidId(value);
        }

        public override void WriteAsPropertyName(Utf8JsonWriter writer, [DisallowNull] GuidId value, JsonSerializerOptions options)
        {
            Span<byte> buffer = stackalloc byte[32];
            if (value.Guid.TryFormat(buffer, out var written, "N"))
            {
                writer.WritePropertyName(buffer[..written]);
            }
            else
            {
                throw new JsonException($"Could not serialize {nameof(GuidId)}.");
            }
        }
    }
}
