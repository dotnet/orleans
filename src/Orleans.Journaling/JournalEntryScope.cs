using System.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Represents the lexical write scope for one state journal entry.
/// </summary>
/// <remarks>
/// Call <see cref="Commit"/> after successfully writing the entry payload. If the scope is
/// disposed before it is committed, the pending entry is aborted.
/// </remarks>
public ref struct JournalEntryScope : IDisposable
{
    private JournalWriter? _writer;
    private int _entryStart;
    private bool _completed;

    internal JournalEntryScope(JournalWriter writer, int entryStart)
    {
        _writer = writer;
        _entryStart = entryStart;
        _completed = false;
    }

    /// <summary>
    /// Gets the payload writer for this entry.
    /// </summary>
    public readonly IBufferWriter<byte> PayloadWriter => _writer ?? throw new InvalidOperationException(_completed ? "The journal entry has already completed." : "The journal entry scope is not active.");

    /// <summary>
    /// Commits the pending entry, making it visible to storage.
    /// </summary>
    /// <exception cref="InvalidOperationException">The entry has already completed.</exception>
    public void Commit()
    {
        if (_completed)
        {
            throw new InvalidOperationException("The journal entry has already completed.");
        }

        var writer = GetWriter();
        var entryStart = _entryStart;
        writer.CommitEntryWrite(entryStart);
        _completed = true;
        _writer = null;
        _entryStart = 0;
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

        var writer = GetWriter();
        var entryStart = _entryStart;
        _completed = true;
        _writer = null;
        _entryStart = 0;
        writer.AbortEntryWrite(entryStart);
    }

    private readonly JournalWriter GetWriter()
    {
        if (_writer is null)
        {
            throw new InvalidOperationException(_completed ? "The journal entry has already completed." : "The journal entry scope is not active.");
        }

        return _writer;
    }
}
