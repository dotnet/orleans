using System.Diagnostics.CodeAnalysis;
using Orleans;

namespace Orleans.Journaling.Messaging;

/// <summary>
/// Represents a hierarchical correlation key for distributed message tracing.
/// </summary>
/// <remarks>
/// <para>
/// <strong>DEPRECATED:</strong> Use <see cref="HierarchicalKey"/> from the <c>Orleans</c> namespace instead.
/// This class is provided for backward compatibility and will be removed in a future release.
/// </para>
/// <para>
/// CorrelationKey uses '/' as the segment separator and '\' as the escape character,
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
/// <para>
/// Migration example:
/// <code>
/// // Old code:
/// CorrelationKey key = CorrelationKey.Create("transfer-123");
/// 
/// // New code:
/// HierarchicalKey key = HierarchicalKey.Create("transfer-123");
/// </code>
/// </para>
/// </remarks>
[GenerateSerializer, Immutable]
public sealed class CorrelationKey : ISpanFormattable, IEquatable<CorrelationKey>, IParsable<CorrelationKey>, ISpanParsable<CorrelationKey>
{
    /// <summary>
    /// The character used to escape special characters in segment values.
    /// </summary>
    public const char EscapeCharacter = HierarchicalKey.EscapeCharacter;

    /// <summary>
    /// The character used to separate segments in the hierarchical path.
    /// </summary>
    public const char SegmentSeparator = HierarchicalKey.SegmentSeparator;

    [Id(0)]
    private readonly HierarchicalKey _inner;

    private CorrelationKey(HierarchicalKey inner)
    {
        _inner = inner;
    }

    /// <summary>
    /// Implicitly converts a <see cref="CorrelationKey"/> to a <see cref="HierarchicalKey"/>.
    /// </summary>
    /// <param name="key">The <see cref="CorrelationKey"/> to convert.</param>
    public static implicit operator HierarchicalKey?(CorrelationKey? key) => key?._inner;

    /// <summary>
    /// Implicitly converts a <see cref="HierarchicalKey"/> to a <see cref="CorrelationKey"/>.
    /// </summary>
    /// <param name="key">The <see cref="HierarchicalKey"/> to convert.</param>
    public static implicit operator CorrelationKey?(HierarchicalKey? key) => key is null ? null : new CorrelationKey(key);

    /// <summary>
    /// Creates a new <see cref="CorrelationKey"/> from the specified string value.
    /// </summary>
    /// <param name="value">The string value representing the correlation key.</param>
    /// <returns>A new <see cref="CorrelationKey"/> instance.</returns>
    /// <exception cref="ArgumentException">Thrown when the value is null, empty, or contains invalid segment separators.</exception>
    public static CorrelationKey Create(string value)
        => new(HierarchicalKey.Create(value));

    /// <summary>
    /// Creates a new <see cref="CorrelationKey"/> with the specified parent and value.
    /// </summary>
    /// <param name="parent">The parent correlation key, or null for a root key.</param>
    /// <param name="value">The string value for this segment.</param>
    /// <returns>A new <see cref="CorrelationKey"/> instance.</returns>
    public static CorrelationKey Create(CorrelationKey? parent, string value)
        => new(HierarchicalKey.Create(parent?._inner, value));

    /// <summary>
    /// Gets the parent correlation key, or null if this is a root key.
    /// </summary>
    /// <returns>The parent <see cref="CorrelationKey"/>, or null if there is no parent.</returns>
    public CorrelationKey? GetParent()
    {
        var parent = _inner.GetParent();
        return parent is null ? null : new CorrelationKey(parent);
    }

    /// <summary>
    /// Parses a string into a <see cref="CorrelationKey"/>.
    /// </summary>
    /// <param name="s">The string to parse.</param>
    /// <param name="provider">An optional format provider.</param>
    /// <returns>A new <see cref="CorrelationKey"/> instance.</returns>
    public static CorrelationKey Parse(string s, IFormatProvider? provider)
        => new(HierarchicalKey.Parse(s, provider));

