using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Orleans.Runtime;

#nullable enable

namespace Orleans.Serialization
{
    public sealed class StreamIdJsonConverter : JsonConverter<StreamId>
    {
        public override StreamId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                return default;
            }

            // TODO: Look at supporting reading the format from Newtonsoft Json

            string? ns = default, key = default;

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
                        case "Namespace":
                            ns = reader.GetString();
                            break;
                        case "Key":
                            key = reader.GetString();
                            break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(key))
            {
                return default;
            }
            else
            {
                return StreamId.Create(ns!, key); // StreamId.Create does handle a null namespace parameter
            }
        }

        public override void Write(Utf8JsonWriter writer, StreamId value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            if (value != default)
            {            
                writer.WriteString("Namespace", value.GetNamespace());
                writer.WriteString("Key", value.GetKeyAsString());
            }
            writer.WriteEndObject();
        }
    }
}