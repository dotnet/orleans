#nullable enable

using System;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orleans.Serialization
{
    public sealed class IPEndPointJsonConverter : JsonConverter<IPEndPoint>
    {
        private const int MaxAddressSize = 71;

        /// <inheritdoc />
        public override IPEndPoint? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => Parse(ref reader);

        /// <inheritdoc />
        public override void Write(Utf8JsonWriter writer, IPEndPoint value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString());

        /// <inheritdoc />
        public override IPEndPoint ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => Parse(ref reader);

        private static IPEndPoint Parse(ref Utf8JsonReader reader)
        {
            if (reader.TokenType is not JsonTokenType.String and not JsonTokenType.PropertyName)
            {
                throw new JsonException($"Could not deserialize {nameof(IPEndPoint)}.");
            }

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
                return IPEndPoint.Parse(reader.GetString() ?? throw new JsonException($"Could not deserialize {nameof(IPEndPoint)}."));
            }
        }

        /// <inheritdoc />
        public override void WriteAsPropertyName(Utf8JsonWriter writer, [DisallowNull] IPEndPoint value, JsonSerializerOptions options)
        {
            writer.WritePropertyName(value.ToString());
        }
    }
}
