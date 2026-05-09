namespace Orleans.Journaling;

/// <summary>
/// Writes journal entries for one state into the current journal batch.
/// </summary>
/// <remarks>
/// This type does not write to storage directly. Entries are buffered by the owning
/// <see cref="IJournalBatchWriter"/> or by the current batch writer owned by the journal manager.
/// </remarks>
public readonly struct JournalStreamWriter
{
    private readonly JournalStreamId _id;
    private readonly IJournalStreamWriterTarget? _target;

    internal JournalStreamWriter(JournalStreamId id, IJournalStreamWriterTarget target)
    {
        _id = id;
        _target = target;
    }

    /// <summary>
    /// Begins writing one journal entry for this writer's state.
    /// </summary>
    /// <returns>A lexical entry scope. Dispose the returned value to abort the entry if <see cref="JournalEntry.Commit"/> is not called.</returns>
    public JournalEntry BeginEntry() => BeginEntry(completion: null);

    internal bool IsInitialized => _target is not null;

    internal JournalEntry BeginEntry(IJournalEntryWriterCompletion? completion) => new(BeginEntryWriter(completion));

    internal JournalEntryWriter BeginEntryWriter(IJournalEntryWriterCompletion? completion) => GetTarget().BeginEntry(_id, completion);

    /// <summary>
    /// Appends a format-owned entry for retired or unknown state preservation.
    /// </summary>
    /// <param name="entry">The format-owned entry.</param>
    internal void AppendFormattedEntry(IFormattedJournalEntry entry) => GetTarget().AppendFormattedEntry(_id, entry);

    /// <summary>
    /// Attempts to append a format-owned entry directly to this writer.
    /// </summary>
    /// <param name="entry">The format-owned entry.</param>
    /// <returns><see langword="true"/> if the writer accepted <paramref name="entry"/>; otherwise, <see langword="false"/>.</returns>
    public bool TryAppendFormattedEntry(IFormattedJournalEntry entry) => GetTarget().TryAppendFormattedEntry(_id, entry);

    private IJournalStreamWriterTarget GetTarget()
    {
        if (_target is null)
        {
            throw new InvalidOperationException("The state journal stream writer is not initialized.");
        }

        return _target;
    }
}

internal interface IJournalStreamWriterTarget
{
    JournalEntryWriter BeginEntry(JournalStreamId streamId, IJournalEntryWriterCompletion? completion);

    void AppendFormattedEntry(JournalStreamId streamId, IFormattedJournalEntry entry);

    bool TryAppendFormattedEntry(JournalStreamId streamId, IFormattedJournalEntry entry);
}
