using System.Collections.ObjectModel;

namespace Orleans.Journaling;

/// <summary>
/// Identifies a provider-neutral hierarchical prefix for listing journal storage ids.
/// </summary>
public sealed class JournalStoragePrefix : IEquatable<JournalStoragePrefix>
{
    private static readonly StringComparer Comparer = StringComparer.Ordinal;
    private readonly ReadOnlyCollection<string> _segments;

    private JournalStoragePrefix(string value, string[] segments)
    {
        Value = value;
        _segments = Array.AsReadOnly(segments);
    }

    /// <summary>
    /// Gets the root prefix, which matches all journal storage ids.
    /// </summary>
    public static JournalStoragePrefix Root { get; } = new(string.Empty, []);

    /// <summary>
    /// Gets the normalized prefix value.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Gets the decoded prefix segments.
    /// </summary>
    public IReadOnlyList<string> Segments => _segments;

    /// <summary>
    /// Gets a value indicating whether this is the root prefix.
    /// </summary>
    public bool IsRoot => Value.Length == 0;

    /// <summary>
    /// Creates a prefix from decoded hierarchical segments.
    /// </summary>
    /// <param name="segments">The prefix segments.</param>
    /// <returns>The normalized prefix.</returns>
    public static JournalStoragePrefix Create(params string[] segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        return segments.Length == 0 ? Root : JournalStorageId.Create(segments).AsPrefix();
    }

    /// <summary>
    /// Creates a prefix from decoded hierarchical segments.
    /// </summary>
    /// <param name="segments">The prefix segments.</param>
    /// <returns>The normalized prefix.</returns>
    public static JournalStoragePrefix Create(IEnumerable<string> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        var segmentArray = segments.ToArray();
        return segmentArray.Length == 0 ? Root : JournalStorageId.Create(segmentArray).AsPrefix();
    }

    /// <summary>
    /// Parses a normalized prefix value.
    /// </summary>
    /// <param name="value">The normalized prefix value, or an empty string for <see cref="Root"/>.</param>
    /// <returns>The parsed prefix.</returns>
    public static JournalStoragePrefix Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0)
        {
            return Root;
        }

        var id = JournalStorageId.Parse(value);
        return new(id.Value, id.Segments.ToArray());
    }

    /// <summary>
    /// Determines whether this prefix matches <paramref name="storageId"/>.
    /// </summary>
    /// <param name="storageId">The storage id.</param>
    /// <returns><see langword="true"/> if this prefix matches <paramref name="storageId"/>; otherwise <see langword="false"/>.</returns>
    public bool Matches(JournalStorageId storageId)
    {
        ArgumentNullException.ThrowIfNull(storageId);
        return IsRoot
            || Comparer.Equals(storageId.Value, Value)
            || storageId.Value.StartsWith(Value + "/", StringComparison.Ordinal);
    }

    /// <inheritdoc/>
    public bool Equals(JournalStoragePrefix? other) => other is not null && Comparer.Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is JournalStoragePrefix other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Comparer.GetHashCode(Value);

    /// <inheritdoc/>
    public override string ToString() => Value;

    /// <summary>
    /// Compares two journal storage prefixes for equality.
    /// </summary>
    public static bool operator ==(JournalStoragePrefix? left, JournalStoragePrefix? right) => Equals(left, right);

    /// <summary>
    /// Compares two journal storage prefixes for inequality.
    /// </summary>
    public static bool operator !=(JournalStoragePrefix? left, JournalStoragePrefix? right) => !Equals(left, right);
}
