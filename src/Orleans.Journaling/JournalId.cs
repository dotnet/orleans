namespace Orleans.Journaling;

/// <summary>
/// Identifies a journal independently of any grain activation.
/// </summary>
public readonly struct JournalId : IEquatable<JournalId>
{
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
}
