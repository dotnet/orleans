using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Orleans.Runtime;

#nullable enable

namespace Orleans.Serialization
{
    public class UniqueKeyJsonConverter : JsonConverter<UniqueKey>
    {
        public override UniqueKey? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }

            string? input = default;

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
                        case "UniqueKey":
                            input = reader.GetString();
                            break;
                    }
                }
            }

            return UniqueKey.Parse(input);
        }

        public override void Write(Utf8JsonWriter writer, UniqueKey value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("UniqueKey", value.ToHexString());
            writer.WriteEndObject();
        }
    }
}