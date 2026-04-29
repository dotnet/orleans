namespace Orleans.Journaling;

/// <summary>
/// Represents the lexical write scope for one state machine log entry.
/// </summary>
/// <remarks>
/// Call <see cref="Commit"/> after successfully writing the entry payload. If the scope is
/// disposed before it is committed, the pending entry is aborted.
/// </remarks>
public ref struct StateMachineLogEntry
{
    private LogEntryWriter? _writer;
    private bool _completed;

    internal StateMachineLogEntry(LogEntryWriter writer)
    {
        _writer = writer;
        _completed = false;
    }

    /// <summary>
    /// Gets the payload writer for this entry.
    /// </summary>
    public LogEntryWriter Writer => _writer ?? throw new InvalidOperationException(
        _completed ? "The log entry has already completed." : "The log entry scope is not active.");

    /// <summary>
    /// Commits the pending entry, making it visible to storage.
    /// </summary>
    /// <exception cref="InvalidOperationException">The entry has already completed.</exception>
    public void Commit()
    {
        if (_completed)
        {
            throw new InvalidOperationException("The log entry has already completed.");
        }

        var writer = Writer;
        writer.Commit();
        _completed = true;
        _writer = null;
    }

    /// <summary>
    /// Aborts the pending entry if it has not been committed.
    /// </summary>
    public void Dispose()
    {
        if (_completed)
        {
            return;
        }

        var writer = Writer;
        _completed = true;
        _writer = null;
        writer.Abort();
    }
}
