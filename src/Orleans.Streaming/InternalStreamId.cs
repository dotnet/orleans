using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orleans.Runtime
{
    /// <summary>
    /// Identifies a stream within a named stream provider.
    /// </summary>
    [Immutable]
    [Serializable]
    [GenerateSerializer]
    [JsonConverter(typeof(QualifiedStreamIdJsonConverter))]
    public readonly struct QualifiedStreamId : IEquatable<QualifiedStreamId>, IComparable<QualifiedStreamId>, ISerializable, ISpanFormattable
    {
        /// <summary>
        /// The stream identifier.
        /// </summary>
        [Id(0)]
        public readonly StreamId StreamId;

        /// <summary>
        /// The name of the stream provider.
        /// </summary>
        [Id(1)]
        public readonly string ProviderName;

        /// <summary>
        /// Initializes a new instance of the <see cref="QualifiedStreamId"/> struct.
        /// </summary>
        /// <param name="providerName">The name of the stream provider.</param>
        /// <param name="streamId">The stream identifier.</param>
        public QualifiedStreamId(string providerName, StreamId streamId)
        {
            ProviderName = providerName;
            StreamId = streamId;
        }

        private QualifiedStreamId(SerializationInfo info, StreamingContext context)
        {
            ProviderName = info.GetString("pvn")!;
            StreamId = (StreamId)info.GetValue("sid", typeof(StreamId))!;
        }

        /// <summary>
        /// Returns the stream identifier contained in a qualified stream identifier.
        /// </summary>
        /// <param name="internalStreamId">The qualified stream identifier.</param>
        /// <returns>The stream identifier.</returns>
        public static implicit operator StreamId(QualifiedStreamId internalStreamId) => internalStreamId.StreamId;

        /// <inheritdoc/>
        public bool Equals(QualifiedStreamId other) => StreamId.Equals(other.StreamId) && string.Equals(ProviderName, other.ProviderName, StringComparison.Ordinal);

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is QualifiedStreamId other ? this.Equals(other) : false;

        /// <summary>
        /// Compares two qualified stream identifiers for equality.
        /// </summary>
        /// <param name="s1">The first qualified stream identifier.</param>
        /// <param name="s2">The second qualified stream identifier.</param>
        /// <returns><see langword="true"/> if both identifiers are equal; otherwise, <see langword="false"/>.</returns>
        public static bool operator ==(QualifiedStreamId s1, QualifiedStreamId s2) => s1.Equals(s2);

        /// <summary>
        /// Compares two qualified stream identifiers for inequality.
        /// </summary>
        /// <param name="s1">The first qualified stream identifier.</param>
        /// <param name="s2">The second qualified stream identifier.</param>
        /// <returns><see langword="true"/> if the identifiers differ; otherwise, <see langword="false"/>.</returns>
        public static bool operator !=(QualifiedStreamId s1, QualifiedStreamId s2) => !s2.Equals(s1);

        /// <inheritdoc/>
        public int CompareTo(QualifiedStreamId other)
        {
            var streamComparison = StreamId.CompareTo(other.StreamId);
            return streamComparison != 0
                ? streamComparison
                : string.Compare(ProviderName, other.ProviderName, StringComparison.Ordinal);
        }

        /// <inheritdoc/>
        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("pvn", ProviderName);
            info.AddValue("sid", StreamId, typeof(StreamId));
        }

        /// <inheritdoc/>
        public override int GetHashCode() => HashCode.Combine(ProviderName, StreamId);

        /// <inheritdoc/>
        public override string ToString() => $"{ProviderName}/{StreamId}";
        string IFormattable.ToString(string? format, IFormatProvider? formatProvider) => ToString();

        bool ISpanFormattable.TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
            => destination.TryWrite($"{ProviderName}/{StreamId}", out charsWritten);

        internal string? GetNamespace() => StreamId.GetNamespace();
    }

    /// <summary>
    /// Functionality for converting <see cref="QualifiedStreamId"/> instances to and from their JSON representation.
    /// </summary>
    public sealed class QualifiedStreamIdJsonConverter : JsonConverter<QualifiedStreamId>
    {
        /// <inheritdoc/>
        public override QualifiedStreamId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray
                || !reader.Read()
                || reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException($"Could not deserialize {nameof(QualifiedStreamId)}.");
            }

            var providerName = reader.GetString();
            if (!reader.Read())
            {
                throw new JsonException($"Could not deserialize {nameof(QualifiedStreamId)}.");
            }

            var streamId = JsonSerializer.Deserialize<StreamId>(ref reader, options);
            if (!reader.Read()
                || reader.TokenType != JsonTokenType.EndArray
                || string.IsNullOrWhiteSpace(providerName)
                || streamId == default)
            {
                throw new JsonException($"Could not deserialize {nameof(QualifiedStreamId)}.");
            }

            return new QualifiedStreamId(providerName, streamId);
        }

        /// <inheritdoc/>
        public override void Write(Utf8JsonWriter writer, QualifiedStreamId value, JsonSerializerOptions options)
        {
            if (string.IsNullOrWhiteSpace(value.ProviderName) || value.StreamId == default)
            {
                throw new JsonException($"Could not serialize {nameof(QualifiedStreamId)}.");
            }

            writer.WriteStartArray();
            writer.WriteStringValue(value.ProviderName);
            JsonSerializer.Serialize(writer, value.StreamId, options);
            writer.WriteEndArray();
        }

        /// <inheritdoc/>
        public override QualifiedStreamId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString() ?? throw new JsonException("Failed to parse QualifiedStreamId from property name.");
            var encoded = value.AsSpan();
            var separator = encoded.IndexOf(':');
            if (separator <= 0
                || !int.TryParse(
                    encoded[..separator],
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var providerNameLength))
            {
                throw new JsonException("Failed to parse QualifiedStreamId from property name.");
            }

            var components = encoded[(separator + 1)..];
            if (providerNameLength <= 0 || providerNameLength > components.Length)
            {
                throw new JsonException("Failed to parse QualifiedStreamId from property name.");
            }

            var providerName = components[..providerNameLength].ToString();
            if (string.IsNullOrWhiteSpace(providerName))
            {
                throw new JsonException("Failed to parse QualifiedStreamId from property name.");
            }

            var streamId = StreamIdJsonConverter.ParsePropertyName(components[providerNameLength..].ToString());
            return new QualifiedStreamId(providerName, streamId);
        }

        /// <inheritdoc/>
        public override void WriteAsPropertyName(Utf8JsonWriter writer, [DisallowNull] QualifiedStreamId value, JsonSerializerOptions options)
        {
            if (string.IsNullOrWhiteSpace(value.ProviderName) || value.StreamId == default)
            {
                throw new JsonException($"Could not serialize {nameof(QualifiedStreamId)}.");
            }

            writer.WritePropertyName(string.Concat(
                value.ProviderName.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ":",
                value.ProviderName,
                StreamIdJsonConverter.FormatPropertyName(value.StreamId)));
        }
    }
}
