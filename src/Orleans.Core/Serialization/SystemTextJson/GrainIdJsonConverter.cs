#nullable enable

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Orleans.Runtime;

namespace Orleans.Serialization
{
    internal sealed class GrainIdJsonConverter : JsonConverter<GrainId>
    {
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

        public override void Write(Utf8JsonWriter writer, GrainId value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("Type", value.Type.ToString());
            writer.WriteString("Key", value.Key.ToString());
            writer.WriteEndObject();
        }
    }
}