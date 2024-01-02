using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace System.Distributed.DurableTasks;

internal sealed class HierarchicalKey : ISpanFormattable, IEquatable<HierarchicalKey>, IParsable<HierarchicalKey>, ISpanParsable<HierarchicalKey>
{
    public const char EscapeCharacter = '\\';
    public const char SegmentSeparator = '/';
    private static ReadOnlySpan<char> SegmentSeparatorSpan => "/";

    private readonly HierarchicalKey? _parent;

    private readonly ReadOnlyMemory<char> _value;

    private HierarchicalKey(ReadOnlyMemory<char> value)
    {
        _value = value;
    }

    private HierarchicalKey(HierarchicalKey? parent, ReadOnlyMemory<char> value) : this(value)
    {
        _parent = parent;
    }

    public static HierarchicalKey Create(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        if (!IsSegmentationValid(value))
        {
            throw new ArgumentException("Value must not contain empty segments.", nameof(value));
        }

        return new(value.AsMemory());
    }

    public static HierarchicalKey Create(HierarchicalKey? parent, string value) => new(parent, value.AsMemory());

    public HierarchicalKey? GetParent() => WithoutLastSegment(_value) switch
    {
        { Length: > 0 } value => new(_parent, value),
        _ => _parent,
    };

    public static HierarchicalKey Parse(string s, IFormatProvider? provider) => Create(s);

    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, [MaybeNullWhen(false)] out HierarchicalKey result)
    {
        if (s is { Length: > 0 } && IsSegmentationValid(s))
        {
            // Avoid re-validating the key.
            result = new HierarchicalKey(s.AsMemory());
            return true;
        }

        result = null;
        return false;
    }

    public static HierarchicalKey Parse(ReadOnlySpan<char> s, IFormatProvider? provider)
    {
        if (!TryParse(s, provider, out var result))
        {
            throw new InvalidOperationException("Unable to parse hierarchical key.");
        }

        return result;
    }

    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, [MaybeNullWhen(false)] out HierarchicalKey result)
    {
        if (s is { Length: > 0 } && IsSegmentationValid(s))
        {
            // Avoid re-validating the key.
            result = new HierarchicalKey(new string(s).AsMemory());
            return true;
        }

        result = null;
        return false;
    }

    public static HierarchicalKey CreateEscaped(HierarchicalKey? parent, ReadOnlyMemory<char> value)
    {
        var unescapedChars = UnescapedCharCount(value.Span);
        if (unescapedChars == 0)
        {
            return new HierarchicalKey(parent, value);
        }

        return new HierarchicalKey(parent, Escape(value.Span, unescapedChars).AsMemory());
    }

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

        return new string(resultArray.AsSpan(0, value.Length + unescapedChars));
    }

    private static int UnescapedCharCount(ReadOnlySpan<char> value)
    {
        var isEscaped = false;
        var result = 0;
        foreach (var c in value)
        {
            if (!isEscaped && c == SegmentSeparator)
            {
                ++result;
                isEscaped = false;
            }

            if (c == EscapeCharacter)
            {
                isEscaped = !isEscaped;
            }
        }

        return result;
    }

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
                        // Empty segments are not allowed (the segment contains only an segment separator)
                        return false;
                    }

                    segmentLength = 0;
                }

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
    /// Returns <value>true</value> if this key is direct descendant of the provided key, <value>false</value> otherwise.
    /// </summary>
    /// <param name="other">The key to check this key against.</param>
    /// <returns><value>true</value> if this key is a direct descendant of <paramref name="other"/>, <value>false</value> otherwise.</returns>
    public bool IsChildOf(HierarchicalKey? other) => other is not null && other.IsParentOf(this);

    /// <summary>
    /// Returns <value>true</value> if this key is a direct ancestor of provided key, <value>false</value> otherwise.
    /// </summary>
    /// <param name="other">The key to check this key against.</param>
    /// <returns><value>true</value> if this key is a direct ancestor of <paramref name="other"/>, <value>false</value> otherwise.</returns>
    public bool IsParentOf(HierarchicalKey? other)
    {
        if (other is null) return false;
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
    /// Returns <value>true</value> if this key is an ancestor (parent or earlier) of the provided key, <value>false</value> otherwise.
    /// </summary>
    /// <param name="other">The key to check this key against.</param>
    /// <returns><value>true</value> if this key is a prefix of <paramref name="other"/>, <value>false</value> otherwise.</returns>
    public bool IsAncestorOf(HierarchicalKey? other)
    {
        if (other is null) return false;
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
    /// Creates a new key, escaping any unescaped segment separators in <paramref name="value"/>, and returns it.
    /// </summary>
    /// <param name="value">The value.</param>
    public static HierarchicalKey CreateEscaped(string value) => CreateEscaped(null, value.AsMemory());

    /// <summary>
    /// Creates a key which is a child of this key, escaping any unescaped segment separators in <paramref name="value"/>, and returns it.
    /// </summary>
    /// <param name="value">The value for the child segments.</param>
    public HierarchicalKey CreateEscapedChildKey(string value) => CreateEscaped(this, value.AsMemory());

    /// <summary>
    /// Creates a key which is a child of this key and returns it.
    /// </summary>
    /// <param name="value">The value for the child segments.</param>
    /// <returns></returns>
    public HierarchicalKey CreateChildKey(string value) => new(this, value.AsMemory());

    /// <inheritdoc/>
    public override string ToString() => $"{this}";

    /// <summary>
    /// Gets the number of characters which comprise the key.
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
        if (obj is not HierarchicalKey other) return false;
        return Equals(other);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        // Note that we want to ensure that GetHashCode returns equal values for semantically equivalent
        // instances. To achieve this, we treat the instances as a sequence of bytes, independent of
        // where in the chain of instances the various segments sit.
        // This allows for one instance with a value "foo/bar" and a child with "baz" to have the same
        // hash code as an instance with the value "foo/bar/baz".
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

    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    public SegmentEnumerator GetEnumerator() => new(this);
    public bool Equals(HierarchicalKey? other)
    {
        if (other is null) return false;

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

    public ref struct SegmentEnumerator(HierarchicalKey id)
    {
        private StructureEnumerator _enumerator = new StructureEnumerator(id);
        private ReadOnlySpan<char> _buffer = ReadOnlySpan<char>.Empty;

        public ReadOnlySpan<char> Current { get; private set; }

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

    private struct StructureEnumerator(HierarchicalKey value)
    {
        private readonly HierarchicalKey? _current = value;
        private int _remaining = -2;

        public ReadOnlySpan<char> Current => _remaining switch
        {
            -2 => throw new InvalidOperationException($"'{nameof(MoveNext)}' must be called before accessing '{nameof(Current)}'."),
            -1 => throw new InvalidOperationException("No remaining elements."),
            int depth => GetElement(_current, depth),
        };

        private static int GetElementCount(HierarchicalKey? current)
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

        private static ReadOnlySpan<char> GetElement(HierarchicalKey? current, int depth)
        {
            // Add a separator between each segment
            if (depth % 2 == 1) return SegmentSeparatorSpan;
            depth /= 2;
            while (depth-- > 0)
            {
                current = current!._parent;
            }

            return current!._value.Span;
        }

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

