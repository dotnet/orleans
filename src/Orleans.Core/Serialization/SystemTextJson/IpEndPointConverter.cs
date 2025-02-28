using System;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

#nullable enable

namespace Orleans.Serialization
{
    public class IpEndPointConverter : JsonConverter<IPEndPoint>
    {
        public override IPEndPoint? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string? address = null;
            var port = 0;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    break;

                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    var propertyName = reader.GetString();
                    reader.Read();

                    if (propertyName == "Address")
                        address = reader.GetString();
                    else if (propertyName == "Port")
                        port = reader.GetInt32();
                }
            }

            return string.IsNullOrWhiteSpace(address) ? null : new IPEndPoint(IPAddress.Parse(address), port);
        }

        public override void Write(Utf8JsonWriter writer, IPEndPoint value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("Address", value.Address.ToString());
            writer.WriteNumber("Port", value.Port);
            writer.WriteEndObject();
        }
    }
}