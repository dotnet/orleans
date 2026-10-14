using StackExchange.Redis;

namespace Orleans.Journaling;

/// <summary>
/// Options for journal storage on embedded Garnet partitions.
/// </summary>
/// <remarks>
/// Each journal maps to one of <see cref="BucketCount"/> buckets by a hash of its id, and each bucket is owned by one
/// partition: one Garnet instance with its own append-only file and checkpoints. A silo either hosts the partition in
/// process, writing its durable state under <see cref="DataDirectory"/>, or connects to a partition hosted by another
/// process through <see cref="ConfigurationOptions"/>.
/// </remarks>
public sealed class GarnetJournalStorageOptions
{
    /// <summary>The default compaction threshold, in bytes.</summary>
    public const long DEFAULT_COMPACTION_THRESHOLD_BYTES = 8L * 1024 * 1024;

    /// <summary>The default number of buckets.</summary>
    public const int DEFAULT_BUCKET_COUNT = 4096;

    /// <summary>The default in-memory size of an embedded partition's hybrid log.</summary>
    public const string DEFAULT_LOG_MEMORY_SIZE = "1g";

    /// <summary>The default size of an embedded partition's hash index.</summary>
    public const string DEFAULT_INDEX_MEMORY_SIZE = "64m";

    /// <summary>The default AOF size at which an embedded partition takes a checkpoint.</summary>
    public const string DEFAULT_CHECKPOINT_AOF_SIZE = "256m";

    /// <summary>
    /// Gets or sets the directory in which this silo hosts an embedded partition.
    /// </summary>
    /// <remarks>
    /// The directory holds the partition's append-only file and checkpoints, and must persist across silo restarts.
    /// Set either this property or <see cref="ConfigurationOptions"/>.
    /// </remarks>
    public string? DataDirectory { get; set; }

    /// <summary>
    /// Gets or sets the client configuration for a partition hosted by another process.
    /// </summary>
    /// <remarks>Set either this property or <see cref="DataDirectory"/>.</remarks>
    [RedactGarnetConfigurationOptions]
    public ConfigurationOptions? ConfigurationOptions { get; set; }

    /// <summary>
    /// Gets or sets the endpoint on which the embedded partition accepts connections.
    /// </summary>
    /// <remarks>
    /// Defaults to a free port on the loopback interface. Silos which connect to this partition through
    /// <see cref="ConfigurationOptions"/> require an endpoint reachable from their hosts. Port 0 chooses a free port.
    /// </remarks>
    public System.Net.IPEndPoint? ListenEndPoint { get; set; }

    /// <summary>
    /// Gets or sets the number of buckets journals are hashed into. Must be a power of two.
    /// </summary>
    /// <remarks>The bucket count is fixed when a partition is first initialized.</remarks>
    public int BucketCount { get; set; } = DEFAULT_BUCKET_COUNT;

    /// <summary>
    /// Gets or sets the journal length, in bytes, at which <see cref="IJournalStorage.IsCompactionRequested"/> returns <see langword="true"/>.
    /// </summary>
    public long CompactionThresholdBytes { get; set; } = DEFAULT_COMPACTION_THRESHOLD_BYTES;

    /// <summary>
    /// Gets or sets how long an operation retries while its partition is unreachable or its outcome is unknown.
    /// </summary>
    /// <remarks>
    /// Retries reuse the operation's ETags, so a retry of an operation which already committed reports its success
    /// instead of applying it again.
    /// </remarks>
    public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets the in-memory size of the embedded partition's hybrid log, for example <c>1g</c>.</summary>
    public string LogMemorySize { get; set; } = DEFAULT_LOG_MEMORY_SIZE;

    /// <summary>Gets or sets the size of the embedded partition's hash index, for example <c>64m</c>.</summary>
    public string IndexMemorySize { get; set; } = DEFAULT_INDEX_MEMORY_SIZE;

    /// <summary>
    /// Gets or sets the size of the embedded partition's append-only file at which it takes a checkpoint, for example <c>256m</c>.
    /// </summary>
    /// <remarks>
    /// A checkpoint compacts the hybrid log and truncates the append-only file, bounding disk use and recovery time.
    /// </remarks>
    public string CheckpointAofSize { get; set; } = DEFAULT_CHECKPOINT_AOF_SIZE;

    /// <summary>
    /// Gets or sets the stage of the silo lifecycle when storage is initialized.
    /// </summary>
    public int InitStage { get; set; } = ServiceLifecycleStage.RuntimeInitialize;
}

internal sealed class RedactGarnetConfigurationOptionsAttribute : RedactAttribute
{
    public override string Redact(object? value) => value is ConfigurationOptions configuration
        ? configuration.ToString(includePassword: false)
        : base.Redact(value);
}
