#nullable enable

using System;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orleans.Serialization
{
    public sealed class IpEndPointConverter(JsonConverter<IPAddress> addressConverter) : JsonConverter<IPEndPoint>
    {
        private const int MaxAddressSize = 71;

        /// <inheritdoc />
        public override IPEndPoint? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            IPAddress? address = null;
            var port = -1;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    break;

                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    if (reader.ValueTextEquals("Address"u8))
                    {
                        reader.Read();
                        address = addressConverter.Read(ref reader, typeof(IPAddress), options);
                    }
                    else if (reader.ValueTextEquals("Port"u8))
                    {
                        reader.Read();
                        _ = reader.TryGetInt32(out port); // Port is optional
                    }
                    else
                    {
                        reader.Read();
                    }
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

            if (valueLength <= MaxAddressSize)
            {
                Span<char> buffer = stackalloc char[MaxAddressSize];
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
            writer.WritePropertyName(value.ToString());
        }
    }
}
