using System.Buffers;
using Orleans.Serialization.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Base class for in-memory journal buffers used to assemble a pending journal batch.
/// </summary>
/// <remarks>
/// Derived classes own physical entry framing. This base class owns the committed and active-entry
/// buffers so that callers can flush a committed prefix while another entry is being written.
/// Buffers returned by <see cref="GetBuffer"/> are pinned, caller-owned snapshots which must remain
/// valid for the caller's lifetime even if <see cref="Reset"/> or <see cref="Dispose"/> is called
/// before the caller disposes the returned buffer.
/// Despite the name, this type does not perform storage I/O; it accumulates encoded journal entries until
/// callers hand the buffer off to <see cref="IJournalStorage"/>.
/// </remarks>
public abstract class JournalBufferWriter : IDisposable
{
#if NET9_0_OR_GREATER
    private readonly Lock _lock = new();
#else
    private readonly object _lock = new();
#endif
    private readonly ArcBufferWriter _committedBuffer = new();
    private readonly ArcBufferWriter _activeEntryBuffer = new();
    private bool _hasActiveEntry;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="JournalBufferWriter"/> class.
    /// </summary>
    protected JournalBufferWriter()
    {
    }

    /// <summary>
    /// Creates a writer for entries belonging to <paramref name="streamId"/>.
    /// </summary>
    /// <param name="streamId">The durable state id.</param>
    /// <returns>A journal stream writer for the specified state.</returns>
    public JournalStreamWriter CreateJournalStreamWriter(JournalStreamId streamId) => new(streamId, this);

    /// <summary>
    /// Gets a pinned snapshot of the accumulated journal bytes which are safe to persist.
    /// </summary>
    /// <remarks>
    /// The returned buffer must remain valid for the caller's lifetime even if <see cref="Reset"/> or
    /// <see cref="Dispose"/> is subsequently called. If an entry is currently in flight, it is excluded from
    /// the returned buffer.
    /// </remarks>
    /// <returns>
    /// An <see cref="ArcBuffer"/> containing the accumulated journal bytes. The caller owns and must dispose the returned buffer.
    /// </returns>
    public ArcBuffer GetBuffer() => GetCommittedBuffer();

    /// <summary>
    /// Clears all buffered data so the writer can be reused for another batch.
    /// </summary>
    /// <remarks>This must not invalidate buffers previously returned by <see cref="GetBuffer"/>.</remarks>
    public void Reset()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            ThrowIfEntryActive();
            _committedBuffer.Reset();
            _activeEntryBuffer.Reset();
        }
    }

    /// <summary>
    /// Releases resources used by this writer.
    /// </summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _activeEntryBuffer.Dispose();
            _committedBuffer.Dispose();
            _disposed = true;
        }
    }

    /// <summary>
    /// Gets the number of committed bytes.
    /// </summary>
    protected int CommittedLength
    {
        get
        {
            lock (_lock)
            {
                ThrowIfDisposed();
                return _committedBuffer.Length;
            }
        }
    }

    /// <summary>
    /// Gets the number of bytes in the active entry payload.
    /// </summary>
    protected int ActiveEntryLength
    {
        get
        {
            lock (_lock)
            {
                ThrowIfDisposed();
                return _activeEntryBuffer.Length;
            }
        }
    }

    /// <summary>
    /// Gets the total number of bytes held by this writer.
    /// </summary>
    protected int BufferedLength
    {
        get
        {
            lock (_lock)
            {
                ThrowIfDisposed();
                return checked(_committedBuffer.Length + _activeEntryBuffer.Length);
            }
        }
    }

    /// <summary>
    /// Encodes a complete journal entry into <paramref name="output"/>.
    /// </summary>
    /// <param name="streamId">The durable state id.</param>
    /// <param name="payload">The entry payload.</param>
    /// <param name="output">The committed output buffer.</param>
    protected abstract void WriteEntry(JournalStreamId streamId, ReadOnlySequence<byte> payload, IBufferWriter<byte> output);

    /// <summary>
    /// Appends a format-owned entry for retired or unknown state preservation.
    /// </summary>
    /// <param name="streamId">The durable state id.</param>
    /// <param name="entry">The preserved entry.</param>
    /// <param name="output">The committed output buffer.</param>
    protected virtual void WritePreservedEntry(JournalStreamId streamId, IPreservedJournalEntry entry, IBufferWriter<byte> output)
    {
        throw new InvalidOperationException(
            $"The journal buffer writer '{GetType().FullName}' cannot append preserved entry of type '{entry.GetType().FullName}'.");
    }

    internal bool HasActiveEntry
    {
        get
        {
            lock (_lock)
            {
                ThrowIfDisposed();
                return _hasActiveEntry;
            }
        }
    }

    internal ArcBuffer GetCommittedBuffer()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            return _committedBuffer.PeekSlice(_committedBuffer.Length);
        }
    }

    internal void Consume(ArcBuffer buffer)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            ValidateCommittedPrefix(buffer);
            _committedBuffer.AdvanceReader(buffer.Length);
        }
    }

    internal JournalEntryScope BeginEntry(JournalStreamId streamId)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            if (_hasActiveEntry)
            {
                throw new InvalidOperationException("The journal buffer writer already has an active entry.");
            }

            _activeEntryBuffer.Reset();
            _hasActiveEntry = true;
            return new(this, streamId, _activeEntryBuffer);
        }
    }

    internal void AppendPreservedEntry(JournalStreamId streamId, IPreservedJournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_lock)
        {
            ThrowIfDisposed();
            if (_hasActiveEntry)
            {
                throw new InvalidOperationException("The journal buffer writer already has an active entry.");
            }

            var committedLength = _committedBuffer.Length;
            try
            {
                WritePreservedEntry(streamId, entry, _committedBuffer);
            }
            catch
            {
                _committedBuffer.Truncate(committedLength);
                throw;
            }
        }
    }

    internal void CommitActiveEntry(JournalStreamId streamId)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            ThrowIfNoActiveEntry();
            var committedLength = _committedBuffer.Length;
            try
            {
                using var payload = _activeEntryBuffer.PeekSlice(_activeEntryBuffer.Length);
                WriteEntry(streamId, payload.AsReadOnlySequence(), _committedBuffer);
            }
            catch
            {
                _committedBuffer.Truncate(committedLength);
                throw;
            }
            finally
            {
                _activeEntryBuffer.Reset();
                _hasActiveEntry = false;
            }
        }
    }

    internal void AbortActiveEntry(JournalStreamId streamId)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            ThrowIfNoActiveEntry();
            _activeEntryBuffer.Reset();
            _hasActiveEntry = false;
        }
    }

    private void ValidateCommittedPrefix(ArcBuffer buffer)
    {
        if (buffer.Length > _committedBuffer.Length)
        {
            throw new InvalidOperationException("The consumed journal buffer exceeds the current committed length.");
        }

        if (buffer.Length == 0)
        {
            return;
        }

        using var prefix = _committedBuffer.PeekSlice(buffer.Length);
        if (!ReferenceEquals(buffer.First, prefix.First) || buffer.Offset != prefix.Offset)
        {
            throw new InvalidOperationException("The consumed journal buffer is not the current committed prefix.");
        }
    }

    private void ThrowIfEntryActive()
    {
        if (_hasActiveEntry)
        {
            throw new InvalidOperationException("The journal buffer writer has an active entry.");
        }
    }

    private void ThrowIfNoActiveEntry()
    {
        if (!_hasActiveEntry)
        {
            throw new InvalidOperationException("The journal buffer writer is not writing an entry.");
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(GetType().FullName);
        }
    }
}
