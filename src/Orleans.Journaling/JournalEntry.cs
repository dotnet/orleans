namespace Orleans.Journaling;

/// <summary>
/// Represents one persisted journal entry payload.
/// </summary>
/// <remarks>
/// <para>
/// The payload is owned by the journal format identified by <see cref="FormatKey"/>. This type does
/// not interpret the payload and has no knowledge of any specific physical format.
/// </para>
/// <para>
/// The payload is only guaranteed to remain valid for the duration of the synchronous replay call
/// which supplies this entry. Implementations which need to retain the payload must copy it.
/// </para>
/// </remarks>
/// <param name="formatKey">The journal format key for <paramref name="payload"/>.</param>
/// <param name="payload">The entry payload buffer.</param>
public readonly ref struct JournalEntry(string formatKey, JournalBufferReader payload)
{
    /// <summary>
    /// Gets the journal format key for this entry.
    /// </summary>
    public string FormatKey { get; } = JournalFormatServices.ValidateJournalFormatKey(formatKey);

    /// <summary>
    /// Gets the entry payload buffer.
    /// </summary>
    public JournalBufferReader Payload { get; } = payload;
}
