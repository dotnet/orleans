using System.Buffers;
using Orleans.Serialization.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Base class for format-owned state machine log extent writers.
/// </summary>
/// <remarks>
/// Derived classes own the physical framing and committed buffer. This base class only
/// connects <see cref="StateMachineLogWriter"/> and <see cref="LogEntryWriter"/> to the
/// derived payload writer and entry completion hooks.
/// </remarks>
public abstract class StateMachineLogExtentWriterBase : IStateMachineLogExtentWriter
{
    private readonly LogEntryWriter _entryWriter = new();
    private readonly Target _target;
    private StateMachineId _activeStreamId;
    private int _activeEntryStart;

    /// <summary>
    /// Initializes a new instance of the <see cref="StateMachineLogExtentWriterBase"/> class.
    /// </summary>
    protected StateMachineLogExtentWriterBase() => _target = new(this);

    /// <inheritdoc/>
    public abstract long Length { get; }

    /// <summary>
    /// Gets a value indicating whether a log entry is currently active.
    /// </summary>
    protected bool IsEntryActive => _entryWriter.IsActive;

    /// <summary>
    /// Gets the stream id for the currently active entry.
    /// </summary>
    protected StateMachineId ActiveStreamId => _activeStreamId;

    /// <inheritdoc/>
    public StateMachineLogWriter CreateLogWriter(StateMachineId streamId) => new(streamId, _target);

    /// <inheritdoc/>
    public abstract ArcBuffer GetCommittedBuffer();

    /// <inheritdoc/>
    public abstract void Reset();

    /// <inheritdoc/>
    public abstract void Dispose();

    /// <summary>
    /// Called before an entry scope becomes active.
    /// </summary>
    /// <param name="streamId">The durable state machine id.</param>
    protected virtual void OnBeginEntry(StateMachineId streamId)
    {
    }

    /// <summary>
    /// Gets the format-owned entry start marker used to validate completion.
    /// </summary>
    /// <param name="streamId">The durable state machine id.</param>
    /// <returns>The entry start marker.</returns>
    protected abstract int GetEntryStart(StateMachineId streamId);

    /// <summary>
    /// Advances the payload writer.
    /// </summary>
    /// <param name="count">The number of bytes written.</param>
    protected abstract void AdvancePayload(int count);

    /// <summary>
    /// Gets writable memory for the payload.
    /// </summary>
    /// <param name="sizeHint">The requested minimum size.</param>
    /// <returns>Writable memory.</returns>
    protected abstract Memory<byte> GetPayloadMemory(int sizeHint);

    /// <summary>
    /// Gets a writable span for the payload.
    /// </summary>
    /// <param name="sizeHint">The requested minimum size.</param>
    /// <returns>A writable span.</returns>
    protected abstract Span<byte> GetPayloadSpan(int sizeHint);

    /// <summary>
    /// Writes payload bytes.
    /// </summary>
    /// <param name="value">The bytes to write.</param>
    protected abstract void WritePayload(ReadOnlySpan<byte> value);

    /// <summary>
    /// Writes payload bytes.
    /// </summary>
    /// <param name="value">The bytes to write.</param>
    protected abstract void WritePayload(ReadOnlySequence<byte> value);

    /// <summary>
    /// Commits the active entry.
    /// </summary>
    /// <param name="streamId">The durable state machine id.</param>
    /// <param name="entryStart">The entry start marker returned by <see cref="GetEntryStart"/>.</param>
    protected abstract void CommitEntry(StateMachineId streamId, int entryStart);

    /// <summary>
    /// Aborts the active entry.
    /// </summary>
    /// <param name="streamId">The durable state machine id.</param>
    /// <param name="entryStart">The entry start marker returned by <see cref="GetEntryStart"/>.</param>
    protected abstract void AbortEntry(StateMachineId streamId, int entryStart);

    private LogEntryWriter BeginEntry(StateMachineId streamId, ILogEntryWriterCompletion? completion)
    {
        if (_entryWriter.IsActive)
        {
            throw new InvalidOperationException("The log extent already has an active entry.");
        }

        OnBeginEntry(streamId);
        var entryStart = GetEntryStart(streamId);
        _activeStreamId = streamId;
        _activeEntryStart = entryStart;
        _entryWriter.Initialize(_target, entryStart, completion);
        return _entryWriter;
    }

    private void CommitActiveEntry(int entryStart)
    {
        ValidateEntryStart(entryStart);
        CommitEntry(_activeStreamId, entryStart);
        ClearActiveEntry();
    }

    private void AbortActiveEntry(int entryStart)
    {
        ValidateEntryStart(entryStart);
        try
        {
            AbortEntry(_activeStreamId, entryStart);
        }
        finally
        {
            ClearActiveEntry();
        }
    }

    private void ValidateEntryStart(int entryStart)
    {
        if (entryStart != _activeEntryStart)
        {
            throw new InvalidOperationException("The log entry start does not match the active entry.");
        }
    }

    private void ClearActiveEntry()
    {
        _activeEntryStart = 0;
        _activeStreamId = default;
    }

    private sealed class Target(StateMachineLogExtentWriterBase owner) : IStateMachineLogWriterTarget, ILogEntryWriterTarget
    {
        public void Advance(int count) => owner.AdvancePayload(count);

        public Memory<byte> GetMemory(int sizeHint = 0) => owner.GetPayloadMemory(sizeHint);

        public Span<byte> GetSpan(int sizeHint = 0) => owner.GetPayloadSpan(sizeHint);

        public void Write(ReadOnlySpan<byte> value) => owner.WritePayload(value);

        public void Write(ReadOnlySequence<byte> value) => owner.WritePayload(value);

        public void CommitEntry(int entryStart) => owner.CommitActiveEntry(entryStart);

        public void AbortEntry(int entryStart) => owner.AbortActiveEntry(entryStart);

        LogEntryWriter IStateMachineLogWriterTarget.BeginEntry(StateMachineId streamId, ILogEntryWriterCompletion? completion) =>
            owner.BeginEntry(streamId, completion);
    }
}
