using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orleans.Messaging;

/// <summary>
/// Identifies one message by an application-supplied <see cref="HierarchicalKey"/>.
/// </summary>
/// <remarks>
/// The receiver deduplicates messages by this identity, so applications reuse it when retrying a send.
/// Equality, hashing, parsing, and formatting use the key's canonical escaped path.
/// </remarks>
[Immutable, Alias("Orleans.Messaging.MessageId")]
[JsonConverter(typeof(MessageIdJsonConverter))]
public readonly struct MessageId : ISpanFormattable, IEquatable<MessageId>, IParsable<MessageId>, ISpanParsable<MessageId>
{
    /// <summary>Creates a message identity from a hierarchical key.</summary>
    /// <param name="key">The application identity of the message.</param>
    public MessageId(HierarchicalKey key) => Key = key;

    /// <summary>Gets the hierarchical key which identifies the message.</summary>
    public HierarchicalKey Key { get; }

    /// <summary>Gets whether this value is unset.</summary>
    public bool IsDefault => Key.IsDefault;

    /// <summary>Creates a message identity from literal segments in root-first order.</summary>
    /// <param name="segments">The nonempty literal segments.</param>
    /// <returns>The message identity.</returns>
    /// <exception cref="ArgumentNullException">A segment is null.</exception>
    /// <exception cref="ArgumentException">The input or a segment is empty, or a segment contains an unpaired surrogate.</exception>
    public static MessageId Create(params ReadOnlySpan<string> segments) => new(HierarchicalKey.Create(segments));

    /// <inheritdoc/>
    public static MessageId Parse(string s, IFormatProvider? provider = null) => new(HierarchicalKey.Parse(s, provider));

    /// <inheritdoc/>
    public static MessageId Parse(ReadOnlySpan<char> s, IFormatProvider? provider = null) => new(HierarchicalKey.Parse(s, provider));

    /// <inheritdoc/>
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out MessageId result)
    {
        var parsed = HierarchicalKey.TryParse(s, provider, out var key);
        result = new(key);
        return parsed;
    }

    /// <inheritdoc/>
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out MessageId result)
    {
        var parsed = HierarchicalKey.TryParse(s, provider, out var key);
        result = new(key);
        return parsed;
    }

    /// <inheritdoc/>
    public bool Equals(MessageId other) => Key.Equals(other.Key);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is MessageId other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Key.GetHashCode();

    /// <summary>Compares message identities.</summary>
    /// <param name="left">The first identity.</param>
    /// <param name="right">The second identity.</param>
    /// <returns>Whether the identities are equal.</returns>
    public static bool operator ==(MessageId left, MessageId right) => left.Equals(right);

    /// <summary>Compares message identities for inequality.</summary>
    /// <param name="left">The first identity.</param>
    /// <param name="right">The second identity.</param>
    /// <returns>Whether the identities differ.</returns>
    public static bool operator !=(MessageId left, MessageId right) => !left.Equals(right);

    /// <inheritdoc/>
    public override string ToString() => Key.ToString();

    /// <inheritdoc/>
    public string ToString(string? format, IFormatProvider? formatProvider) => Key.ToString();

    /// <inheritdoc/>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        Key.TryFormat(destination, out charsWritten, format, provider);

    /// <summary>Serializes message identities as canonical escaped JSON strings.</summary>
    public sealed class MessageIdJsonConverter : JsonConverter<MessageId>
    {
        /// <summary>Creates a message identity JSON converter.</summary>
        public MessageIdJsonConverter() { }

        /// <inheritdoc/>
        public override MessageId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return default;
            if (reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName
                && TryParse(reader.GetString(), null, out var result)) return result;
            throw new JsonException("A message identity must be a canonical escaped path.");
        }

        /// <inheritdoc/>
        public override void Write(Utf8JsonWriter writer, MessageId value, JsonSerializerOptions options)
        {
            if (value.IsDefault) writer.WriteNullValue();
            else writer.WriteStringValue(value.ToString());
        }

        /// <inheritdoc/>
        public override MessageId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Read(ref reader, typeToConvert, options);

        /// <inheritdoc/>
        public override void WriteAsPropertyName(Utf8JsonWriter writer, MessageId value, JsonSerializerOptions options)
        {
            if (value.IsDefault) throw new JsonException("A message identity dictionary key must be set.");
            writer.WritePropertyName(value.ToString());
        }
    }
}
