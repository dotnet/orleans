using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.ScheduledJobs;

/// <summary>
/// Manages the lifecycle of job shards for a specific silo.
/// Each silo instance has its own shard manager.
/// </summary>
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
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of job shards assigned to this silo.</returns>
    public abstract Task<List<IJobShard>> AssignJobShardsAsync(DateTimeOffset maxDueTime, CancellationToken cancellationToken);

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
}

internal class InMemoryJobShardManager : JobShardManager
{
    private readonly Dictionary<string, List<IJobShard>> _shardStore = new();

    public InMemoryJobShardManager(SiloAddress siloAddress) : base(siloAddress)
    {
    }

    public override Task<List<IJobShard>> AssignJobShardsAsync(DateTimeOffset maxDueTime, CancellationToken cancellationToken)
    {
        var key = SiloAddress.ToString();
        if (_shardStore.TryGetValue(key, out var shards))
        {
            var result = new List<IJobShard>();
            foreach (var shard in shards)
            {
                if (shard.EndTime <= maxDueTime)
                {
                    result.Add(shard);
                }
            }
            return Task.FromResult(result);
        }
        return Task.FromResult(new List<IJobShard>());
    }

    public override Task<IJobShard> CreateShardAsync(DateTimeOffset minDueTime, DateTimeOffset maxDueTime, IDictionary<string, string> metadata, CancellationToken cancellationToken)
    {
        var key = SiloAddress.ToString();
        if (!_shardStore.ContainsKey(key))
        {
            _shardStore[key] = [];
        }
        var shardId = $"{key}-{Guid.NewGuid()}";
        var newShard = new InMemoryJobShard(shardId, minDueTime, maxDueTime);
        _shardStore[key].Add(newShard);
        return Task.FromResult((IJobShard)newShard);
    }

    public override Task UnregisterShardAsync(IJobShard shard, CancellationToken cancellationToken)
    {
        var key = SiloAddress.ToString();
        if (_shardStore.TryGetValue(key, out var shards))
        {
            shards.RemoveAll(s => s.Id == shard.Id);
        }
        return Task.CompletedTask;
    }
}
