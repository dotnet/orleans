using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Orleans.Runtime;

#nullable enable

namespace Orleans.Serialization
{
    public sealed class StreamIdJsonConverter : JsonConverter<StreamId>
    {
        // This is backward compatible with Newtonsoft.JsonSerializer
        // which didn't have a JsonConverter for StreamId.
        // StreamId used the default serialization that Newtonsoft provided.

        // Ideally this STJ Converter would write Key and Namespace property in a similar way to
        // GrainIdConverter.

        // This implementation emulates Newtonsoft by both reading and writing
        // the same structure.
        //
        // The alternatives were to
        // 1. break backward compatibility which would have prevented switching from Newtonsoft to STJ if streamIds were stored in persistence.
        // 2. To support reading the Newtonsoft format and the new format, but write using the preferred Key and Namespace format, which would allow a one-way migration, but prevent reverting to Newtonsoft.
        // 3. Add a Newtonsoft.JsonConverter for StreamId which supported the previous default Newtonsoft structure and also the preferred STJ Key and Namespace approach. This would make reverting Orleans a breaking change.

        readonly string? _byteArrayType = typeof(byte[]).AssemblyQualifiedName;
        readonly string? _streamIdType = typeof(StreamId).AssemblyQualifiedName;

        public override StreamId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                return default;
            }

            // This is backward compatible with the way Newtonsoft writes StreamId

            uint? ki = null;
            byte[]? value = null;
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
                        case "ki":
                            ki = reader.GetUInt32();
                            break;
                        case "fk":
                            while (reader.Read())
                            {
                                if (reader.TokenType == JsonTokenType.EndObject)
                                    break;

                                if (reader.TokenType == JsonTokenType.PropertyName)
                                {
                                    propertyName = reader.GetString();
                                    reader.Read();

                                    if (propertyName == "$value")
                                    {
                                        value = reader.GetBytesFromBase64();
                                    }
                                }
                            }
                            break;                        
                    }
                }
            }

            return value is not { Length : >0 }
                || !ki.HasValue
                ? default
                : new StreamId(value, (ushort)ki);
        }

        public override void Write(Utf8JsonWriter writer, StreamId value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            if (value != default)
            {
                writer.WriteString("$type", _streamIdType);
                writer.WriteStartObject("fk");
                    writer.WriteString("$type", _byteArrayType);
                    writer.WriteBase64String("$value", value.FullKey.Span);
                writer.WriteEndObject();
                writer.WriteNumber("ki", value.GetKeyIndex());
                writer.WriteNumber("fh", (int)value.GetUniformHashCode());
            }
            writer.WriteEndObject();
        }
    }
}