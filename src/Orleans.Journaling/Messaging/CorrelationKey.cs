using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Orleans.Journaling.Messaging;

/// <summary>
/// Represents a hierarchical correlation key for distributed message tracing.
/// </summary>
/// <remarks>
/// <para>
/// CorrelationKey uses '/' as the segment separator and '\\' as the escape character,
/// allowing for hierarchical correlation across distributed operations.
/// </para>
/// <para>
/// Example usage:
/// <code>
/// var transferKey = CorrelationKey.Create("transfer-123");
/// var debitKey = transferKey.CreateChildKey("debit");
/// var creditKey = transferKey.CreateChildKey("credit");
/// 
/// // Results in keys: "transfer-123/debit" and "transfer-123/credit"
/// </code>
/// </para>
/// </remarks>
[GenerateSerializer, Immutable]
public sealed class CorrelationKey : ISpanFormattable, IEquatable<CorrelationKey>, IParsable<CorrelationKey>, ISpanParsable<CorrelationKey>
{
    /// <summary>
    /// The character used to escape special characters in segment values.
    /// </summary>
    public const char EscapeCharacter = '\\';

    /// <summary>
    /// The character used to separate segments in the hierarchical path.
    /// </summary>
    public const char SegmentSeparator = '/';

    private static ReadOnlySpan<char> SegmentSeparatorSpan => "/";

    [Id(0)]
    private readonly CorrelationKey? _parent;

    [Id(1)]
    private readonly ReadOnlyMemory<char> _value;

    private CorrelationKey(ReadOnlyMemory<char> value)
    {
        _value = value;
    }

    private CorrelationKey(CorrelationKey? parent, ReadOnlyMemory<char> value) : this(value)
    {
        _parent = parent;
    }

    /// <summary>
    /// Creates a new <see cref="CorrelationKey"/> from the specified string value.
    /// </summary>
    /// <param name="value">The string value representing the correlation key.</param>
    /// <returns>A new <see cref="CorrelationKey"/> instance.</returns>
    /// <exception cref="ArgumentException">Thrown when the value is null, empty, or contains invalid segment separators.</exception>
    public static CorrelationKey Create(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        if (!IsSegmentationValid(value))
        {
            throw new ArgumentException("Value must not contain empty segments.", nameof(value));
        }

        return new CorrelationKey(value.AsMemory());
    }

    /// <summary>
    /// Creates a new <see cref="CorrelationKey"/> with the specified parent and value.
    /// </summary>
    /// <param name="parent">The parent correlation key, or null for a root key.</param>
    /// <param name="value">The string value for this segment.</param>
    /// <returns>A new <see cref="CorrelationKey"/> instance.</returns>
    public static CorrelationKey Create(CorrelationKey? parent, string value)
        => new(parent, value.AsMemory());

    /// <summary>
    /// Gets the parent correlation key, or null if this is a root key.
    /// </summary>
    /// <returns>The parent <see cref="CorrelationKey"/>, or null if there is no parent.</returns>
    public CorrelationKey? GetParent() => WithoutLastSegment(_value) switch
    {
        { Length: > 0 } value => new(_parent, value),
        _ => _parent,
    };

    /// <summary>
    /// Parses a string into a <see cref="CorrelationKey"/>.
    /// </summary>
    /// <param name="s">The string to parse.</param>
    /// <param name="provider">An optional format provider.</param>
    /// <returns>A new <see cref="CorrelationKey"/> instance.</returns>
    public static CorrelationKey Parse(string s, IFormatProvider? provider) => Create(s);

