namespace Orleans.Journaling;

/// <summary>
/// Reads and writes the physical byte format used to persist state machine journal entries.
/// </summary>
/// <remarks>
/// A journal format owns physical framing for entries. Durable state machine codecs write only
/// the payload for a single entry.
/// </remarks>
public interface IJournalFormat
{
    /// <summary>
    /// Gets the file extension used for journal storage written with this format, including the leading dot.
    /// </summary>
    string FileExtension { get; }

    /// <summary>
    /// Gets the MIME type for journal storage written with this format, or <see langword="null"/> to use the storage provider's default.
    /// </summary>
    string? MimeType { get; }

    /// <summary>
    /// Creates a writer for a new mutable journal batch.
    /// </summary>
    /// <returns>A new journal batch writer.</returns>
    IJournalBatchWriter CreateWriter();

    /// <summary>
    /// Reads as many complete journal entries as possible from <paramref name="input"/> and applies them to resolved state machines.
    /// </summary>
    /// <param name="input">The buffered persisted journal data, including its completion state.</param>
    /// <param name="resolver">The resolver used to locate state machines by journal stream id.</param>
    /// <remarks>
    /// If <see cref="JournalReadBuffer.IsCompleted"/> is <see langword="false"/>, this method returns when
    /// there is insufficient data to read another complete journal entry. If <see cref="JournalReadBuffer.IsCompleted"/>
    /// is <see langword="true"/>, this method throws if the remaining data does not contain complete journal entries.
    /// </remarks>
    void Read(JournalReadBuffer input, IStateMachineResolver resolver);
}
