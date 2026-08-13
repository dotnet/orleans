using System.Data.Common;

namespace Orleans.Configuration;

/// <summary>
/// Options for ADO.NET Streaming.
/// </summary>
public class AdoNetStreamOptions
{
    /// <summary>
    /// Gets or sets the ADO.NET invariant.
    /// </summary>
    public string Invariant { get; set; } = default!;

    /// <summary>
    /// Gets or sets the connection string.
    /// </summary>
    [Redact]
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Gets or sets the data source used to open database connections.
    /// </summary>
    /// <remarks>
    /// The data source is owned by the caller and is not disposed by Orleans.
    /// </remarks>
    [Redact]
    public DbDataSource? DataSource { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a new partition checkpoint starts at the retained log tail.
    /// </summary>
    /// <remarks>
    /// When <see langword="false"/>, a new checkpoint starts immediately before the earliest retained message.
    /// This setting is only used while initializing a partition which does not have a checkpoint.
    /// </remarks>
    public bool StartFromNow { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of messages returned by a partition read.
    /// </summary>
    public int MaxMessagesPerRead { get; set; } = 1_000;

    /// <summary>
    /// Gets or sets the interval between checkpoint persistence attempts.
    /// </summary>
    public TimeSpan CheckpointPersistInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets the minimum amount of time that a message is retained after it is checkpointed.
    /// </summary>
    public TimeSpan RetentionPeriod { get; set; } = TimeSpan.FromDays(1);

    /// <summary>
    /// Gets or sets an optional hard retention ceiling.
    /// </summary>
    /// <remarks>
    /// Messages older than this value can be deleted even when they are newer than the checkpoint.
    /// Such deletions are reported by storage so that the receiver can emit gap diagnostics.
    /// </remarks>
    public TimeSpan? MaximumRetentionPeriod { get; set; }

    /// <summary>
    /// Gets or sets the interval between cleanup attempts for a partition.
    /// </summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Gets or sets the maximum number of messages deleted by one cleanup operation.
    /// </summary>
    public int CleanupBatchSize { get; set; } = 1_000;

    /// <summary>
    /// A safety timeout for underlying database initialization.
    /// </summary>
    public TimeSpan InitializationTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
