using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Serialization;
using System.Text;
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
        private readonly string? _qualifiedStreamIdType = typeof(QualifiedStreamId).AssemblyQualifiedName;
        // The versioned form preserves provider and stream delimiters. Legacy property names always contain '/'.
        private const string PropertyNamePrefix = "$qualifiedstreamid:v1:";

        public override QualifiedStreamId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                return default;
            }

            string? providerName = null;
            StreamId streamId = default;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    break;
                }

                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    var propertyName = reader.GetString();

                    reader.Read();

                    switch (propertyName)
                    {
                        case "pvn":
                            providerName = reader.GetString();
                            break;
                        case "sid":
                            streamId = JsonSerializer.Deserialize<StreamId>(ref reader, options);
                            break;
                    }
                }
            }

            if (providerName is null || streamId == default)
            {
                return default;
            }

            return new QualifiedStreamId(providerName, streamId);
        }

        public override void Write(Utf8JsonWriter writer, QualifiedStreamId value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("$type", _qualifiedStreamIdType);
            writer.WriteString("pvn", value.ProviderName);
            writer.WritePropertyName("sid");
            JsonSerializer.Serialize(writer, value.StreamId, options);
            writer.WriteEndObject();
        }

        public override QualifiedStreamId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString() ?? throw new JsonException("Failed to parse QualifiedStreamId from property name.");
            if (value.StartsWith(PropertyNamePrefix, StringComparison.Ordinal)
                && !value.AsSpan(PropertyNamePrefix.Length).Contains('/'))
            {
                var encoded = value.AsSpan(PropertyNamePrefix.Length);
                var separator = encoded.IndexOf(':');
                if (separator < 0)
                {
                    throw new JsonException("Failed to parse QualifiedStreamId from property name.");
                }

                string encodedProviderName;
                try
                {
                    encodedProviderName = Encoding.UTF8.GetString(Convert.FromHexString(encoded[..separator]));
                }
                catch (FormatException exception)
                {
                    throw new JsonException("Failed to parse QualifiedStreamId from property name.", exception);
                }

                var encodedStreamId = StreamIdJsonConverter.ParsePropertyName(encoded[(separator + 1)..].ToString());
                return new QualifiedStreamId(encodedProviderName, encodedStreamId);
            }

            var i = value.IndexOf(':');

            if (i < 0)
            {
                throw new JsonException("Failed to parse QualifiedStreamId from property name.");
            }

            var providerName = value[..i];
            var streamId = StreamId.Parse(Encoding.UTF8.GetBytes(value[(i + 1)..]));
            return new QualifiedStreamId(providerName, streamId);
        }

        public override void WriteAsPropertyName(Utf8JsonWriter writer, [DisallowNull] QualifiedStreamId value, JsonSerializerOptions options)
            => writer.WritePropertyName(string.Concat(
                PropertyNamePrefix,
                Convert.ToHexString(Encoding.UTF8.GetBytes(value.ProviderName)),
                ":",
                StreamIdJsonConverter.FormatPropertyName(value.StreamId)));
    }
}
