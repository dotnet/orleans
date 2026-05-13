using System.Buffers;
using Orleans.Serialization.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Base class for format-owned state journal batch writers.
/// </summary>
/// <remarks>
/// Derived classes own the physical framing and committed buffer. This base class only
/// connects <see cref="JournalStreamWriter"/> and <see cref="JournalEntryWriter"/> to the
/// derived payload writer and entry lifecycle hooks. Buffers returned by <see cref="GetCommittedBuffer"/>
/// must remain valid for the caller's lifetime even if <see cref="Reset"/> is called before the caller
/// disposes the returned buffer.
/// </remarks>
public abstract class JournalBatchWriterBase : IJournalBatchWriter
{
    private readonly JournalEntryWriter _entryWriter = new();
    private JournalStreamId _activeStreamId;
    private int _activeEntryStart;

    /// <summary>
    /// Initializes a new instance of the <see cref="JournalBatchWriterBase"/> class.
    /// </summary>
    protected JournalBatchWriterBase()
    {
    }

    /// <inheritdoc/>
    public long Length => checked(CommittedLength + (_entryWriter.IsActive ? ActivePayloadLength : 0));

    /// <inheritdoc/>
    public JournalStreamWriter CreateJournalStreamWriter(JournalStreamId streamId) => new(streamId, this);

    /// <inheritdoc/>
    public ArcBuffer GetCommittedBuffer()
    {
        ThrowIfEntryActive();
        return GetCommittedBufferCore();
    }

    /// <inheritdoc/>
    public void Reset()
    {
        ThrowIfEntryActive();
        ResetCore();
    }

    /// <inheritdoc/>
    public abstract void Dispose();

    /// <summary>
    /// Gets the number of bytes committed to the batch, excluding any active entry payload.
    /// </summary>
    protected abstract long CommittedLength { get; }

    /// <summary>
    /// Gets the number of payload bytes written to the active entry.
    /// </summary>
    /// <remarks>Only consulted while an entry is active.</remarks>
    protected abstract long ActivePayloadLength { get; }

    /// <summary>
    /// Returns a borrowed buffer containing the committed bytes of the batch.
    /// </summary>
    /// <remarks>
    /// Called by <see cref="GetCommittedBuffer"/> after verifying that no entry is active. The returned buffer
    /// must remain valid for the caller's lifetime even if <see cref="Reset"/> is subsequently called.
    /// </remarks>
    /// <returns>An <see cref="ArcBuffer"/> containing the committed bytes. The caller owns and must dispose the returned buffer.</returns>
    protected abstract ArcBuffer GetCommittedBufferCore();

    /// <summary>
    /// Discards all buffered data so the writer can be reused.
    /// </summary>
    /// <remarks>Called by <see cref="Reset"/> after verifying that no entry is active.</remarks>
    protected abstract void ResetCore();

    /// <summary>
    /// Called before an entry scope becomes active.
    /// </summary>
    /// <param name="streamId">The durable state id.</param>
    protected virtual void OnBeginEntry(JournalStreamId streamId)
    {
    }

    /// <summary>
    /// Gets the format-owned entry start marker used to validate completion.
    /// </summary>
    /// <param name="streamId">The durable state id.</param>
    /// <returns>The entry start marker.</returns>
    protected abstract int GetEntryStart(JournalStreamId streamId);

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
    /// Appends a format-owned entry for retired or unknown state preservation.
    /// </summary>
    /// <param name="streamId">The durable state id.</param>
    /// <param name="entry">The format-owned entry.</param>
    protected virtual void OnAppendFormattedEntry(JournalStreamId streamId, IFormattedJournalEntry entry)
    {
        throw new InvalidOperationException(
            $"The journal batch writer '{GetType().FullName}' cannot append formatted entry of type '{entry.GetType().FullName}'.");
    }

    /// <summary>
    /// Commits the active entry.
    /// </summary>
    /// <param name="streamId">The durable state id.</param>
    /// <param name="entryStart">The entry start marker returned by <see cref="GetEntryStart"/>.</param>
    protected abstract void CommitEntry(JournalStreamId streamId, int entryStart);

    /// <summary>
    /// Aborts the active entry.
    /// </summary>
    /// <param name="streamId">The durable state id.</param>
    /// <param name="entryStart">The entry start marker returned by <see cref="GetEntryStart"/>.</param>
    protected abstract void AbortEntry(JournalStreamId streamId, int entryStart);

    internal JournalEntryWriter BeginEntry(JournalStreamId streamId)
    {
        if (_entryWriter.IsActive)
        {
            throw new InvalidOperationException("The journal batch already has an active entry.");
        }

        OnBeginEntry(streamId);
        var entryStart = GetEntryStart(streamId);
        _activeStreamId = streamId;
        _activeEntryStart = entryStart;
        _entryWriter.Initialize(this, entryStart);
        return _entryWriter;
    }

    internal void AdvanceEntryPayload(int count) => AdvancePayload(count);

    internal Memory<byte> GetEntryPayloadMemory(int sizeHint) => GetPayloadMemory(sizeHint);

    internal Span<byte> GetEntryPayloadSpan(int sizeHint) => GetPayloadSpan(sizeHint);

    internal void WriteEntryPayload(ReadOnlySpan<byte> value) => WritePayload(value);

    internal void WriteEntryPayload(ReadOnlySequence<byte> value) => WritePayload(value);

    internal void CommitEntryWrite(int entryStart) => CommitActiveEntry(entryStart);

    internal void AbortEntryWrite(int entryStart) => AbortActiveEntry(entryStart);

    internal void AppendFormattedEntry(JournalStreamId streamId, IFormattedJournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (_entryWriter.IsActive)
        {
            throw new InvalidOperationException("The journal batch already has an active entry.");
        }

        OnAppendFormattedEntry(streamId, entry);
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
            throw new InvalidOperationException("The journal entry start does not match the active entry.");
        }
    }

    private void ClearActiveEntry()
    {
        _activeEntryStart = 0;
        _activeStreamId = default;
    }

    private void ThrowIfEntryActive()
    {
        if (_entryWriter.IsActive)
        {
            throw new InvalidOperationException("The journal batch has an active entry.");
        }
    }
}
