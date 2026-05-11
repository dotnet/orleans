namespace Orleans.Journaling;

/// <summary>
/// Consumes raw journal data read from an <see cref="IJournalStorage"/> instance.
/// </summary>
public interface IJournalStorageConsumer
{
    /// <summary>
    /// Consumes buffered raw journal data.
    /// </summary>
    /// <param name="buffer">The buffered journal data available to the consumer.</param>
    void Consume(JournalReadBuffer buffer);
}

/// <summary>
/// Receives storage metadata associated with journal data being read.
/// </summary>
/// <remarks>
/// Storage implementations which persist journal format metadata should call
/// <see cref="SetJournalFormatKey"/> before supplying any journal bytes to <see cref="IJournalStorageConsumer.Consume"/>.
/// </remarks>
public interface IJournalStorageFormatMetadataConsumer : IJournalStorageConsumer
{
    /// <summary>
    /// Sets the journal format key stored with the journal data, or <see langword="null"/> if no key is present.
    /// </summary>
    /// <param name="journalFormatKey">The stored journal format key, or <see langword="null"/> if absent.</param>
    void SetJournalFormatKey(string? journalFormatKey);
}
