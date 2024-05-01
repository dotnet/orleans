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
    public int MaxAttempts { get; set; } = 3;

    /// <summary>
    /// The expiry timeout until a message is considered expired and moved to dead letters regardless of attempts.
    /// The message is only moved if the current attempt is also past its visibility timeout.
    /// </summary>
    public TimeSpan ExpiryTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The removal timeout until a failed message is deleted from the dead letters table.
    /// </summary>
    public TimeSpan RemovalTimeout { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Whether to delete messages from the dead letters table after <see cref="RemovalTimeout"/>.
    /// </summary>
    public bool RemoveDeadLetters { get; set; } = true;

    /// <summary>
    /// The maximum number of messages affected by a sweep batch.
    /// Lower this number to lessen the impact of maintenance on streaming activities.
    /// Increasing this number above ~5000 may cause the underlying RDBMS to suffer from deadlocks due to automatic lock promotion.
    /// In that situation also alter the sql queries to always use table locks.
    /// </summary>
    public int SweepBatchSize { get; set; } = 1000;

    /// <summary>
    /// The cluster-wide period between sweep activities.
    /// These include moving expired messages to dead letters and removing dead letters after their own lifetime.
    /// This period is scaled and randomized dynamically according to the current number of silos in the cluster to avoid database stampedes.
    /// </summary>
    public TimeSpan SweepPeriod { get; set; } = TimeSpan.FromMinutes(1);
}