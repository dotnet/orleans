using System.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Writes log entries for one state machine into the current log segment.
/// </summary>
/// <remarks>
/// This type does not write to storage directly. Storage only observes bytes returned by the
/// owning <see cref="ILogSegmentWriter"/>.
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

    internal LogEntry BeginEntry(ILogEntryWriterCompletion? completion) => new(GetTarget().BeginEntry(_id, completion));

    /// <summary>
    /// Appends an already decoded durable operation payload for retired or unknown state-machine preservation.
    /// </summary>
    /// <remarks>
    /// This helper preserves payload bytes which were decoded by the active log format during recovery.
    /// It is not a normal durable write convenience API and does not accept physical log-entry bytes.
    /// </remarks>
    internal void AppendPreservedDecodedPayload(byte[] value) => AppendPreservedDecodedPayload((ReadOnlySpan<byte>)value);

    /// <summary>
    /// Appends an already decoded durable operation payload for retired or unknown state-machine preservation.
    /// </summary>
    /// <remarks>
    /// This helper preserves payload bytes which were decoded by the active log format during recovery.
    /// It is not a normal durable write convenience API and does not accept physical log-entry bytes.
    /// </remarks>
    internal void AppendPreservedDecodedPayload(ReadOnlySpan<byte> value)
    {
        using var entry = BeginEntry();
        entry.Writer.Write(value);
        entry.Commit();
    }

    /// <summary>
    /// Appends an already decoded durable operation payload for retired or unknown state-machine preservation.
    /// </summary>
    /// <remarks>
    /// This helper preserves payload bytes which were decoded by the active log format during recovery.
    /// It is not a normal durable write convenience API and does not accept physical log-entry bytes.
    /// </remarks>
    internal void AppendPreservedDecodedPayload(ReadOnlySequence<byte> value)
    {
        using var entry = BeginEntry();
        entry.Writer.Write(value);
        entry.Commit();
    }

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
}
