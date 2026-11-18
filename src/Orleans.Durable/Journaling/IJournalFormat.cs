namespace Orleans.Journaling;

/// <summary>
/// Reads and writes the physical byte format used to persist state journal entries.
/// </summary>
/// <remarks>
/// A journal format owns physical framing for entries. Durable state codecs write only
/// the payload for a single entry.
/// </remarks>
public interface IJournalFormat
{
    /// <summary>
    /// Gets the key which identifies this journal format in storage metadata and keyed service registration.
    /// </summary>
    string FormatKey { get; }

    /// <summary>
    /// Gets the MIME type for journal storage written with this format, or <see langword="null"/> to use the storage provider's default.
    /// </summary>
    string? MimeType { get; }

    /// <summary>
    /// Creates a writer for a new mutable journal batch.
    /// </summary>
    /// <returns>A new journal buffer writer.</returns>
    JournalBufferWriter CreateWriter();

    /// <summary>
    /// Reads as many complete journal entries as possible from <paramref name="input"/> and applies them to resolved states.
    /// </summary>
    /// <param name="input">The buffered persisted journal data, including its completion state.</param>
    /// <param name="context">The replay context used to resolve states and journal services.</param>
    /// <remarks>
    /// If <see cref="JournalBufferReader.IsCompleted"/> is <see langword="false"/>, this method returns when
    /// there is insufficient data to read another complete journal entry. If <see cref="JournalBufferReader.IsCompleted"/>
    /// is <see langword="true"/>, this method throws if the remaining data does not contain complete journal entries.
    /// </remarks>
    void Replay(JournalBufferReader input, JournalReplayContext context);
}