    /// <summary>
    /// Attempts to parse a string into a <see cref="CorrelationKey"/>.
    /// </summary>
    /// <param name="s">The string to parse.</param>
    /// <param name="provider">An optional format provider.</param>
    /// <param name="result">When this method returns, contains the parsed <see cref="CorrelationKey"/> if successful, or null if parsing failed.</param>
    /// <returns>True if parsing was successful; otherwise, false.</returns>
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, [MaybeNullWhen(false)] out CorrelationKey result)
    {
        if (HierarchicalKey.TryParse(s, provider, out var innerResult))
        {
            result = new CorrelationKey(innerResult);
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
        => new(HierarchicalKey.Parse(s, provider));

    /// <summary>
    /// Attempts to parse a span of characters into a <see cref="CorrelationKey"/>.
    /// </summary>
    /// <param name="s">The span to parse.</param>
    /// <param name="provider">An optional format provider.</param>
    /// <param name="result">When this method returns, contains the parsed <see cref="CorrelationKey"/> if successful, or null if parsing failed.</param>
    /// <returns>True if parsing was successful; otherwise, false.</returns>
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, [MaybeNullWhen(false)] out CorrelationKey result)
    {
        if (HierarchicalKey.TryParse(s, provider, out var innerResult))
        {
            result = new CorrelationKey(innerResult);
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
        => new(HierarchicalKey.CreateEscaped(parent?._inner, value));

    /// <summary>
    /// Determines whether this correlation key is the parent of another key.
    /// </summary>
    /// <param name="other">The other correlation key to compare.</param>
    /// <returns>True if this key is the direct parent of the other key; otherwise, false.</returns>
    public bool IsParentOf(CorrelationKey? other)
        => other is not null && _inner.IsParentOf(other._inner);

    /// <summary>
    /// Determines whether this correlation key is a child of another key.
    /// </summary>
    /// <param name="other">The other correlation key to compare.</param>
    /// <returns>True if this key is a direct child of the other key; otherwise, false.</returns>
    public bool IsChildOf(CorrelationKey? other)
        => other is not null && _inner.IsChildOf(other._inner);

    /// <summary>
    /// Determines whether this correlation key is an ancestor (parent, grandparent, etc.) of another key.
    /// </summary>
    /// <param name="other">The other correlation key to compare.</param>
    /// <returns>True if this key is an ancestor of the other key or equal to it; otherwise, false.</returns>
    public bool IsAncestorOf(CorrelationKey? other)
        => other is not null && _inner.IsAncestorOf(other._inner);

    /// <summary>
    /// Creates a new <see cref="CorrelationKey"/> with the specified value as a child of this key.
    /// </summary>
    /// <param name="value">The value for the child key.</param>
    /// <returns>A new <see cref="CorrelationKey"/> instance.</returns>
    public CorrelationKey CreateChildKey(string value)
        => new(_inner.CreateChildKey(value));

    /// <summary>
    /// Creates a new <see cref="CorrelationKey"/> with escaped segment separators as a child of this key.
    /// </summary>
    /// <param name="value">The value to escape and use for the child key.</param>
    /// <returns>A new <see cref="CorrelationKey"/> instance.</returns>
    public CorrelationKey CreateEscapedChildKey(string value)
        => new(_inner.CreateEscapedChildKey(value));

    /// <summary>
    /// Creates a new <see cref="CorrelationKey"/> from an escaped string value.
    /// </summary>
    /// <param name="value">The value to escape.</param>
    /// <returns>A new <see cref="CorrelationKey"/> instance.</returns>
    public static CorrelationKey CreateEscaped(string value)
        => new(HierarchicalKey.CreateEscaped(value));

    /// <inheritdoc/>
    public override string ToString() => _inner.ToString();

    /// <summary>
    /// Gets the total length of the correlation key string.
    /// </summary>
    public int Length => _inner.Length;

    /// <inheritdoc/>
    public override bool Equals(object? obj)
        => obj is CorrelationKey other && _inner.Equals(other._inner);

    /// <inheritdoc/>
    public override int GetHashCode() => _inner.GetHashCode();

    /// <inheritdoc/>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
        => _inner.TryFormat(destination, out charsWritten, format, provider);

    /// <inheritdoc/>
    public string ToString(string? format, IFormatProvider? formatProvider)
        => _inner.ToString(format, formatProvider);

    /// <summary>
    /// Gets an enumerator for iterating over the segments of this correlation key.
    /// </summary>
    /// <returns>A <see cref="SegmentEnumerator"/> for this key.</returns>
    public SegmentEnumerator GetEnumerator() => new(_inner);

    /// <inheritdoc/>
    public bool Equals(CorrelationKey? other)
        => other is not null && _inner.Equals(other._inner);

    /// <summary>
    /// Enumerates the segments of a <see cref="CorrelationKey"/>.
    /// </summary>
    public ref struct SegmentEnumerator(HierarchicalKey key)
    {
        private HierarchicalKey.SegmentEnumerator _innerEnumerator = key.GetEnumerator();

        /// <summary>
        /// Gets the current segment.
        /// </summary>
        public ReadOnlySpan<char> Current => _innerEnumerator.Current;

        /// <summary>
        /// Advances the enumerator to the next segment.
        /// </summary>
        /// <returns>True if there is a next segment; otherwise, false.</returns>
        public bool MoveNext() => _innerEnumerator.MoveNext();
    }
}
