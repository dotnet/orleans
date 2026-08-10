namespace Orleans.Streaming.RabbitMQ.RabbitMQ;

/// <summary>
/// Configures retries when creating a RabbitMQ stream connection.
/// </summary>
public sealed class RabbitMQConnectionRetryOptions
{
    /// <summary>
    /// Gets or sets the maximum number of connection attempts.
    /// </summary>
    public int MaxAttempts { get; set; } = 4;

    /// <summary>
    /// Gets or sets the delay between connection attempts.
    /// </summary>
    public TimeSpan Delay { get; set; } = TimeSpan.FromSeconds(5);
}