using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Orleans.Runtime;

#nullable enable

namespace Orleans.Serialization
{
    public class SiloAddressConverter : JsonConverter<SiloAddress>
    {
        public override SiloAddress? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var siloAddress = reader.GetString();
            return string.IsNullOrWhiteSpace(siloAddress) ? null : SiloAddress.FromParsableString(siloAddress);
        }

        public override void Write(Utf8JsonWriter writer, SiloAddress value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToParsableString());
        }
    }
}