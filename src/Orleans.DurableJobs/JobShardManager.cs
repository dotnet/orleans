using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.DurableJobs;

/// <summary>
/// Manages the lifecycle of job shards for a specific silo.
/// </summary>
/// <remarks>
/// Each silo instance has its own shard manager.
/// </remarks>
public abstract class JobShardManager
{
    /// <summary>
    /// Gets the silo address this manager is associated with.
    /// </summary>
    protected SiloAddress SiloAddress { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="JobShardManager"/> class.
    /// </summary>
    /// <param name="siloAddress">The silo address this manager represents.</param>
    protected JobShardManager(SiloAddress siloAddress)
    {
        SiloAddress = siloAddress;
    }

    /// <summary>
    /// Assigns orphaned job shards to this silo.
    /// </summary>
    /// <param name="maxDueTime">Maximum due time for shards to consider.</param>
    /// <param name="maxNewClaims">
    /// The maximum number of orphaned shards to claim in this call.
    /// Use <see cref="int.MaxValue"/> for unlimited.
    /// Shards already owned by this silo are always returned regardless of this limit.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of job shards assigned to this silo.</returns>
    public abstract Task<List<IJobShard>> AssignJobShardsAsync(DateTimeOffset maxDueTime, int maxNewClaims, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a new job shard owned by this silo.
    /// </summary>
    /// <param name="minDueTime">The minimum due time for jobs in this shard.</param>
    /// <param name="maxDueTime">The maximum due time for jobs in this shard.</param>
    /// <param name="metadata">Optional metadata for the shard.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The newly created job shard.</returns>
    public abstract Task<IJobShard> CreateShardAsync(DateTimeOffset minDueTime, DateTimeOffset maxDueTime, IDictionary<string, string> metadata, CancellationToken cancellationToken);

    /// <summary>
    /// Unregisters a shard owned by this silo.
    /// </summary>
    /// <param name="shard">The shard to unregister.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public abstract Task UnregisterShardAsync(IJobShard shard, CancellationToken cancellationToken);

    internal virtual ValueTask<SiloAddress?> GetShardOwnerAsync(string shardId, CancellationToken cancellationToken) => new((SiloAddress?)null);

    internal virtual ValueTask<bool> IsShardOwnedByLocalSiloAsync(string shardId, CancellationToken cancellationToken) => new(true);
}
