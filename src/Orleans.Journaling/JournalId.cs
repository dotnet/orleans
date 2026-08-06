namespace Orleans.Journaling;

/// <summary>
/// Identifies a journal independently of any grain activation.
/// </summary>
public readonly struct JournalId : IEquatable<JournalId>
{
    private const char Separator = '/';

    /// <summary>
    /// Initializes a new instance of the <see cref="JournalId"/> struct.
    /// </summary>
    /// <param name="value">The stable journal identifier.</param>
    public JournalId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    /// <summary>
    /// Gets the stable journal identifier.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Gets a value indicating whether this instance is the default value.
    /// </summary>
    public bool IsDefault => Value is null;

    /// <summary>
    /// Creates a journal id from a grain id.
    /// </summary>
    /// <param name="grainId">The grain id.</param>
    /// <returns>The journal id.</returns>
    public static JournalId FromGrainId(GrainId grainId)
    {
        if (grainId.IsDefault)
        {
            throw new ArgumentException("The grain id must not be the default value.", nameof(grainId));
        }

        return new(grainId.ToString());
    }

    /// <summary>
    /// Creates a journal id from decoded hierarchical segments.
    /// </summary>
    /// <param name="firstSegment">The first id segment.</param>
    /// <param name="additionalSegments">Additional id segments.</param>
    /// <returns>The normalized journal id.</returns>
    public static JournalId Create(string firstSegment, params ReadOnlySpan<string> additionalSegments)
    {
        var encodedSegments = new string[additionalSegments.Length + 1];
        encodedSegments[0] = EncodeSegment(firstSegment, nameof(firstSegment));
        for (var i = 0; i < additionalSegments.Length; i++)
        {
            encodedSegments[i + 1] = EncodeSegment(additionalSegments[i], nameof(additionalSegments));
        }

        return new(string.Join(Separator, encodedSegments));
    }

    /// <summary>
    /// Creates a journal id from decoded hierarchical segments.
    /// </summary>
    /// <param name="segments">The id segments.</param>
    /// <returns>The normalized journal id.</returns>
    public static JournalId Create(IEnumerable<string> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        return Create(segments.ToArray().AsSpan());
    }

    /// <summary>
    /// Creates a journal id from decoded hierarchical segments.
    /// </summary>
    /// <param name="segments">The id segments.</param>
    /// <returns>The normalized journal id.</returns>
    public static JournalId Create(ReadOnlySpan<string> segments)
    {
        if (segments.Length == 0)
        {
            throw new ArgumentException("A journal id must contain at least one segment.", nameof(segments));
        }

        var encodedSegments = new string[segments.Length];
        for (var i = 0; i < segments.Length; i++)
        {
            encodedSegments[i] = EncodeSegment(segments[i], nameof(segments));
        }

        return new(string.Join(Separator, encodedSegments));
    }

    /// <summary>
    /// Determines whether this id is a prefix of <paramref name="journalId"/>.
    /// </summary>
    /// <param name="journalId">The journal id to test.</param>
    /// <returns><see langword="true"/> if this id is the default value, equals <paramref name="journalId"/>, or identifies an ancestor segment.</returns>
    public bool IsPrefixOf(JournalId journalId)
    {
        if (IsDefault)
        {
            return true;
        }

        if (journalId.IsDefault)
        {
            return false;
        }

        return string.Equals(journalId.Value, Value, StringComparison.Ordinal)
            || journalId.Value.StartsWith(Value + Separator, StringComparison.Ordinal);
    }

    /// <inheritdoc/>
    public override string ToString() => Value ?? string.Empty;

    /// <inheritdoc/>
    public bool Equals(JournalId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is JournalId other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value ?? string.Empty);

    /// <summary>
    /// Compares two journal ids for equality.
    /// </summary>
    /// <param name="left">The first journal id.</param>
    /// <param name="right">The second journal id.</param>
    /// <returns><see langword="true"/> if the journal ids are equal; otherwise, <see langword="false"/>.</returns>
    public static bool operator ==(JournalId left, JournalId right) => left.Equals(right);

    /// <summary>
    /// Compares two journal ids for inequality.
    /// </summary>
    /// <param name="left">The first journal id.</param>
    /// <param name="right">The second journal id.</param>
    /// <returns><see langword="true"/> if the journal ids are not equal; otherwise, <see langword="false"/>.</returns>
    public static bool operator !=(JournalId left, JournalId right) => !left.Equals(right);

    private static string EncodeSegment(string segment, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segment, parameterName);
        if (segment is "." or "..")
        {
            throw new ArgumentException("Journal id segments must not be '.' or '..'.", parameterName);
        }

        if (segment.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("Journal id segments must not contain null characters.", parameterName);
        }

        return Uri.EscapeDataString(segment);
    }
}
