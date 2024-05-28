namespace Orleans.Configuration;

/// <summary>
/// Options for ADO.NET Streaming.
/// </summary>
public class AdoNetStreamOptions
{
    /// <summary>
    /// Gets or sets the ADO.NET invariant.
    /// </summary>
    public string Invariant { get; set; }

    /// <summary>
    /// Gets or sets the connection string.
    /// </summary>
    [Redact]
    public string ConnectionString { get; set; }

    /// <summary>
    /// The maximum number of attempts to deliver a message.
    /// The message is eventually moved to dead letters if these many attempts are made without success.
    /// </summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>
    /// The timeout until a message is allowed to be dequeued again if not yet confirmed.
    /// </summary>
    public TimeSpan VisibilityTimeout { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The expiry timeout until a message is considered expired and moved to dead letters regardless of attempts.
    /// The message is only moved if the current attempt is also past its visibility timeout.
    /// </summary>
    public TimeSpan ExpiryTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The removal timeout until a failed message is deleted from the dead letters table.
    /// </summary>
    public TimeSpan DeadLetterEvictionTimeout { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// The period of time between eviction activities.
    /// These include moving expired messages to dead letters and removing dead letters after their own lifetime.
    /// This period is cluster wide and will not change with the number of silos.
    /// </summary>
    public TimeSpan EvictionInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The maximum number of messages affected by an eviction batch.
    /// </summary>
    public int EvictionBatchSize { get; set; } = 1000;

    /// <summary>
    /// A safety timeout for underlying database initialization.
    /// </summary>
    public TimeSpan InitializationTimeout { get; set; } = TimeSpan.FromSeconds(30);
}