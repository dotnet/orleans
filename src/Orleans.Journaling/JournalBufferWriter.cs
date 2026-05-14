using System.Buffers;
using Orleans.Serialization.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Base class for in-memory journal buffers used to assemble a pending journal batch.
/// </summary>
/// <remarks>
/// Derived classes own physical entry framing. This base class owns the buffer and committed-length
/// watermark so that callers can flush a committed prefix while another entry is being written.
/// Buffers returned by <see cref="GetBuffer"/> are pinned, caller-owned snapshots which must remain
/// valid for the caller's lifetime even if <see cref="Reset"/> or <see cref="Dispose"/> is called
/// before the caller disposes the returned buffer.
/// Despite the name, this type does not perform storage I/O; it accumulates encoded journal entries until
/// callers hand the buffer off to <see cref="IJournalStorage"/>.
/// </remarks>
public abstract class JournalBufferWriter : IDisposable, IBufferWriter<byte>
{
#if NET9_0_OR_GREATER
    private readonly Lock _lock = new();
#else
    private readonly object _lock = new();
#endif
    private readonly ArcBufferWriter _buffer = new();
    private int _committedLength;
    private bool _hasActiveEntry;
    private bool _disposed;

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
            _buffer.Reset();
            _committedLength = 0;
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

            _buffer.Dispose();
            _disposed = true;
        }
    }

    /// <summary>
    /// Gets the number of bytes in the active entry.
    /// </summary>
    protected int ActiveEntryLength
    {
        get
        {
            lock (_lock)
            {
                ThrowIfDisposed();
                return _hasActiveEntry ? checked(_buffer.Length - _committedLength) : 0;
            }
        }
    }

    /// <summary>
    /// Gets the current output writer.
    /// </summary>
    /// <remarks>This writer is only safe to use from journal buffer writer lifecycle methods.</remarks>
    protected IBufferWriter<byte> Output => _buffer;

    /// <summary>
    /// Patches format-owned framing bytes at the specified offset in the active entry.
    /// </summary>
    /// <param name="offset">The offset into the active entry.</param>
    /// <param name="value">The replacement bytes.</param>
    protected void WriteAt(int offset, ReadOnlySpan<byte> value)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            ThrowIfNoActiveEntry();
            var activeEntryLength = checked(_buffer.Length - _committedLength);
            if (offset < 0 || offset > activeEntryLength)
            {
                throw new ArgumentOutOfRangeException(nameof(offset), offset, "Offset must be within the active entry.");
            }

            if (value.Length > activeEntryLength - offset)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Value must fit within the active entry.");
            }

            _buffer.WriteAt(checked(_committedLength + offset), value);
        }
    }

    /// <summary>
    /// Gets a byte at the specified offset in the active entry.
    /// </summary>
    /// <param name="offset">The offset into the active entry.</param>
    protected byte GetEntryByte(int offset)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            ThrowIfNoActiveEntry();
            var activeEntryLength = checked(_buffer.Length - _committedLength);
            if (offset < 0 || offset >= activeEntryLength)
            {
                throw new ArgumentOutOfRangeException(nameof(offset), offset, "Offset must be within the active entry.");
            }

            return _buffer.Reader.Peek(checked(_committedLength + offset));
        }
    }

    /// <summary>
    /// Writes any format-owned entry prefix and prepares the payload writer.
    /// </summary>
    /// <param name="streamId">The durable state id.</param>
    protected virtual void StartEntry(JournalStreamId streamId)
    {
    }

    /// <summary>
    /// Writes any format-owned entry suffix or patches framing bytes.
    /// </summary>
    /// <param name="streamId">The durable state id.</param>
    protected abstract void FinishEntry(JournalStreamId streamId);

    /// <summary>
    /// Appends a format-owned entry for retired or unknown state preservation.
    /// </summary>
    /// <param name="streamId">The durable state id.</param>
    /// <param name="entry">The preserved entry.</param>
    protected virtual void WritePreservedEntry(JournalStreamId streamId, IPreservedJournalEntry entry)
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
            return _buffer.PeekSlice(_committedLength);
        }
    }

    internal void Consume(ArcBuffer buffer)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            ValidateCommittedPrefix(buffer);
            _buffer.AdvanceReader(buffer.Length);
            _committedLength -= buffer.Length;
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

            _hasActiveEntry = true;
            try
            {
                StartEntry(streamId);
                return new(this, streamId);
            }
            catch
            {
                _buffer.Truncate(_committedLength);
                ClearActiveEntry();
                throw;
            }
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

            var committedLength = _committedLength;
            try
            {
                WritePreservedEntry(streamId, entry);
                _committedLength = _buffer.Length;
            }
            catch
            {
                _buffer.Truncate(committedLength);
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
            try
            {
                FinishEntry(streamId);
                _committedLength = _buffer.Length;
            }
            catch
            {
                _buffer.Truncate(_committedLength);
                throw;
            }
            finally
            {
                ClearActiveEntry();
            }
        }
    }

    internal void AbortActiveEntry()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            ThrowIfNoActiveEntry();
            _buffer.Truncate(_committedLength);
            ClearActiveEntry();
        }
    }

    private void ValidateCommittedPrefix(ArcBuffer buffer)
    {
        if (buffer.Length > _committedLength)
        {
            throw new InvalidOperationException("The consumed journal buffer exceeds the current committed length.");
        }

        if (buffer.Length == 0)
        {
            return;
        }

        using var prefix = _buffer.PeekSlice(buffer.Length);
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

    private void ClearActiveEntry()
    {
        _hasActiveEntry = false;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    void IBufferWriter<byte>.Advance(int count)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            ThrowIfNoActiveEntry();
            _buffer.AdvanceWriter(count);
        }
    }

    Memory<byte> IBufferWriter<byte>.GetMemory(int sizeHint)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            ThrowIfNoActiveEntry();
            return _buffer.GetMemory(sizeHint);
        }
    }

    Span<byte> IBufferWriter<byte>.GetSpan(int sizeHint)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            ThrowIfNoActiveEntry();
            return _buffer.GetSpan(sizeHint);
        }
    }
}
