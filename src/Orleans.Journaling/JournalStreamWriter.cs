namespace Orleans.Journaling;

/// <summary>
/// Writes journal entries for one state into the current journal batch.
/// </summary>
/// <remarks>
/// This type does not write to storage directly. Entries are buffered by the owning
/// <see cref="JournalBufferWriter"/>.
/// </remarks>
public readonly struct JournalStreamWriter
{
    private readonly JournalStreamId _id;
    private readonly JournalBufferWriter? _writer;

    internal JournalStreamWriter(JournalStreamId id, JournalBufferWriter writer)
    {
        _id = id;
        _writer = writer;
    }

    /// <summary>
    /// Begins writing one journal entry for this writer's state.
    /// </summary>
    /// <returns>A lexical entry scope. Dispose the returned value to abort the entry if <see cref="JournalEntryScope.Commit"/> is not called.</returns>
    public JournalEntryScope BeginEntry() => GetWriter().BeginEntry(_id);

    internal bool IsInitialized => _writer is not null;

    /// <summary>
    /// Appends a format-owned entry for retired or unknown state preservation.
    /// </summary>
    /// <param name="entry">The format-owned entry.</param>
    internal void AppendPreservedEntry(IPreservedJournalEntry entry) => GetWriter().AppendPreservedEntry(_id, entry);

    private JournalBufferWriter GetWriter()
    {
        if (_writer is null)
        {
            throw new InvalidOperationException("The state journal stream writer is not initialized.");
        }

        return _writer;
    }
}