    /// <summary>
    /// Attempts to parse a string into a <see cref="CorrelationKey"/>.
    /// </summary>
    /// <param name="s">The string to parse.</param>
    /// <param name="provider">An optional format provider.</param>
    /// <param name="result">When this method returns, contains the parsed <see cref="CorrelationKey"/> if successful, or null if parsing failed.</param>
    /// <returns>True if parsing was successful; otherwise, false.</returns>
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, [MaybeNullWhen(false)] out CorrelationKey result)
    {
        if (s is { Length: > 0 } && IsSegmentationValid(s))
        {
            // Avoid re-validating the key.
            result = new CorrelationKey(s.AsMemory());
            return true;
        }

        result = null;
        return false;
    }

    /// <summary>
    /// Parses a span of characters into a <see cref="CorrelationKey"/>.
    /// </summary>
    /// <param name="s">The span to parse.</param>
    /// <param name="provider">An optional format provider.</param>
    /// <returns>A new <see cref="CorrelationKey"/> instance.</returns>
    public static CorrelationKey Parse(ReadOnlySpan<char> s, IFormatProvider? provider)
    {
        if (!TryParse(s, provider, out var result))
        {
            throw new InvalidOperationException("Unable to parse correlation key.");
        }

        return result;
    }

    /// <summary>
    /// Attempts to parse a span of characters into a <see cref="CorrelationKey"/>.
    /// </summary>
    /// <param name="s">The span to parse.</param>
    /// <param name="provider">An optional format provider.</param>
    /// <param name="result">When this method returns, contains the parsed <see cref="CorrelationKey"/> if successful, or null if parsing failed.</param>
    /// <returns>True if parsing was successful; otherwise, false.</returns>
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, [MaybeNullWhen(false)] out CorrelationKey result)
    {
        if (s is { Length: > 0 } && IsSegmentationValid(s))
        {
            // Avoid re-validating the key.
            result = new CorrelationKey(new string(s).AsMemory());
            return true;
        }

        result = null;
        return false;
    }

    /// <summary>
    /// Creates a new <see cref="CorrelationKey"/> with escaped segment separators.
    /// </summary>
    /// <param name="parent">The parent correlation key.</param>
    /// <param name="value">The value to escape.</param>
    /// <returns>A new <see cref="CorrelationKey"/> with escaped separators.</returns>
    public static CorrelationKey CreateEscaped(CorrelationKey? parent, ReadOnlyMemory<char> value)
    {
        var unescapedChars = UnescapedCharCount(value.Span);
        if (unescapedChars == 0)
        {
            return new CorrelationKey(parent, value);
        }

        return new CorrelationKey(parent, Escape(value.Span, unescapedChars).AsMemory());
    }

    /// <summary>
    /// Escapes segment separators in the specified value.
    /// </summary>
    /// <param name="value">The value to escape.</param>
    /// <param name="unescapedChars">The count of unescaped segment separators.</param>
    /// <returns>A new string with escaped segment separators.</returns>
    private static string Escape(ReadOnlySpan<char> value, int unescapedChars)
    {
        var resultArray = ArrayPool<char>.Shared.Rent(value.Length + unescapedChars);
        var isEscaped = false;
        var insertions = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (!isEscaped && c == SegmentSeparator)
            {
                resultArray[i + insertions] = EscapeCharacter;
                ++insertions;
                isEscaped = false;
            }

            if (c == EscapeCharacter)
            {
                isEscaped = !isEscaped;
            }

            resultArray[i + insertions] = c;
        }

        var result = new string(resultArray.AsSpan(0, value.Length + unescapedChars));
        ArrayPool<char>.Shared.Return(resultArray);
        return result;
    }

    /// <summary>
    /// Counts the number of unescaped segment separators in the value.
    /// </summary>
    /// <param name="value">The value to check.</param>
    /// <returns>The count of unescaped segment separators.</returns>
    private static int UnescapedCharCount(ReadOnlySpan<char> value)
    {
        var isEscaped = false;
        var result = 0;
        foreach (var c in value)
        {
            if (!isEscaped && c == SegmentSeparator)
            {
                ++result;
            }

            if (c == EscapeCharacter)
            {
                isEscaped = !isEscaped;
            }
            else
            {
                isEscaped = false;
            }
        }

        return result;
    }

    /// <summary>
    /// Returns a memory containing all segments except the last one.
    /// </summary>
    /// <param name="value">The value to process.</param>
    /// <returns>A memory containing the value without its last segment.</returns>
    private static ReadOnlyMemory<char> WithoutLastSegment(ReadOnlyMemory<char> value)
    {
        // Find the last segment in the value string by searching for the last unescaped segment separator
        var isEscaped = false;
        var lastSegmentStart = 0;
        var valueSpan = value.Span;
        for (var i = 0; i < valueSpan.Length; i++)
        {
            var c = valueSpan[i];
            if (c == SegmentSeparator)
            {
                if (!isEscaped)
                {
                    lastSegmentStart = i + 1;
                }

                isEscaped = false;
            }

            if (c == EscapeCharacter)
            {
                isEscaped = !isEscaped;
            }
        }

        return lastSegmentStart == 0 ? ReadOnlyMemory<char>.Empty : value[..(lastSegmentStart - 1)];
    }

    /// <summary>
    /// Gets the last segment from the specified value.
    /// </summary>
    /// <param name="value">The value to process.</param>
    /// <returns>A span containing the last segment.</returns>
    private static ReadOnlySpan<char> GetLastSegment(ReadOnlySpan<char> value)
    {
        // Find the last segment in the value string by searching for the last unescaped segment separator
        var isEscaped = false;
        var lastSegmentStart = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (!isEscaped && c == SegmentSeparator)
            {
                lastSegmentStart = i + 1;
            }

            if (c == EscapeCharacter)
            {
                isEscaped = !isEscaped;
            }
        }

        return value[lastSegmentStart..];
    }

    /// <summary>
    /// Validates that the value has proper segment separators and no empty segments.
    /// </summary>
    /// <param name="value">The value to validate.</param>
    /// <returns>True if the segmentation is valid; otherwise, false.</returns>
    private static bool IsSegmentationValid(ReadOnlySpan<char> value)
    {
        var isEscaped = false;
        var segmentLength = 0;
        foreach (var c in value)
        {
            ++segmentLength;

            if (isEscaped && c != SegmentSeparator && c != EscapeCharacter)
            {
                // The only characters which can be escaped are the escape character itself and the segment separator.
                return false;
            }

            if (c == EscapeCharacter)
            {
                // The escape character is allowed and can be used to escape itself.
                isEscaped = !isEscaped;
            }
            else if (c == SegmentSeparator)
            {
                // Check if this is the start of a new segment.
                if (!isEscaped)
                {
                    if (segmentLength <= 1)
                    {
                        // Empty segments are not allowed
                        return false;
                    }

                    segmentLength = 0;
                }

                isEscaped = false;
            }
            else
            {
                isEscaped = false;
            }
        }

        // The sequence must not end with an incomplete escape sequence.
        if (isEscaped)
        {
            return false;
        }

        // Empty segments are not valid
        if (segmentLength == 0)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Determines whether this correlation key is the parent of another key.
    /// </summary>
    /// <param name="other">The other correlation key to compare.</param>
    /// <returns>True if this key is the direct parent of the other key; otherwise, false.</returns>
    public bool IsParentOf(CorrelationKey? other)
    {
        if (other is null)
        {
            return false;
        }

        var left = GetEnumerator();
        var right = other.GetEnumerator();
        while (true)
        {
            var leftValid = left.MoveNext();
            var rightValid = right.MoveNext();
            if (!leftValid && !rightValid)
            {
                // Completed enumeration, both keys are equal and there is no parent/child relationship between them.
                return false;
            }
            else if (leftValid && !rightValid)
            {
                // The left key is longer than the right key, so it is not a prefix of it.
                return false;
            }
            else if (!leftValid && rightValid)
            {
                // The right key is longer than the left key, and all common components are equal,
                // so the left is the parent of the right if the right has one more segment.
                return !right.MoveNext();
            }
            else if (!left.Current.SequenceEqual(right.Current))
            {
                // Some segment is not equal and therefore neither is a prefix of the other.
                return false;
            }
        }
    }

    /// <summary>
    /// Determines whether this correlation key is a child of another key.
    /// </summary>
    /// <param name="other">The other correlation key to compare.</param>
    /// <returns>True if this key is a direct child of the other key; otherwise, false.</returns>
    public bool IsChildOf(CorrelationKey? other)
        => other is not null && other.IsParentOf(this);

    /// <summary>
    /// Determines whether this correlation key is an ancestor (parent, grandparent, etc.) of another key.
    /// </summary>
    /// <param name="other">The other correlation key to compare.</param>
    /// <returns>True if this key is an ancestor of the other key or equal to it; otherwise, false.</returns>
    public bool IsAncestorOf(CorrelationKey? other)
    {
        if (other is null)
        {
            return false;
        }

        var left = GetEnumerator();
        var right = other.GetEnumerator();
        while (true)
        {
            var leftValid = left.MoveNext();
            var rightValid = right.MoveNext();
            if (!leftValid && !rightValid)
            {
                // Completed enumeration, both keys are equal and therefore prefixes of each other.
                return true;
            }
            else if (leftValid && !rightValid)
            {
                // The left key is longer than the right key, so it is not a prefix of it.
                return false;
            }
            else if (!leftValid && rightValid)
            {
                // The right key is longer than the left key, and all common components are equal,
                // so the left is a prefix of the right.
                return true;
            }
            else if (!left.Current.SequenceEqual(right.Current))
            {
                // Some segment is not equal and therefore neither is a prefix of the other.
                return false;
            }
        }
    }

    /// <summary>
    /// Creates a new <see cref="CorrelationKey"/> with the specified value as a child of this key.
    /// </summary>
    /// <param name="value">The value for the child key.</param>
    /// <returns>A new <see cref="CorrelationKey"/> instance.</returns>
    public CorrelationKey CreateChildKey(string value)
        => new(this, value.AsMemory());

    /// <summary>
    /// Creates a new <see cref="CorrelationKey"/> with escaped segment separators as a child of this key.
    /// </summary>
    /// <param name="value">The value to escape and use for the child key.</param>
    /// <returns>A new <see cref="CorrelationKey"/> instance.</returns>
    public CorrelationKey CreateEscapedChildKey(string value)
        => CreateEscaped(this, value.AsMemory());

    /// <summary>
    /// Creates a new <see cref="CorrelationKey"/> from an escaped string value.
    /// </summary>
    /// <param name="value">The value to escape.</param>
    /// <returns>A new <see cref="CorrelationKey"/> instance.</returns>
    public static CorrelationKey CreateEscaped(string value)
        => CreateEscaped(null, value.AsMemory());

    /// <inheritdoc/>
    public override string ToString() => $"{this}";

    /// <summary>
    /// Gets the total length of the correlation key string.
    /// </summary>
    public int Length
    {
        get
        {
            var length = 0;
            foreach (var segment in this)
            {
                // Account for segment separators.
                if (length > 0)
                {
                    ++length;
                }

                length += segment.Length;
            }

            return length;
        }
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj)
    {
        if (obj is not CorrelationKey other)
        {
            return false;
        }

        return Equals(other);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        // Note that we want to ensure that GetHashCode returns equal values for semantically equivalent instances.
        // To achieve this, we treat the instances as a sequence of bytes, independent of where in the chain of
        // instances the various segments sit.
        // This allows for one instance with a value "foo/bar" and a child with "baz" to have the same hash code
        // as an instance with the value "foo/bar/baz".
        var length = Length;
        var array = length <= 256 ? null : ArrayPool<char>.Shared.Rent(length);
        Span<char> buffer = array ?? stackalloc char[256];

        // Write the value into the buffer.
        var didFormat = TryFormat(buffer, out var len, ReadOnlySpan<char>.Empty, null);
        buffer = buffer[..len];
        Debug.Assert(didFormat);

        HashCode hashCode = new();
        hashCode.AddBytes(MemoryMarshal.AsBytes(buffer));

        if (array is not null)
        {
            ArrayPool<char>.Shared.Return(array);
        }

        return hashCode.ToHashCode();
    }

    /// <inheritdoc/>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        if (_parent is not null)
        {
            if (_parent.TryFormat(destination, out charsWritten, format, provider))
            {
                destination = destination[charsWritten..];
                if (destination.Length > 0)
                {
                    destination[0] = SegmentSeparator;
                    destination = destination[1..];
                    ++charsWritten;
                }
            }
            else
            {
                return false;
            }
        }
        else
        {
            charsWritten = 0;
        }

        if (_value.Span.TryCopyTo(destination))
        {
            charsWritten += _value.Length;
            return true;
        }

        return false;
    }

    /// <inheritdoc/>
    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    /// <summary>
    /// Gets an enumerator for iterating over the segments of this correlation key.
    /// </summary>
    /// <returns>A <see cref="SegmentEnumerator"/> for this key.</returns>
    public SegmentEnumerator GetEnumerator() => new(this);

    /// <inheritdoc/>
    public bool Equals(CorrelationKey? other)
    {
        if (other is null)
        {
            return false;
        }

        var left = GetEnumerator();
        var right = other.GetEnumerator();
        while (true)
        {
            var leftValid = left.MoveNext();
            var rightValid = right.MoveNext();
            if (!leftValid && !rightValid)
            {
                // Completed enumeration.
                return true;
            }
            else if (leftValid ^ rightValid)
            {
                // One side is complete and the other is not.
                return false;
            }
            else if (!left.Current.SequenceEqual(right.Current))
            {
                // Some segment is not equal.
                return false;
            }
        }
    }

    /// <summary>
    /// Enumerates the segments of a <see cref="CorrelationKey"/>.
    /// </summary>
    public ref struct SegmentEnumerator(CorrelationKey id)
    {
        private StructureEnumerator _enumerator = new(id);
        private ReadOnlySpan<char> _buffer = ReadOnlySpan<char>.Empty;

        /// <summary>
        /// Gets the current segment.
        /// </summary>
        public ReadOnlySpan<char> Current { get; private set; }

        /// <summary>
        /// Advances the enumerator to the next segment.
        /// </summary>
        /// <returns>True if there is a next segment; otherwise, false.</returns>
        public bool MoveNext()
        {
            if (_buffer.Length == 0)
            {
                if (!_enumerator.MoveNext())
                {
                    return false;
                }

                _buffer = _enumerator.Current;
            }

            Current = GetNextSegment();
            _buffer = _buffer[Current.Length..];

            if (_buffer.Length > 0 && _buffer[0] == SegmentSeparator)
            {
                _buffer = _buffer[1..];
            }

            while (Current.Length == 0)
            {
                // Advance
                if (!MoveNext())
                {
                    return false;
                }
            }

            return true;
        }

        private readonly ReadOnlySpan<char> GetNextSegment()
        {
            var buffer = _buffer;
            var isEscaped = false;
            var length = 0;
            foreach (var c in buffer)
            {
                ++length;
                if (c == EscapeCharacter)
                {
                    isEscaped = !isEscaped;
                    continue;
                }
                else if (c == SegmentSeparator && !isEscaped)
                {
                    --length;
                    break;
                }

                isEscaped = false;
            }

            return buffer[..length];
        }
    }

    /// <summary>
    /// Enumerates the structural components (parent chain and value) of a <see cref="CorrelationKey"/>.
    /// </summary>
    private struct StructureEnumerator(CorrelationKey value)
    {
        private readonly CorrelationKey? _current = value;
        private int _remaining = -2;

        /// <summary>
        /// Gets the current structural element.
        /// </summary>
        public readonly ReadOnlySpan<char> Current => _remaining switch
        {
            -2 => throw new InvalidOperationException($"'{nameof(MoveNext)}' must be called before accessing '{nameof(Current)}'."),
            -1 => throw new InvalidOperationException("No remaining elements."),
            int depth => GetElement(_current, depth),
        };

        private static int GetElementCount(CorrelationKey? current)
        {
            var elements = 0;
            while (current is not null)
            {
                ++elements;
                current = current._parent;
            }

            // If there is more than one segment, insert a separator segment between each.
            if (elements > 1)
            {
                elements += elements - 1;
            }

            return elements;
        }

        private static ReadOnlySpan<char> GetElement(CorrelationKey? current, int depth)
        {
            // Add a separator between each segment
            if (depth % 2 == 1)
            {
                return SegmentSeparatorSpan;
            }

            depth /= 2;
            while (depth-- > 0)
            {
                current = current!._parent;
            }

            return current!._value.Span;
        }

        /// <summary>
        /// Advances the enumerator to the next structural element.
        /// </summary>
        /// <returns>True if there is a next element; otherwise, false.</returns>
        public bool MoveNext()
        {
            // Start: calculate the number of elements
            if (_remaining == -2)
            {
                _remaining = GetElementCount(_current);
            }

            // If there are no elements remaining 
            if (_remaining == 0)
            {
                return false;
            }

            --_remaining;
            return true;
        }
    }
}
