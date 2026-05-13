using System.Buffers;
using Orleans.Serialization.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Base class for format-owned state journal batch writers.
/// </summary>
/// <remarks>
/// Derived classes own the physical framing and committed buffer. This base class only
/// connects <see cref="JournalStreamWriter"/> and active entry payload writes to the derived
/// payload writer and entry lifecycle hooks. Buffers returned by <see cref="GetCommittedBuffer"/>
/// must remain valid for the caller's lifetime even if <see cref="Reset"/> is called before the caller
/// disposes the returned buffer.
/// </remarks>
public abstract class JournalWriter : IDisposable, IBufferWriter<byte>
{
    private JournalStreamId _activeStreamId;
    private int _activeEntryStart;
    private bool _entryIsActive;
    private bool _entryCompleted;

    /// <summary>
    /// Initializes a new instance of the <see cref="JournalWriter"/> class.
    /// </summary>
    protected JournalWriter()
    {
    }

    /// <summary>
    /// Gets the number of bytes currently buffered by this writer, including any active uncommitted entry payload.
    /// </summary>
    public long Length => checked(CommittedLength + (_entryIsActive ? ActivePayloadLength : 0));

    /// <summary>
    /// Creates a writer for entries belonging to <paramref name="streamId"/>.
    /// </summary>
    /// <param name="streamId">The durable state id.</param>
    /// <returns>A journal stream writer for the specified state.</returns>
    public JournalStreamWriter CreateJournalStreamWriter(JournalStreamId streamId) => new(streamId, this);

    /// <summary>
    /// Gets a borrowed buffer containing only committed bytes which are safe to persist.
    /// </summary>
    /// <remarks>
    /// The returned buffer must remain valid for the caller's lifetime even if <see cref="Reset"/> is subsequently called.
    /// </remarks>
    /// <returns>An <see cref="ArcBuffer"/> containing the committed journal bytes. The caller owns and must dispose the returned buffer.</returns>
    /// <exception cref="InvalidOperationException">An entry is currently active and has not been committed or aborted.</exception>
    public ArcBuffer GetCommittedBuffer()
    {
        ThrowIfEntryActive();
        return GetCommittedBufferCore();
    }

    /// <summary>
    /// Clears all buffered data so the writer can be reused for another batch.
    /// </summary>
    /// <remarks>This must not invalidate buffers previously returned by <see cref="GetCommittedBuffer"/>.</remarks>
    public void Reset()
    {
        ThrowIfEntryActive();
        ResetCore();
    }

    /// <summary>
    /// Releases resources used by this writer.
    /// </summary>
    public abstract void Dispose();

    /// <inheritdoc/>
    void IBufferWriter<byte>.Advance(int count) => GetActiveEntryWriter().AdvancePayload(count);

    /// <inheritdoc/>
    Memory<byte> IBufferWriter<byte>.GetMemory(int sizeHint) => GetActiveEntryWriter().GetPayloadMemory(sizeHint);

    /// <inheritdoc/>
    Span<byte> IBufferWriter<byte>.GetSpan(int sizeHint) => GetActiveEntryWriter().GetPayloadSpan(sizeHint);

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

    internal JournalEntryScope BeginEntry(JournalStreamId streamId)
    {
        if (_entryIsActive)
        {
            throw new InvalidOperationException("The journal batch already has an active entry.");
        }

        OnBeginEntry(streamId);
        var entryStart = GetEntryStart(streamId);
        _activeStreamId = streamId;
        _activeEntryStart = entryStart;
        _entryIsActive = true;
        _entryCompleted = false;
        return new(this, entryStart);
    }

    internal void CommitEntryWrite(int entryStart) => CommitActiveEntry(entryStart);

    internal void AbortEntryWrite(int entryStart) => AbortActiveEntry(entryStart);

    internal void AppendFormattedEntry(JournalStreamId streamId, IFormattedJournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (_entryIsActive)
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
        _entryIsActive = false;
        _entryCompleted = true;
    }

    private void ThrowIfEntryActive()
    {
        if (_entryIsActive)
        {
            throw new InvalidOperationException("The journal batch has an active entry.");
        }
    }

    private JournalWriter GetActiveEntryWriter()
    {
        if (!_entryIsActive)
        {
            if (_entryCompleted)
            {
                throw new InvalidOperationException("The journal entry has already completed.");
            }

            throw new InvalidOperationException("The journal writer is not writing an entry.");
        }

        return this;
    }
}
