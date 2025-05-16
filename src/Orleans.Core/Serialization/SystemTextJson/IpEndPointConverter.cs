#nullable enable

using System;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orleans.Serialization
{
    internal sealed class IpEndPointConverter(JsonConverter<IPAddress> addressConverter) : JsonConverter<IPEndPoint>
    {
        /// <inheritdoc />
        public override IPEndPoint? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            IPAddress? address = null;
            var port = -1;

            Span<char> propertyName = stackalloc char[8];

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    break;

                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    var written = reader.CopyString(propertyName);

                    reader.Read();

                    if (propertyName[..written] is "Address")
                        address = addressConverter.Read(ref reader, typeof(IPAddress), options);
                    else if (propertyName[..written] is "Port")
                        _ = reader.TryGetInt32(out port); // Port is optional
                }
            }

            return address is null ? null : new IPEndPoint(address, port);
        }

        /// <inheritdoc />
        public override void Write(Utf8JsonWriter writer, IPEndPoint value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("Address");
            addressConverter.Write(writer, value.Address, options);
            writer.WriteNumber("Port", value.Port);
            writer.WriteEndObject();
        }

        /// <inheritdoc />
        public override IPEndPoint ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var valueLength = reader.HasValueSequence
            ? checked((int)reader.ValueSequence.Length)
            : reader.ValueSpan.Length;

            if (valueLength <= 65)
            {
                Span<char> buffer = stackalloc char[65];
                var written = reader.CopyString(buffer);
                return IPEndPoint.Parse(buffer[..written]);
            }
            else
            {
                var endpoint = reader.GetString();
                return IPEndPoint.Parse(endpoint!);
            }
        }

        /// <inheritdoc />
        public override void WriteAsPropertyName(Utf8JsonWriter writer, [DisallowNull] IPEndPoint value, JsonSerializerOptions options)
        {
            writer.WritePropertyName(value.ToString()); // TODO: PR for IPEndPoint to support ISpanFormattable/IUtf8SpanFormattable
        }
    }
}
