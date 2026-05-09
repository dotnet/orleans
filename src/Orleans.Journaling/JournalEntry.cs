using System.Diagnostics.CodeAnalysis;

namespace Orleans.Journaling;

/// <summary>
/// Represents the lexical write scope for one state journal entry.
/// </summary>
/// <remarks>
/// Call <see cref="Commit"/> after successfully writing the entry payload. If the scope is
/// disposed before it is committed, the pending entry is aborted.
/// </remarks>
public ref struct JournalEntry : IDisposable
{
    private bool _completed;

    internal JournalEntry(JournalEntryWriter writer)
    {
        Writer = writer;
        _completed = false;
    }

    /// <summary>
    /// Gets the payload writer for this entry.
    /// </summary>
    [AllowNull]
    public JournalEntryWriter Writer
    {
        readonly get => field ?? throw new InvalidOperationException(_completed ? "The journal entry has already completed." : "The journal entry scope is not active.");
        private set;
    }

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

        var writer = Writer;
        writer.Commit();
        _completed = true;
        Writer = null;
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
        Writer = null;
        writer.Abort();
    }
}
