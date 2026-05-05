namespace Orleans.Journaling;

/// <summary>
/// Consumes raw log data read from an <see cref="ILogStorage"/> instance.
/// </summary>
public interface ILogStorageConsumer
{
    /// <summary>
    /// Consumes buffered raw log data.
    /// </summary>
    /// <param name="buffer">The buffered log data available to the consumer.</param>
    void Consume(LogReadBuffer buffer);
}
