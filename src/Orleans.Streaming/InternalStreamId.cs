using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orleans.Runtime
{
    [Immutable]
    [Serializable]
    [GenerateSerializer]
    [JsonConverter(typeof(QualifiedStreamIdJsonConverter))]
    public readonly struct QualifiedStreamId : IEquatable<QualifiedStreamId>, IComparable<QualifiedStreamId>, ISerializable, ISpanFormattable
    {
        [Id(0)]
        public readonly StreamId StreamId;

        [Id(1)]
        public readonly string ProviderName;

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

        public static implicit operator StreamId(QualifiedStreamId internalStreamId) => internalStreamId.StreamId;

        public bool Equals(QualifiedStreamId other) => StreamId.Equals(other.StreamId) && string.Equals(ProviderName, other.ProviderName, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is QualifiedStreamId other ? this.Equals(other) : false;

        public static bool operator ==(QualifiedStreamId s1, QualifiedStreamId s2) => s1.Equals(s2);

        public static bool operator !=(QualifiedStreamId s1, QualifiedStreamId s2) => !s2.Equals(s1);

        public int CompareTo(QualifiedStreamId other) => StreamId.CompareTo(other.StreamId);

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("pvn", ProviderName);
            info.AddValue("sid", StreamId, typeof(StreamId));
        }

        public override int GetHashCode() => HashCode.Combine(ProviderName, StreamId);

        public override string ToString() => $"{ProviderName}/{StreamId}";
        string IFormattable.ToString(string? format, IFormatProvider? formatProvider) => ToString();

        bool ISpanFormattable.TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
            => destination.TryWrite($"{ProviderName}/{StreamId}", out charsWritten);

        internal string? GetNamespace() => StreamId.GetNamespace();
    }

    public sealed class QualifiedStreamIdJsonConverter : JsonConverter<QualifiedStreamId>
    {
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
