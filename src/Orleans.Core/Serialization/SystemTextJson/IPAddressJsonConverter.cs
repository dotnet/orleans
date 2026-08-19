#nullable enable

using System;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orleans.Serialization
{
    public sealed class IPAddressJsonConverter : JsonConverter<IPAddress>
    {
        private const int MaxAddressSize = 65; // Maximum bytes or chars stackallocated, taken from IPAddressParser.MaxIPv6StringLength

        public override void Write(Utf8JsonWriter writer, IPAddress value, JsonSerializerOptions options)
            => WriteCore(writer, value, options, false);

        private void WriteCore(Utf8JsonWriter writer, IPAddress value, JsonSerializerOptions options, bool writeAsPropertyName)
        {
            Span<byte> buf = stackalloc byte[MaxAddressSize];
            if (value.TryFormat(buf, out var bytesWritten))
            {
                if (writeAsPropertyName)
                {
                    writer.WritePropertyName(buf[..bytesWritten]);
                }
                else
                {
                    writer.WriteStringValue(buf[..bytesWritten]);
                }
            }
            else
            {
                if (writeAsPropertyName)
                {
                    writer.WritePropertyName(value.ToString());
                }
                else
                {
                    writer.WriteStringValue(value.ToString());
                }
            }
        }

        public override IPAddress? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => Parse(ref reader);

        private static IPAddress Parse(ref Utf8JsonReader reader)
        {
            var valueLength = reader.HasValueSequence
                ? checked((int)reader.ValueSequence.Length)
                : reader.ValueSpan.Length;

            if (valueLength <= MaxAddressSize)
            {
                Span<char> chars = stackalloc char[MaxAddressSize];
                var written = reader.CopyString(chars);
                return IPAddress.Parse(chars[..written]);
            }
            else
            {
                return IPAddress.Parse(reader.GetString() ?? throw new JsonException($"Could not deserialize {nameof(IPAddress)}."));
            }
        }

        public override IPAddress ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => Parse(ref reader);

        public override void WriteAsPropertyName(Utf8JsonWriter writer, [DisallowNull] IPAddress value, JsonSerializerOptions options)
            => WriteCore(writer, value, options, true);
    }
}
