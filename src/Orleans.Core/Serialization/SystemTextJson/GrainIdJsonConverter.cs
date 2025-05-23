#nullable enable

using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Orleans.Runtime;

namespace Orleans.Serialization
{
    internal sealed class GrainIdJsonConverter : JsonConverter<GrainId>
    {
        /// <inheritdoc />
        public override GrainId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                return default;
            }

            string? type = default, key = default;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    break;

                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    var propertyName = reader.GetString();
                    reader.Read();

                    switch (propertyName)
                    {
                        case "Type":
                            type = reader.GetString();
                            break;
                        case "Key":
                            key = reader.GetString();
                            break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(key))
            {
                return default;
            }
            else
            {
                return GrainId.Create(type, key);
            }
        }

        /// <inheritdoc />
        public override void Write(Utf8JsonWriter writer, GrainId value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("Type", value.Type.AsSpan());
            writer.WriteString("Key", value.Key.AsSpan());
            writer.WriteEndObject();
        }

        /// <inheritdoc />
        public override GrainId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var valueLength = reader.HasValueSequence
                ? checked((int)reader.ValueSequence.Length)
                : reader.ValueSpan.Length;

            Span<char> buf = valueLength <= 128
                ? (stackalloc char[128])[..valueLength]
                : new char[valueLength];

            var written = reader.CopyString(buf);
            buf = buf[..written];

            return GrainId.Parse(buf);
        }

        /// <inheritdoc />
        public override void WriteAsPropertyName(Utf8JsonWriter writer, [DisallowNull] GrainId value, JsonSerializerOptions options)
        {
            var type = value.Type.AsSpan();
            var key = value.Key.AsSpan();
            Span<byte> buf = stackalloc byte[type.Length + key.Length + 1];

            type.CopyTo(buf);
            buf[type.Length] = (byte)'/';
            key.CopyTo(buf[(type.Length + 1)..]);

            writer.WritePropertyName(buf);
        }
    }
}
