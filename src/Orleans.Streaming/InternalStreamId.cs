using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using Orleans.Providers.Streams.Generator;

#nullable enable
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

        public bool Equals(QualifiedStreamId other) => StreamId.Equals(other) && EqualityComparer<string>.Default.Equals(ProviderName,other.ProviderName);

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

        public override void WriteAsPropertyName(Utf8JsonWriter writer, [DisallowNull] QualifiedStreamId value, JsonSerializerOptions options)
        {
            Span<char> buffer = stackalloc char[128];

            if (value.ProviderName.TryCopyTo(buffer)
                && ((ISpanFormattable)value.StreamId).TryFormat(buffer, out var written, [], null))
            {
                buffer[value.ProviderName.Length] = ':';
                Span<byte> buffer2 = stackalloc byte[128];
                Utf8.FromUtf16(buffer[0..(value.ProviderName.Length + written)], buffer2, out _, out written);
                writer.WritePropertyName(buffer2[..written]);
            }
            else
            {
               writer.WritePropertyName($"{value.ProviderName}:{value.StreamId}");
            }
        }
    }
}
