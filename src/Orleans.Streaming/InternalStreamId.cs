using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Serialization;
using System.Text;
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
        private const int MaxBufferSize = 128;

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
            scoped ReadOnlySpan<byte> value;

            if (reader.HasValueSequence)
            {
                var buffer = reader.ValueSequence.Length <= MaxBufferSize ?
                             stackalloc byte[(int)reader.ValueSequence.Length] :
                             new byte[reader.ValueSequence.Length];

                reader.ValueSequence.CopyTo(buffer);
                value = buffer;
            }
            else
            {
                value = reader.ValueSpan;
            }

            var i = value.IndexOf((byte)':');

            ArgumentOutOfRangeException.ThrowIfLessThan(i, 0);

            var providerName = Encoding.UTF8.GetString(value[0..i]);
            var streamId = StreamId.Parse(value[(i + 1)..]);
            return new QualifiedStreamId(providerName, streamId);
        }

        public override void WriteAsPropertyName(Utf8JsonWriter writer, [DisallowNull] QualifiedStreamId value, JsonSerializerOptions options)
        {
            Span<byte> buffer = stackalloc byte[MaxBufferSize];

            if (Encoding.UTF8.TryGetBytes(value.ProviderName, buffer, out var bytesWritten)
                && ((IUtf8SpanFormattable)value.StreamId).TryFormat(buffer[(bytesWritten + 1)..], out var moreBytesWritten, [], null))
            {
                buffer[bytesWritten] = (byte)':';
                writer.WritePropertyName(buffer[..(bytesWritten + 1 + moreBytesWritten)]);
            }
            else
            {
                writer.WritePropertyName($"{value.ProviderName}:{value.StreamId}");
            }
        }
    }
}
