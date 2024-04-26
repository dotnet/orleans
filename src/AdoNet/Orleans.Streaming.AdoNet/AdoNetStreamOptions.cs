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
    /// The number of individual "queues" to use in the storage table.
    /// Each "queue":
    /// - Maps to an individual clustered index key in the table.
    /// - Will have its own individual pulling agent in the cluster.
    /// Therefore:
    /// - A higher number of queues can increase throughput at the expense of increased i/o contention.
    /// - A lower number of queues can reduce i/o contention at the expense of reduced throughput.
    /// The default is 8.
    /// </summary>
    public int QueueCount { get; set; } = 8;

    /// <summary>
    /// The maximum number of attempts to deliver a message.
    /// The message is eventually moved to dead letters if these many attempts are made without success.
    /// </summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>
    /// The visibility timeout, in seconds, before a dequeued but unconfirmed message becomes available for dequeuing again.
    /// </summary>
    public int VisibilityTimeout { get; set; } = 60;

    /// <summary>
    /// The maximum number of messages to dequeue in a single operation.
    /// This value is further capped by the maximum number of messages that the current queue cache supports.
    /// </summary>
    public int MaxBatchSize { get; set; } = 32;

    /// <summary>
    /// The expiry timeout, in seconds, until a message is considered expired and moved to dead letters.
    /// </summary>
    public int ExpiryTimeout { get; set; } = 300;

    /// <summary>
    /// The removal timeout, in seconds, until a failed message is deleted from the dead letters table.
    /// Defaults to seven days.
    /// </summary>
    public int RemovalTimeout { get; set; } = 604800;

    /// <summary>
    /// Whether to delete messages from the dead letters table after <see cref="RemovalTimeout"/>.
    /// </summary>
    public bool RemoveDeadLetters { get; set; } = true;
}