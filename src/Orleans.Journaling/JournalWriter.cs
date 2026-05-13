using System.Buffers;
using Orleans.Serialization.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Base class for format-owned state journal writers.
/// </summary>
/// <remarks>
/// Derived classes own the physical framing and committed buffer. This base class only
/// connects <see cref="JournalStreamWriter"/> to entry lifecycle hooks. Buffers returned by <see cref="GetCommittedBuffer"/>
/// are pinned, caller-owned snapshots which must remain valid for the caller's lifetime even if
/// <see cref="Reset"/> or <see cref="Dispose"/> is called before the caller disposes the returned buffer.
/// </remarks>
public abstract class JournalWriter : IDisposable
{
    private bool _hasActiveEntry;

    /// <summary>
    /// Initializes a new instance of the <see cref="JournalWriter"/> class.
    /// </summary>
    protected JournalWriter()
    {
    }

    /// <summary>
    /// Creates a writer for entries belonging to <paramref name="streamId"/>.
    /// </summary>
    /// <param name="streamId">The durable state id.</param>
    /// <returns>A journal stream writer for the specified state.</returns>
    public JournalStreamWriter CreateJournalStreamWriter(JournalStreamId streamId) => new(streamId, this);

    /// <summary>
    /// Gets a pinned buffer containing only committed bytes which are safe to persist.
    /// </summary>
    /// <remarks>
    /// The returned buffer must remain valid for the caller's lifetime even if <see cref="Reset"/> or
    /// <see cref="Dispose"/> is subsequently called.
    /// </remarks>
    /// <returns>
    /// An <see cref="ArcBuffer"/> containing the committed journal bytes. The caller owns and must dispose the returned buffer.
    /// </returns>
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

    /// <summary>
    /// Returns a pinned buffer containing the committed bytes of the batch.
    /// </summary>
    /// <remarks>
    /// Called by <see cref="GetCommittedBuffer"/> after verifying that no entry is active. The returned buffer
    /// must remain valid for the caller's lifetime even if <see cref="Reset"/> or <see cref="Dispose"/> is subsequently called.
    /// </remarks>
    /// <returns>
    /// An <see cref="ArcBuffer"/> containing the committed bytes. The caller owns and must dispose the returned buffer.
    /// </returns>
    protected abstract ArcBuffer GetCommittedBufferCore();

    /// <summary>
    /// Discards all buffered data so the writer can be reused.
    /// </summary>
    /// <remarks>Called by <see cref="Reset"/> after verifying that no entry is active.</remarks>
    protected abstract void ResetCore();

    /// <summary>
    /// Begins a format-owned entry and returns the entry payload writer.
    /// </summary>
    /// <param name="streamId">The durable state id.</param>
    /// <returns>The entry payload writer.</returns>
    protected abstract IBufferWriter<byte> BeginEntryCore(JournalStreamId streamId);

    /// <summary>
    /// Appends a format-owned entry for retired or unknown state preservation.
    /// </summary>
    /// <param name="streamId">The durable state id.</param>
    /// <param name="operation">The preserved operation.</param>
    protected virtual void OnAppendPreservedOperation(JournalStreamId streamId, IPreservedJournalOperation operation)
    {
        throw new InvalidOperationException(
            $"The journal writer '{GetType().FullName}' cannot append preserved operation of type '{operation.GetType().FullName}'.");
    }

    /// <summary>
    /// Commits the active entry.
    /// </summary>
    /// <param name="streamId">The durable state id.</param>
    protected abstract void CommitEntry(JournalStreamId streamId);

    /// <summary>
    /// Aborts the active entry.
    /// </summary>
    /// <param name="streamId">The durable state id.</param>
    protected abstract void AbortEntry(JournalStreamId streamId);

    internal JournalEntryScope BeginEntry(JournalStreamId streamId)
    {
        if (_hasActiveEntry)
        {
            throw new InvalidOperationException("The journal writer already has an active entry.");
        }

        var payloadWriter = BeginEntryCore(streamId) ?? throw new InvalidOperationException(
            $"The journal writer '{GetType().FullName}' returned a null payload writer.");
        _hasActiveEntry = true;
        return new(this, streamId, payloadWriter);
    }

    internal void AppendPreservedOperation(JournalStreamId streamId, IPreservedJournalOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (_hasActiveEntry)
        {
            throw new InvalidOperationException("The journal writer already has an active entry.");
        }

        OnAppendPreservedOperation(streamId, operation);
    }

    internal void CommitActiveEntry(JournalStreamId streamId)
    {
        ThrowIfNoActiveEntry();
        try
        {
            CommitEntry(streamId);
        }
        finally
        {
            _hasActiveEntry = false;
        }
    }

    internal void AbortActiveEntry(JournalStreamId streamId)
    {
        ThrowIfNoActiveEntry();
        try
        {
            AbortEntry(streamId);
        }
        finally
        {
            _hasActiveEntry = false;
        }
    }

    private void ThrowIfEntryActive()
    {
        if (_hasActiveEntry)
        {
            throw new InvalidOperationException("The journal writer has an active entry.");
        }
    }

    private void ThrowIfNoActiveEntry()
    {
        if (!_hasActiveEntry)
        {
            throw new InvalidOperationException("The journal writer is not writing an entry.");
        }
    }
}
