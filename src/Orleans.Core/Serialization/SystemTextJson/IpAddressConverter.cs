using System;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

#nullable enable

namespace Orleans.Serialization
{
    public class IpAddressConverter : JsonConverter<IPAddress>
    {
        public override void Write(Utf8JsonWriter writer, IPAddress value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString());
        }

        public override IPAddress? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var address = reader.GetString();
            if (string.IsNullOrWhiteSpace(address))
            {
                return null;
            }
            return IPAddress.Parse(address);
        }
    }
}