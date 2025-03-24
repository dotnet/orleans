#nullable enable

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Orleans.Runtime;

namespace Orleans.Serialization
{
    internal sealed class ActivationIdJsonConverter : JsonConverter<ActivationId>
    {
        public override ActivationId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString();
            return string.IsNullOrEmpty(value) ? default : ActivationId.FromParsableString(value);
        }

        public override void Write(Utf8JsonWriter writer, ActivationId value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToParsableString());
        }
    }
}