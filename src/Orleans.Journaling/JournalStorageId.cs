using System.Collections.ObjectModel;

namespace Orleans.Journaling;

/// <summary>
/// Identifies one logical journal storage instance independently of any storage provider.
/// </summary>
/// <remarks>
/// A journal storage id is hierarchical and provider-neutral. Providers map the normalized
/// <see cref="Value"/> to physical storage names. Use <see cref="ForGrain(GrainId)"/> for
/// grain-scoped journals and <see cref="Create(string, string[])"/> for named journals such
/// as DurableJobs shard journals.
/// </remarks>
public sealed class JournalStorageId : IComparable<JournalStorageId>, IEquatable<JournalStorageId>
{
    private const char Separator = '/';
    private static readonly StringComparer Comparer = StringComparer.Ordinal;
    private readonly ReadOnlyCollection<string> _segments;

    private JournalStorageId(string value, string[] segments)
    {
        Value = value;
        _segments = Array.AsReadOnly(segments);
    }

    /// <summary>
    /// Gets the normalized, provider-neutral storage id value.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Gets the decoded hierarchical id segments.
    /// </summary>
    public IReadOnlyList<string> Segments => _segments;

    /// <summary>
    /// Creates an id from decoded hierarchical segments.
    /// </summary>
    /// <param name="firstSegment">The first id segment.</param>
    /// <param name="additionalSegments">Additional id segments.</param>
    /// <returns>The normalized journal storage id.</returns>
    public static JournalStorageId Create(string firstSegment, params string[] additionalSegments)
    {
        ArgumentNullException.ThrowIfNull(additionalSegments);

        var segments = new string[additionalSegments.Length + 1];
        segments[0] = firstSegment;
        Array.Copy(additionalSegments, 0, segments, 1, additionalSegments.Length);
        return Create(segments);
    }

    /// <summary>
    /// Creates an id from decoded hierarchical segments.
    /// </summary>
    /// <param name="segments">The id segments.</param>
    /// <returns>The normalized journal storage id.</returns>
    public static JournalStorageId Create(IEnumerable<string> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        var decodedSegments = segments.ToArray();
        if (decodedSegments.Length == 0)
        {
            throw new ArgumentException("A journal storage id must contain at least one segment.", nameof(segments));
        }

        var encodedSegments = new string[decodedSegments.Length];
        for (var i = 0; i < decodedSegments.Length; i++)
        {
            encodedSegments[i] = EncodeSegment(decodedSegments[i], nameof(segments));
        }

        return new(string.Join(Separator, encodedSegments), decodedSegments);
    }

    /// <summary>
    /// Parses a normalized journal storage id value.
    /// </summary>
    /// <param name="value">The normalized journal storage id value.</param>
    /// <returns>The parsed journal storage id.</returns>
    public static JournalStorageId Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (value[0] == Separator || value[^1] == Separator)
        {
            throw new ArgumentException("A journal storage id must not start or end with a separator.", nameof(value));
        }

        var encodedSegments = value.Split(Separator);
        var decodedSegments = new string[encodedSegments.Length];
        for (var i = 0; i < encodedSegments.Length; i++)
        {
            if (encodedSegments[i].Length == 0)
            {
                throw new ArgumentException("A journal storage id must not contain empty segments.", nameof(value));
            }

            decodedSegments[i] = Uri.UnescapeDataString(encodedSegments[i]);
        }

        return Create(decodedSegments);
    }

    /// <summary>
    /// Creates the grain-scoped journal storage id for <paramref name="grainContext"/>.
    /// </summary>
    /// <param name="grainContext">The grain context.</param>
    /// <returns>The grain-scoped journal storage id.</returns>
    public static JournalStorageId ForGrain(IGrainContext grainContext)
    {
        ArgumentNullException.ThrowIfNull(grainContext);
        return ForGrain(grainContext.GrainId);
    }

    /// <summary>
    /// Creates the grain-scoped journal storage id for <paramref name="grainId"/>.
    /// </summary>
    /// <param name="grainId">The grain id.</param>
    /// <returns>The grain-scoped journal storage id.</returns>
    public static JournalStorageId ForGrain(GrainId grainId) => Create("grains", grainId.ToString());

    /// <summary>
    /// Converts this id into a prefix matching this id and its descendants.
    /// </summary>
    /// <returns>A prefix for this id.</returns>
    public JournalStoragePrefix AsPrefix() => JournalStoragePrefix.Parse(Value);

    /// <inheritdoc/>
    public int CompareTo(JournalStorageId? other) => other is null ? 1 : Comparer.Compare(Value, other.Value);

    /// <inheritdoc/>
    public bool Equals(JournalStorageId? other) => other is not null && Comparer.Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is JournalStorageId other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Comparer.GetHashCode(Value);

    /// <inheritdoc/>
    public override string ToString() => Value;

    /// <summary>
    /// Compares two journal storage ids for equality.
    /// </summary>
    public static bool operator ==(JournalStorageId? left, JournalStorageId? right) => Equals(left, right);

    /// <summary>
    /// Compares two journal storage ids for inequality.
    /// </summary>
    public static bool operator !=(JournalStorageId? left, JournalStorageId? right) => !Equals(left, right);

    private static string EncodeSegment(string segment, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segment, parameterName);
        if (segment is "." or "..")
        {
            throw new ArgumentException("Journal storage id segments must not be '.' or '..'.", parameterName);
        }

        if (segment.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("Journal storage id segments must not contain null characters.", parameterName);
        }

        return Uri.EscapeDataString(segment);
    }
}
