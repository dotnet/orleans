namespace Orleans.Journaling;

/// <summary>
/// Writes journal entries for one state into the current journal batch.
/// </summary>
/// <remarks>
/// This type does not write to storage directly. Entries are buffered by the owning
/// <see cref="IJournalBatchWriter"/>.
/// </remarks>
public readonly struct JournalStreamWriter
{
    private readonly JournalStreamId _id;
    private readonly JournalBatchWriterBase? _writer;

    internal JournalStreamWriter(JournalStreamId id, JournalBatchWriterBase writer)
    {
        _id = id;
        _writer = writer;
    }

    /// <summary>
    /// Begins writing one journal entry for this writer's state.
    /// </summary>
    /// <returns>A lexical entry scope. Dispose the returned value to abort the entry if <see cref="JournalEntry.Commit"/> is not called.</returns>
    public JournalEntry BeginEntry() => new(BeginEntryWriter());

    internal bool IsInitialized => _writer is not null;

    internal JournalEntryWriter BeginEntryWriter() => GetWriter().BeginEntry(_id);

    /// <summary>
    /// Appends a format-owned entry for retired or unknown state preservation.
    /// </summary>
    /// <param name="entry">The format-owned entry.</param>
    internal void AppendFormattedEntry(IFormattedJournalEntry entry) => GetWriter().AppendFormattedEntry(_id, entry);

    private JournalBatchWriterBase GetWriter()
    {
        if (_writer is null)
        {
            throw new InvalidOperationException("The state journal stream writer is not initialized.");
        }

        return _writer;
    }
}
