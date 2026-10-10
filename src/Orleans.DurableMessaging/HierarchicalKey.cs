using System;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Orleans.DurableMessaging;

/// <summary>
/// An immutable, ordinal application identity formed from nonempty hierarchical segments.
/// </summary>
/// <remarks>
/// Create constructs literal segments and escapes slash and backslash characters exactly once.
/// Parse reads the canonical escaped path. Assignment shares immutable backing data; equality and
/// hashing use the full canonical identity. Applications preserve the identity across retries.
/// </remarks>
[Immutable, Alias("Orleans.DurableMessaging.HierarchicalKey")]
public readonly struct HierarchicalKey : ISpanFormattable, IEquatable<HierarchicalKey>, IParsable<HierarchicalKey>, ISpanParsable<HierarchicalKey>
{
    /// <summary>The escape character used within canonical segments.</summary>
    public const char EscapeCharacter = '\\';

    /// <summary>The separator between canonical segments.</summary>
    public const char SegmentSeparator = '/';

    private readonly KeyData? _data;

    private HierarchicalKey(string canonical, int segmentCount) => _data = new(canonical, segmentCount);

    /// <summary>Gets whether this value is unset.</summary>
    public bool IsDefault => _data is null;

    /// <summary>Gets the canonical path length in UTF-16 characters.</summary>
    public int Length => _data?.Canonical.Length ?? 0;

    /// <summary>Gets the number of segments, or zero for an unset key.</summary>
    public int SegmentCount => _data?.SegmentCount ?? 0;

    /// <summary>Creates one literal segment, escaping slash and backslash characters.</summary>
    /// <param name="value">The nonempty literal segment.</param>
    /// <returns>The segment identity.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty.</exception>
    public static HierarchicalKey Create(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        return new(Escape(value), 1);
    }

    /// <summary>Creates a hierarchy from literal segments in root-first order.</summary>
    /// <param name="values">The nonempty literal segments.</param>
    /// <returns>A flat canonical identity which owns its immutable backing string.</returns>
    /// <exception cref="ArgumentNullException">A segment is null.</exception>
    /// <exception cref="ArgumentException">The input or a segment is empty.</exception>
    public static HierarchicalKey Create(params ReadOnlySpan<string> values)
    {
        if (values.IsEmpty)
        {
            throw new ArgumentException("Values must not be empty.", nameof(values));
        }
        var builder = new StringBuilder();
        foreach (var value in values)
        {
            ArgumentException.ThrowIfNullOrEmpty(value);
            if (builder.Length > 0) builder.Append(SegmentSeparator);
            AppendEscaped(builder, value);
        }
        return new(builder.ToString(), values.Length);
    }

    /// <summary>Appends one literal child segment.</summary>
    /// <param name="value">The nonempty literal child segment.</param>
    /// <returns>The child identity.</returns>
    /// <exception cref="InvalidOperationException">This key is unset.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty.</exception>
    public HierarchicalKey CreateChildKey(string value)
    {
        EnsureSet();
        ArgumentException.ThrowIfNullOrEmpty(value);
        return new(string.Concat(_data!.Canonical, "/", Escape(value)), checked(SegmentCount + 1));
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
        return new(string.Concat(_data!.Canonical, "/", suffix._data!.Canonical), checked(SegmentCount + suffix.SegmentCount));
    }

    /// <summary>Gets the immediate parent, or null for a root or unset key.</summary>
    /// <returns>The parent identity when this key has more than one segment.</returns>
    public HierarchicalKey? GetParent()
    {
        if (SegmentCount < 2) return null;
        var lastSeparator = 0;
        var path = _data!.Canonical.AsSpan();
        for (var i = 0; i < path.Length; i++)
        {
            if (path[i] == EscapeCharacter) i++;
            else if (path[i] == SegmentSeparator) lastSeparator = i;
        }
        return new HierarchicalKey(_data.Canonical[..lastSeparator], SegmentCount - 1);
    }

    /// <summary>Tests whether this key is the other's immediate child.</summary>
    /// <param name="other">The potential parent.</param>
    /// <returns>Whether there is exactly one additional segment.</returns>
    public bool IsChildOf(HierarchicalKey other) => other.IsParentOf(this);

    /// <summary>Tests whether this key is the other's immediate parent.</summary>
    /// <param name="other">The potential child.</param>
    /// <returns>Whether the other key extends this key by exactly one segment.</returns>
    public bool IsParentOf(HierarchicalKey other) => !IsDefault && other.SegmentCount == SegmentCount + 1 && IsAncestorOf(other);

    /// <summary>Tests whether this key is equal to or an ancestor of the other key.</summary>
    /// <param name="other">The identity to inspect.</param>
    /// <returns>Whether the full prefix consists of equal ordinal segments. Unset keys return false.</returns>
    public bool IsAncestorOf(HierarchicalKey other) => !IsDefault && !other.IsDefault
        && (Equals(other) || (other.Length > Length && other._data!.Canonical[Length] == SegmentSeparator
            && other._data.Canonical.StartsWith(_data!.Canonical, StringComparison.Ordinal)));

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
        if (s is not null && TryCountSegments(s, out var count))
        {
            result = new(s, count);
            return true;
        }
        result = default;
        return false;
    }

    /// <inheritdoc/>
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out HierarchicalKey result)
    {
        if (TryCountSegments(s, out var count))
        {
            result = new(new string(s), count);
            return true;
        }
        result = default;
        return false;
    }

    /// <inheritdoc/>
    public bool Equals(HierarchicalKey other) => ReferenceEquals(_data, other._data)
        || string.Equals(_data?.Canonical, other._data?.Canonical, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is HierarchicalKey other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => _data?.Hash ?? 0;

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
    public override string ToString() => _data?.Canonical ?? string.Empty;

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

    private static bool TryCountSegments(ReadOnlySpan<char> path, out int count)
    {
        count = 0;
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
                count++;
                segmentLength = 0;
                continue;
            }
            segmentLength++;
        }
        if (segmentLength == 0) return false;
        count++;
        return true;
    }

    private sealed class KeyData(string canonical, int segmentCount)
    {
        public string Canonical { get; } = canonical;
        public int Hash { get; } = canonical.GetHashCode(StringComparison.Ordinal);
        public int SegmentCount { get; } = segmentCount;
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
