using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.DurableJobs;

/// <summary>
/// Manages the lifecycle of job shards for a specific silo.
/// </summary>
internal abstract class JobShardManager
{
    /// <summary>
    /// Gets the silo address this manager is associated with.
    /// </summary>
    protected SiloAddress SiloAddress { get; }

    protected JobShardManager(SiloAddress siloAddress)
    {
        SiloAddress = siloAddress;
    }

    public abstract Task<List<IJobShard>> AssignJobShardsAsync(DateTimeOffset maxDueTime, int maxNewClaims, CancellationToken cancellationToken);

    public abstract Task<IJobShard> CreateShardAsync(DateTimeOffset minDueTime, DateTimeOffset maxDueTime, IDictionary<string, string> metadata, CancellationToken cancellationToken);

    public abstract Task UnregisterShardAsync(IJobShard shard, CancellationToken cancellationToken);

    internal virtual ValueTask<SiloAddress?> GetShardOwnerAsync(string shardId, CancellationToken cancellationToken) => new((SiloAddress?)null);

    internal virtual ValueTask<bool> IsShardOwnedByLocalSiloAsync(string shardId, CancellationToken cancellationToken) => new(true);
}
