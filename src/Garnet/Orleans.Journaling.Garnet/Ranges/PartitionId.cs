namespace Orleans.Journaling.Ranges;

/// <summary>
/// Identifies a partition: one store, with its own durable storage, which owns ranges of buckets.
/// </summary>
/// <remarks>
/// Partition ids are never reused, so state recorded under an id always refers to the same partition. An id has
/// 1 to 64 characters from <c>[A-Za-z0-9._-]</c> and starts with a letter or digit, so it can name files and keys.
/// </remarks>
public readonly record struct PartitionId : IComparable<PartitionId>
{
    /// <summary>The maximum length of a partition id.</summary>
    public const int MaxLength = 64;

    /// <summary>
    /// Initializes a new instance of the <see cref="PartitionId"/> struct.
    /// </summary>
    /// <param name="value">The partition id.</param>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not a valid partition id.</exception>
    public PartitionId(string value)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException(
                $"'{value}' is not a valid partition id. A partition id has 1 to {MaxLength} characters from [A-Za-z0-9._-] and starts with a letter or digit.",
                nameof(value));
        }

        Value = value;
    }

    /// <summary>Gets the partition id's value, or <see langword="null"/> for the default value.</summary>
    public string Value { get; }

    /// <summary>Gets a value indicating whether this is the default, invalid, value.</summary>
    public bool IsDefault => Value is null;

    /// <summary>Gets a value indicating whether <paramref name="value"/> is a valid partition id.</summary>
    /// <param name="value">The candidate value.</param>
    /// <returns><see langword="true"/> if <paramref name="value"/> is a valid partition id.</returns>
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength || !char.IsAsciiLetterOrDigit(value[0]))
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not '.' and not '_' and not '-')
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public int CompareTo(PartitionId other) => string.CompareOrdinal(Value, other.Value);

    /// <inheritdoc/>
    public bool Equals(PartitionId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override int GetHashCode() => Value is null ? 0 : StringComparer.Ordinal.GetHashCode(Value);

    /// <inheritdoc/>
    public override string ToString() => Value ?? string.Empty;
}
