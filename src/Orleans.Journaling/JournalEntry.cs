namespace Orleans.Journaling;

/// <summary>
/// Represents one persisted journal entry.
/// </summary>
/// <remarks>
/// <para>
/// The reader is owned by the journal format identified by <see cref="FormatKey"/>. This type does
/// not interpret the entry data and has no knowledge of any specific physical format.
/// </para>
/// <para>
/// The reader is only guaranteed to remain valid for the duration of the synchronous replay call
/// which supplies this entry. Implementations which need to retain the data must copy it.
/// </para>
/// </remarks>
/// <param name="formatKey">The journal format key for <paramref name="reader"/>.</param>
/// <param name="reader">The entry payload reader.</param>
public readonly ref struct JournalEntry(string formatKey, JournalBufferReader reader)
{
    /// <summary>
    /// Gets the journal format key for this entry.
    /// </summary>
    public string FormatKey { get; } = JournalFormatServices.ValidateJournalFormatKey(formatKey);

    /// <summary>
    /// Gets the entry payload reader.
    /// </summary>
    public JournalBufferReader Reader { get; } = reader;
}
