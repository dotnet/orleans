using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orleans.Messaging;

/// <summary>
/// An immutable, ordinal application identity formed from nonempty hierarchical segments.
/// </summary>
/// <remarks>
/// Create constructs literal segments and escapes slash and backslash characters exactly once.
/// Parse reads the canonical escaped path. The key wraps its canonical string; equality and hashing use that string. Applications preserve the identity across retries.
/// Every key is well-formed UTF-16: surrogate characters appear only as high-low pairs, so distinct keys keep distinct serialized forms.
/// </remarks>
[Immutable, Alias("Orleans.Messaging.HierarchicalKey")]
[JsonConverter(typeof(HierarchicalKeyJsonConverter))]
public readonly struct HierarchicalKey : ISpanFormattable, IEquatable<HierarchicalKey>, IParsable<HierarchicalKey>, ISpanParsable<HierarchicalKey>
{
    /// <summary>The escape character used within canonical segments.</summary>
    public const char EscapeCharacter = '\\';

    /// <summary>The separator between canonical segments.</summary>
    public const char SegmentSeparator = '/';

    private const char HighSurrogateStart = '\uD800';
    private const char LowSurrogateEnd = '\uDFFF';

    private readonly string? _value;

    private HierarchicalKey(string canonical) => _value = canonical;

    /// <summary>Serializes hierarchical identities as canonical escaped JSON strings.</summary>
    public sealed class HierarchicalKeyJsonConverter : JsonConverter<HierarchicalKey>
    {
        /// <summary>Creates a hierarchical identity JSON converter.</summary>
        public HierarchicalKeyJsonConverter() { }

        /// <inheritdoc/>
        public override HierarchicalKey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return default;
            if (reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName
                && TryParse(reader.GetString(), null, out var result)) return result;
            throw new JsonException("A hierarchical identity must be a canonical escaped path.");
        }

        /// <inheritdoc/>
        public override void Write(Utf8JsonWriter writer, HierarchicalKey value, JsonSerializerOptions options)
        {
            if (value.IsDefault) writer.WriteNullValue();
            else writer.WriteStringValue(value.ToString());
        }

        /// <inheritdoc/>
        public override HierarchicalKey ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Read(ref reader, typeToConvert, options);

        /// <inheritdoc/>
        public override void WriteAsPropertyName(Utf8JsonWriter writer, HierarchicalKey value, JsonSerializerOptions options)
        {
            if (value.IsDefault) throw new JsonException("A hierarchical dictionary key must be set.");
            writer.WritePropertyName(value.ToString());
        }
    }

    /// <summary>Gets whether this value is unset.</summary>
    public bool IsDefault => _value is null;

    /// <summary>Gets the canonical path length in UTF-16 characters.</summary>
    public int Length => _value?.Length ?? 0;

    /// <summary>Creates one literal segment, escaping slash and backslash characters.</summary>
    /// <param name="value">The nonempty literal segment.</param>
    /// <returns>The segment identity.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or contains an unpaired surrogate.</exception>
    public static HierarchicalKey Create(string value)
    {
        ValidateSegment(value);
        return new(Escape(value));
    }

    /// <summary>Creates a hierarchy from literal segments in root-first order.</summary>
    /// <param name="values">The nonempty literal segments.</param>
    /// <returns>A flat canonical identity which owns its immutable backing string.</returns>
    /// <exception cref="ArgumentNullException">A segment is null.</exception>
    /// <exception cref="ArgumentException">The input or a segment is empty, or a segment contains an unpaired surrogate.</exception>
    public static HierarchicalKey Create(params ReadOnlySpan<string> values)
    {
        if (values.IsEmpty)
        {
            throw new ArgumentException("Values must not be empty.", nameof(values));
        }
        var builder = new StringBuilder();
        foreach (var value in values)
        {
            ValidateSegment(value);
            if (builder.Length > 0) builder.Append(SegmentSeparator);
            AppendEscaped(builder, value);
        }
        return new(builder.ToString());
    }

    /// <summary>Appends one literal child segment.</summary>
    /// <param name="value">The nonempty literal child segment.</param>
    /// <returns>The child identity.</returns>
    /// <exception cref="InvalidOperationException">This key is unset.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or contains an unpaired surrogate.</exception>
    public HierarchicalKey CreateChildKey(string value)
    {
        EnsureSet();
        ValidateSegment(value);
        return new(string.Concat(_value, "/", Escape(value)));
    }

    /// <summary>Composes this hierarchy with an already constructed suffix hierarchy.</summary>
    /// <param name="suffix">The constructed suffix.</param>
    /// <returns>A flat concatenated identity preserving both paths' segment boundaries.</returns>
    /// <exception cref="InvalidOperationException">This key is unset.</exception>
    /// <exception cref="ArgumentException"><paramref name="suffix"/> is unset.</exception>
    public HierarchicalKey Append(HierarchicalKey suffix)
    {
        EnsureSet();
        if (suffix.IsDefault) throw new ArgumentException("The suffix must not be unset.", nameof(suffix));
        return new(string.Concat(_value, "/", suffix._value));
    }

    /// <summary>Gets the immediate parent, or null for a root or unset key.</summary>
    /// <returns>The parent identity when this key has more than one segment.</returns>
    public HierarchicalKey? GetParent()
    {
        if (_value is null) return null;
        var lastSeparator = LastSeparator(_value);
        return lastSeparator < 0 ? null : new HierarchicalKey(_value[..lastSeparator]);
    }

    /// <summary>Tests whether this key is the other's immediate child.</summary>
    /// <param name="other">The potential parent.</param>
    /// <returns>Whether there is exactly one additional segment.</returns>
    public bool IsChildOf(HierarchicalKey other) => other.IsParentOf(this);

    /// <summary>Tests whether this key is the other's immediate parent.</summary>
    /// <param name="other">The potential child.</param>
    /// <returns>Whether the other key extends this key by exactly one segment.</returns>
    public bool IsParentOf(HierarchicalKey other) => IsAncestorOf(other) && LastSeparator(other._value!) == Length;

    /// <summary>Tests whether this key is equal to or an ancestor of the other key.</summary>
    /// <param name="other">The identity to inspect.</param>
    /// <returns>Whether the full prefix consists of equal ordinal segments. Unset keys return false.</returns>
    public bool IsAncestorOf(HierarchicalKey other) => !IsDefault && !other.IsDefault
        && (Equals(other) || other.Length > Length && other._value![Length] == SegmentSeparator
            && other._value.StartsWith(_value!, StringComparison.Ordinal));

    /// <inheritdoc/>
    public static HierarchicalKey Parse(string s, IFormatProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        return TryParse(s, provider, out var result) ? result : throw new FormatException("The value is not a valid canonical hierarchical key.");
    }

    /// <inheritdoc/>
    public static HierarchicalKey Parse(ReadOnlySpan<char> s, IFormatProvider? provider = null) =>
        TryParse(s, provider, out var result) ? result : throw new FormatException("The value is not a valid canonical hierarchical key.");

    /// <inheritdoc/>
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out HierarchicalKey result)
    {
        if (s is not null && IsCanonical(s))
        {
            result = new(s);
            return true;
        }
        result = default;
        return false;
    }

    /// <inheritdoc/>
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out HierarchicalKey result)
    {
        if (IsCanonical(s))
        {
            result = new(new string(s));
            return true;
        }
        result = default;
        return false;
    }

    /// <inheritdoc/>
    public bool Equals(HierarchicalKey other) => string.Equals(_value, other._value, StringComparison.Ordinal);


    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is HierarchicalKey other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => _value?.GetHashCode() ?? 0;

    /// <summary>Compares complete ordinal key values.</summary>
    /// <param name="left">The first identity.</param>
    /// <param name="right">The second identity.</param>
    /// <returns>Whether the identities are equal.</returns>
    public static bool operator ==(HierarchicalKey left, HierarchicalKey right) => left.Equals(right);

    /// <summary>Compares complete ordinal key values for inequality.</summary>
    /// <param name="left">The first identity.</param>
    /// <param name="right">The second identity.</param>
    /// <returns>Whether the identities differ.</returns>
    public static bool operator !=(HierarchicalKey left, HierarchicalKey right) => !left.Equals(right);

    /// <inheritdoc/>
    public override string ToString() => _value ?? string.Empty;

    /// <inheritdoc/>
    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    /// <inheritdoc/>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        if (ToString().AsSpan().TryCopyTo(destination))
        {
            charsWritten = Length;
            return true;
        }
        charsWritten = 0;
        return false;
    }

    /// <summary>Enumerates escaped canonical segment spans in root-first order.</summary>
    /// <returns>An allocation-free segment enumerator.</returns>
    public SegmentEnumerator GetEnumerator() => new(ToString().AsSpan());

    private void EnsureSet()
    {
        if (IsDefault) throw new InvalidOperationException("The key must not be unset.");
    }

    private static void ValidateSegment(string value, [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(value, paramName);
        if (!IsWellFormedUtf16(value)) throw new ArgumentException("The value must not contain unpaired surrogate characters.", paramName);
    }

    // Orleans string serialization replaces unpaired surrogates, so accepting them would let distinct keys collide on the wire.
    internal static bool IsWellFormedUtf16(ReadOnlySpan<char> value)
    {
        var i = value.IndexOfAnyInRange(HighSurrogateStart, LowSurrogateEnd);
        if (i < 0) return true;
        for (; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                if (++i == value.Length || !char.IsLowSurrogate(value[i])) return false;
            }
            else if (char.IsLowSurrogate(value[i]))
            {
                return false;
            }
        }
        return true;
    }

    private static string Escape(string value)
    {
        if (value.AsSpan().IndexOfAny(EscapeCharacter, SegmentSeparator) < 0) return value;
        var builder = new StringBuilder(value.Length);
        AppendEscaped(builder, value);
        return builder.ToString();
    }

    private static void AppendEscaped(StringBuilder builder, string value)
    {
        foreach (var character in value)
        {
            if (character is EscapeCharacter or SegmentSeparator) builder.Append(EscapeCharacter);
            builder.Append(character);
        }
    }

    private static int LastSeparator(ReadOnlySpan<char> path)
    {
        var lastSeparator = -1;
        for (var i = 0; i < path.Length; i++)
        {
            if (path[i] == EscapeCharacter) i++;
            else if (path[i] == SegmentSeparator) lastSeparator = i;
        }
        return lastSeparator;
    }

    private static bool IsCanonical(ReadOnlySpan<char> path)
    {
        var segmentLength = 0;
        for (var i = 0; i < path.Length; i++)
        {
            if (path[i] == EscapeCharacter)
            {
                if (++i == path.Length || path[i] is not (EscapeCharacter or SegmentSeparator)) return false;
            }
            else if (path[i] == SegmentSeparator)
            {
                if (segmentLength == 0) return false;
                segmentLength = 0;
                continue;
            }
            else if (char.IsSurrogate(path[i]))
            {
                if (!char.IsHighSurrogate(path[i]) || ++i == path.Length || !char.IsLowSurrogate(path[i])) return false;
            }
            segmentLength++;
        }
        return segmentLength != 0;
    }

    /// <summary>Enumerates borrowed spans of the immutable canonical key.</summary>
    public ref struct SegmentEnumerator
    {
        private readonly ReadOnlySpan<char> _path;
        private int _next;

        internal SegmentEnumerator(ReadOnlySpan<char> path) => _path = path;

        /// <summary>Gets the current escaped canonical segment.</summary>
        public ReadOnlySpan<char> Current { get; private set; }

        /// <summary>Advances to the next segment.</summary>
        /// <returns>Whether a segment is available.</returns>
        public bool MoveNext()
        {
            if (_next >= _path.Length)
            {
                Current = default;
                return false;
            }
            var start = _next;
            for (; _next < _path.Length; _next++)
            {
                if (_path[_next] == EscapeCharacter) _next++;
                else if (_path[_next] == SegmentSeparator) break;
            }
            Current = _path[start.._next];
            _next++;
            return true;
        }
    }
}
