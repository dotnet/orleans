using System.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Writes log entries for one state machine into the current log segment.
/// </summary>
/// <remarks>
/// This type does not write to storage directly. Entries are buffered by the owning
/// <see cref="ILogSegmentWriter"/> or by the current segment writer owned by the log manager.
/// </remarks>
public readonly struct LogWriter
{
    private readonly LogStreamId _id;
    private readonly ILogWriterTarget? _target;

    internal LogWriter(LogStreamId id, ILogWriterTarget target)
    {
        _id = id;
        _target = target;
    }

    /// <summary>
    /// Begins writing one log entry for this writer's state machine.
    /// </summary>
    /// <returns>A lexical entry scope. Dispose the returned value to abort the entry if <see cref="LogEntry.Commit"/> is not called.</returns>
    public LogEntry BeginEntry() => BeginEntry(completion: null);

    internal bool IsInitialized => _target is not null;

    internal LogEntry BeginEntry(ILogEntryWriterCompletion? completion) => new(BeginEntryWriter(completion));

    internal LogEntryWriter BeginEntryWriter(ILogEntryWriterCompletion? completion) => GetTarget().BeginEntry(_id, completion);

    /// <summary>
    /// Appends a format-owned entry for retired or unknown state-machine preservation.
    /// </summary>
    /// <param name="entry">The format-owned entry.</param>
    internal void AppendFormattedEntry(IFormattedLogEntry entry) => GetTarget().AppendFormattedEntry(_id, entry);

    /// <summary>
    /// Attempts to append a format-owned entry directly to this writer.
    /// </summary>
    /// <param name="entry">The format-owned entry.</param>
    /// <returns><see langword="true"/> if the writer accepted <paramref name="entry"/>; otherwise, <see langword="false"/>.</returns>
    public bool TryAppendFormattedEntry(IFormattedLogEntry entry) => GetTarget().TryAppendFormattedEntry(_id, entry);

    private ILogWriterTarget GetTarget()
    {
        if (_target is null)
        {
            throw new InvalidOperationException("The state machine log writer is not initialized.");
        }

        return _target;
    }
}

internal interface ILogWriterTarget
{
    LogEntryWriter BeginEntry(LogStreamId streamId, ILogEntryWriterCompletion? completion);

    void AppendFormattedEntry(LogStreamId streamId, IFormattedLogEntry entry);

    bool TryAppendFormattedEntry(LogStreamId streamId, IFormattedLogEntry entry);
}
