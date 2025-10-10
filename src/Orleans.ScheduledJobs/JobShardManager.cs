using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.ScheduledJobs;

public abstract class JobShardManager
{
    public abstract Task<List<JobShard>> GetJobShardsAsync(SiloAddress siloAddress, DateTimeOffset maxDueTime);

    public abstract Task<JobShard> RegisterShard(SiloAddress siloAddress, DateTimeOffset minDueTime, DateTimeOffset maxDueTime, IDictionary<string,string> metadata);

    public abstract Task UnregisterShard(SiloAddress siloAddress, JobShard shard);
}

internal class InMemoryJobShardManager : JobShardManager
{
    private readonly Dictionary<string, List<InMemoryJobShard>> _shardStore = new();

    public override Task<List<JobShard>> GetJobShardsAsync(SiloAddress siloAddress, DateTimeOffset maxDueTime)
    {
        var key = siloAddress.ToString();
        if (_shardStore.TryGetValue(key, out var shards))
        {
            var result = new List<JobShard>();
            foreach (var shard in shards)
            {
                if (shard.EndTime <= maxDueTime)
                {
                    result.Add(shard);
                }
            }
            return Task.FromResult(result);
        }
        return Task.FromResult(new List<JobShard>());
    }

    public override Task<JobShard> RegisterShard(SiloAddress siloAddress, DateTimeOffset minDueTime, DateTimeOffset maxDueTime, IDictionary<string, string> metadata)
    {
        var key = siloAddress.ToString();
        if (!_shardStore.ContainsKey(key))
        {
            _shardStore[key] = new List<InMemoryJobShard>();
        }
        var shardId = $"{key}-{Guid.NewGuid()}";
        var newShard = new InMemoryJobShard(shardId, minDueTime, maxDueTime);
        _shardStore[key].Add(newShard);
        return Task.FromResult((JobShard)newShard);
    }

    public override Task UnregisterShard(SiloAddress siloAddress, JobShard shard)
    {
        var key = siloAddress.ToString();
        if (_shardStore.TryGetValue(key, out var shards))
        {
            shards.RemoveAll(s => s.Id == shard.Id);
        }
        return Task.CompletedTask;
    }
}
