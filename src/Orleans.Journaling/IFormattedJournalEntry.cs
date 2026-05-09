namespace Orleans.Journaling;

/// <summary>
/// A format-owned journal entry which can be copied forward without interpreting it.
/// </summary>
public interface IFormattedJournalEntry
{
    /// <summary>
    /// Gets the durable operation payload bytes for the formatted entry.
    /// </summary>
    ReadOnlyMemory<byte> Payload { get; }

    /// <summary>
    /// Applies this entry to <paramref name="stateMachine"/>.
    /// </summary>
    /// <param name="stateMachine">The target state machine.</param>
    void Apply(IDurableStateMachine stateMachine);
}

/// <summary>
/// Buffers format-owned journal entries for a stream which is not currently registered.
/// </summary>
public interface IFormattedJournalEntryBuffer
{
    /// <summary>
    /// Adds a formatted journal entry to the buffer.
    /// </summary>
    /// <param name="entry">The formatted journal entry.</param>
    void AddFormattedEntry(IFormattedJournalEntry entry);

    /// <summary>
    /// Gets the buffered formatted entries.
    /// </summary>
    IReadOnlyList<IFormattedJournalEntry> FormattedEntries { get; }
}
