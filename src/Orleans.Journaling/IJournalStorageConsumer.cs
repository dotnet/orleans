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
