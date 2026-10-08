namespace Orleans.Journaling;

/// <summary>
/// Configures the journal history retained by volatile storage between snapshots.
/// </summary>
public sealed class VolatileJournalStorageOptions
{
    /// <summary>
    /// Gets or sets the number of successful appends after which storage requests a snapshot.
    /// The default is 100. The value must be positive.
    /// </summary>
    public int MaxAppendsBeforeSnapshot { get; set; } = 100;

    /// <summary>
    /// Gets or sets the appended byte count after which storage requests a snapshot.
    /// The default is 1 MiB (1,048,576 bytes). The value must be positive.
    /// </summary>
    /// <remarks>
    /// Storage requests a snapshot when either limit is reached. Snapshot bytes are excluded
    /// from the appended byte count. A successful snapshot or deletion resets both counters.
    /// The state manager checks the request before its next write, so an individual append can exceed this limit.
    /// </remarks>
    public long MaxBytesBeforeSnapshot { get; set; } = 1024 * 1024;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxAppendsBeforeSnapshot);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxBytesBeforeSnapshot);
    }
}
