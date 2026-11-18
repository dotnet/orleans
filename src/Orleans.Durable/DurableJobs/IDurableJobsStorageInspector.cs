namespace Orleans.DurableJobs;

/// <summary>
/// Inspects the complete shard namespace of a selected Durable Jobs journal provider.
/// </summary>
public interface IDurableJobsStorageInspector
{
    /// <summary>
    /// Counts existing shards, including future-dated and poisoned shards, without changing ownership.
    /// </summary>
    /// <remarks>
    /// The result observes live storage using the provider's catalog semantics. Retiring a provider
    /// requires all scheduling silos to finish their write-provider cutover before an empty inventory
    /// can establish that its shards have drained. Storage and cancellation failures fault the operation.
    /// </remarks>
    /// <param name="providerName">The selected write or draining journal provider name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The provider inventory.</returns>
    ValueTask<DurableJobsStorageStatus> InspectAsync(string providerName, CancellationToken cancellationToken = default);
}

/// <summary>
/// A complete catalog observation of a selected Durable Jobs journal provider.
/// </summary>
public sealed class DurableJobsStorageStatus
{
    internal DurableJobsStorageStatus(
        string providerName,
        bool isWriteProvider,
        long shardCount,
        long ownedShardCount,
        long poisonedShardCount,
        long unrecognizedShardCount,
        DateTimeOffset? oldestShardStartTime,
        DateTimeOffset? newestShardStartTime)
    {
        ProviderName = providerName;
        IsWriteProvider = isWriteProvider;
        ShardCount = shardCount;
        OwnedShardCount = ownedShardCount;
        PoisonedShardCount = poisonedShardCount;
        UnrecognizedShardCount = unrecognizedShardCount;
        OldestShardStartTime = oldestShardStartTime;
        NewestShardStartTime = newestShardStartTime;
    }

    /// <summary>Gets the journal provider name.</summary>
    public string ProviderName { get; }

    /// <summary>Gets whether this silo uses the provider to create new shards.</summary>
    public bool IsWriteProvider { get; }

    /// <summary>Gets the number of existing journals in the shard namespace.</summary>
    public long ShardCount { get; }

    /// <summary>Gets the number of recognized shards with a recorded owner.</summary>
    public long OwnedShardCount { get; }

    /// <summary>Gets the number of recognized poisoned shards.</summary>
    public long PoisonedShardCount { get; }

    /// <summary>Gets the number of journals whose shard metadata could not be interpreted.</summary>
    public long UnrecognizedShardCount { get; }

    /// <summary>Gets the earliest recognized shard start time, or <see langword="null"/> when none exist.</summary>
    public DateTimeOffset? OldestShardStartTime { get; }

    /// <summary>Gets the latest recognized shard start time, or <see langword="null"/> when none exist.</summary>
    public DateTimeOffset? NewestShardStartTime { get; }
}
