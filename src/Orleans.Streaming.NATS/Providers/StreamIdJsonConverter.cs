using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using Orleans.Runtime;

namespace Orleans.Streaming.NATS;

internal class StreamIdJsonConverter : JsonConverter<StreamId>
{
    public override StreamId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException("StreamId namespace must be a base64 string");
            }

            var streamNamespace = reader.GetBytesFromBase64();
            if (streamNamespace.Length > ushort.MaxValue)
            {
                throw new JsonException("StreamId namespace exceeds the maximum length");
            }

            if (!reader.Read() || reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException("StreamId key must be a base64 string");
            }

            var key = reader.GetBytesFromBase64();
            if (key.Length == 0 || !reader.Read() || reader.TokenType != JsonTokenType.EndArray)
            {
                throw new JsonException("StreamId must contain a namespace and a nonempty key");
            }

            return StreamId.Create(streamNamespace, key);
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("StreamId must be a string or an array of base64 components");
        }

        var str = reader.GetString();

        if (string.IsNullOrWhiteSpace(str))
        {
            throw new JsonException("StreamId is empty");
        }

        return StreamId.Parse(Encoding.UTF8.GetBytes(str));
    }

    public override void Write(Utf8JsonWriter writer, StreamId value, JsonSerializerOptions options)
    {
        if (value.Key.IsEmpty)
        {
            throw new JsonException("StreamId key is empty");
        }

        // Preserve the legacy wire format for identities which round-trip through namespace/key text.
        if (!value.Namespace.IsEmpty
            && !value.Namespace.Span.Contains((byte)'/')
            && Utf8.IsValid(value.Namespace.Span)
            && Utf8.IsValid(value.Key.Span))
        {
            writer.WriteStringValue(value.ToString());
        }
        else
        {
            writer.WriteStartArray();
            writer.WriteBase64StringValue(value.Namespace.Span);
            writer.WriteBase64StringValue(value.Key.Span);
            writer.WriteEndArray();
        }
    }
}
