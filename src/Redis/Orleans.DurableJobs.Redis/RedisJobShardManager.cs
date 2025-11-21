using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using StackExchange.Redis;

namespace Orleans.DurableJobs.Redis;

public sealed partial class RedisJobShardManager : JobShardManager
{
    private readonly IConnectionMultiplexer? _connection;
    private readonly IDatabase? _db;
    private readonly string _keyPrefix;
    private readonly string _shardSetKey;
    private readonly ConcurrentDictionary<string, RedisJobShard> _jobShardCache = new();
    private readonly ILogger<RedisJobShardManager>? _logger;
    private readonly ILoggerFactory? _loggerFactory;
    private long _shardCounter = 0;

    // Back-compat simple ctor (will require calling Init with connection via DI in practice)
    //public RedisJobShardManager(SiloAddress siloAddress)
    //    : base(siloAddress)
    //{
    //    // No redis configured - methods will throw if called.
    //}

    // Preferred ctor used in production
    public RedisJobShardManager(SiloAddress siloAddress, IConnectionMultiplexer connection, string keyPrefix, ILoggerFactory loggerFactory)
        : base(siloAddress)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _db = connection.GetDatabase();
        _keyPrefix = keyPrefix ?? "durablejobs";
        _shardSetKey = $"{_keyPrefix}:shards";
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = loggerFactory.CreateLogger<RedisJobShardManager>();
    }

    public override async Task<List<IJobShard>> AssignJobShardsAsync(DateTimeOffset maxDueTime, CancellationToken cancellationToken)
    {
        if (_db is null) throw new InvalidOperationException("Redis connection is not configured for RedisJobShardManager.");

        _logger?.LogDebug("Assigning job shards for silo {SiloAddress} with maxDueTime {MaxDueTime}", SiloAddress, maxDueTime);

        var result = new List<IJobShard>();
        var members = await _db.SetMembersAsync(_shardSetKey).ConfigureAwait(false);

        foreach (var member in members)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var shardId = (string)member!;
            var metaKey = MetaKeyFor(shardId);

            // Read metadata hash
            var metaEntries = await _db.HashGetAllAsync(metaKey).ConfigureAwait(false);
            var metadata = metaEntries.ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());

            var (owner, minDue, maxDue) = ParseMetadata(metadata);

            // If shard is too new, since we don't order set members, continue (manager caller should filter by maxDueTime)
            if (minDue > maxDueTime)
            {
                _logger?.LogTrace("Ignoring shard '{ShardId}' because MinDueTime {MinDue} > {MaxDueTime}", shardId, minDue, maxDueTime);
                continue;
            }

            // If I'm the owner, return cached or load it
            if (owner is not null && owner.Equals(SiloAddress))
            {
                if (_jobShardCache.TryGetValue(shardId, out var cached))
                {
                    _logger?.LogDebug("Shard '{ShardId}' assigned to this silo (cached).", shardId);
                    result.Add(cached);
                }
                else
                {
                    // Create and initialize
                    var shard = new RedisJobShard(shardId, minDue, maxDue, _db, _keyPrefix, _loggerFactory!.CreateLogger<RedisJobShard>());
                    await shard.InitializeAsync(cancellationToken).ConfigureAwait(false);
                    await shard.MarkAsCompleteAsync(cancellationToken).ConfigureAwait(false); // mimic Azure behavior for new claim
                    _jobShardCache[shardId] = shard;
                    _logger?.LogDebug("Shard '{ShardId}' assigned to this silo (loaded).", shardId);
                    result.Add(shard);
                }
                continue;
            }

            // If owner is null -> try to claim
            if (owner is null)
            {
                _logger?.LogDebug("Attempting to claim orphan shard '{ShardId}'", shardId);
                var claimed = await TryTakeOwnershipAsync(shardId, metadata, SiloAddress, cancellationToken).ConfigureAwait(false);
                if (!claimed)
                {
                    _logger?.LogDebug("Failed to claim shard '{ShardId}' - someone else claimed it.", shardId);
                    continue;
                }

                // Instantiate shard and initialize
                var shard = new RedisJobShard(shardId, minDue, maxDue, _db, _keyPrefix, _loggerFactory!.CreateLogger<RedisJobShard>());
                await shard.InitializeAsync(cancellationToken).ConfigureAwait(false);
                // We don't want to add new jobs to shards we just took ownership of
                await shard.MarkAsCompleteAsync(cancellationToken).ConfigureAwait(false);
                _jobShardCache[shardId] = shard;
                _logger?.LogInformation("Shard '{ShardId}' claimed and assigned to {Silo}", shardId, SiloAddress);
                result.Add(shard);
                continue;
            }

            // Otherwise it's owned by another silo - skip
            _logger?.LogTrace("Shard '{ShardId}' is owned by another silo {Owner} - skipping", shardId, owner);
        }

        _logger?.LogInformation("Assigned {Count} shard(s) to silo {Silo}", result.Count, SiloAddress);
        return result;
    }

    public override async Task<IJobShard> CreateShardAsync(DateTimeOffset minDueTime, DateTimeOffset maxDueTime, IDictionary<string, string> metadata, CancellationToken cancellationToken)
    {
        if (_db is null) throw new InvalidOperationException("Redis connection is not configured for RedisJobShardManager.");
        var i = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var counter = Interlocked.Increment(ref _shardCounter);
            var shardId = $"{_keyPrefix}-{minDueTime:yyyyMMddHHmm}-{SiloAddress.ToParsableString()}-{counter}";
            var metaKey = MetaKeyFor(shardId);

            // Prepare metadata with time window and ownership
            var meta = CreateMetadata(metadata, minDueTime, maxDueTime);
            meta["Owner"] = SiloAddress.ToParsableString();

            try
            {
                // Create metadata hash and register in shards set atomically via Lua script to avoid races:
                // Only create if the shard does not already exist.
                //var script = @"
                //    if (redis.call('EXISTS', KEYS[1]) == 0) then
                //        redis.call('HSET', KEYS[1], unpack(ARGV))
                //        redis.call('SADD', KEYS[2], ARGV[1]) -- first ARGV entry is 'MinDueTime', not the name; so add KEYS[2] + shardId passed separately
                //        return 1
                //    end
                //    return 0
                //";
                // Because the simple script above is awkward to pass ARGV as pairs, do a simpler approach:
                // Check existence and then create in a transaction
                if (!await _db.KeyExistsAsync(metaKey).ConfigureAwait(false))
                {
                    // Write metadata
                    var entries = meta.Select(kv => new HashEntry(kv.Key, kv.Value)).ToArray();
                    await _db.HashSetAsync(metaKey, entries).ConfigureAwait(false);
                    await _db.SetAddAsync(_shardSetKey, shardId).ConfigureAwait(false);
                    // Created successfully
                }
                else
                {
                    // Collision - try again
                    i++;
                    if (i > 10) throw new InvalidOperationException($"Failed to create unique shard id '{shardId}' after {i} attempts");
                    continue;
                }
            }
            catch (Exception ex)
            {
                i++;
                if (i > 10) throw new InvalidOperationException($"Failed to create shard '{shardId}' after {i} attempts", ex);
                continue;
            }

            // Instantiate shard and initialize
            var shard = new RedisJobShard(shardId, minDueTime, maxDueTime, _db, _keyPrefix, _loggerFactory!.CreateLogger<RedisJobShard>());
            await shard.InitializeAsync(cancellationToken).ConfigureAwait(false);
            _jobShardCache[shardId] = shard;
            _logger?.LogInformation("Shard '{ShardId}' created and assigned to {Silo}", shardId, SiloAddress);
            return shard;
        }
    }

    public override async Task UnregisterShardAsync(IJobShard shard, CancellationToken cancellationToken)
    {
        if (_db is null) throw new InvalidOperationException("Redis connection is not configured for RedisJobShardManager.");
        if (shard is not RedisJobShard redisShard) throw new ArgumentException("Shard is not a RedisJobShard", nameof(shard));

        _logger?.LogInformation("Unregistering shard '{ShardId}' for silo {Silo}", shard.Id, SiloAddress);

        // Stop accepting new jobs on the shard
        await redisShard.MarkAsCompleteAsync(cancellationToken).ConfigureAwait(false);

        var count = await shard.GetJobCountAsync().ConfigureAwait(false);
        var metaKey = MetaKeyFor(shard.Id);

        // Read current owner
        var ownerValue = await _db.HashGetAsync(metaKey, "Owner").ConfigureAwait(false);
        if (!ownerValue.HasValue || ownerValue.IsNull)
        {
            _logger?.LogWarning("Shard '{ShardId}' has no Owner when trying to unregister", shard.Id);
            throw new InvalidOperationException("Cannot unregister a shard without an owner");
        }

        var owner = SiloAddress.FromParsableString(ownerValue.ToString());

        if (!owner.Equals(SiloAddress))
        {
            _logger?.LogWarning("Cannot unregister shard '{ShardId}' - owned by another silo {Owner}", shard.Id, owner);
            throw new InvalidOperationException("Cannot unregister a shard owned by another silo");
        }

        if (count > 0)
        {
            // Remove ownership but keep shard
            await _db.HashDeleteAsync(metaKey, "Owner").ConfigureAwait(false);
            _jobShardCache.TryRemove(shard.Id, out _);
            _logger?.LogInformation("Released ownership of shard '{ShardId}' by silo {Silo} ({JobCount} jobs remaining)", shard.Id, SiloAddress, count);
        }
        else
        {
            // Delete the shard entirely: metadata, stream, and from set
            var streamKey = StreamKeyFor(shard.Id);
            await _db.KeyDeleteAsync(metaKey).ConfigureAwait(false);
            await _db.KeyDeleteAsync(streamKey).ConfigureAwait(false);
            await _db.SetRemoveAsync(_shardSetKey, shard.Id).ConfigureAwait(false);
            _jobShardCache.TryRemove(shard.Id, out _);
            _logger?.LogInformation("Deleted shard '{ShardId}' by silo {Silo} (no jobs remaining)", shard.Id, SiloAddress);
        }

        await redisShard.DisposeAsync().ConfigureAwait(false);
    }

    private static IDictionary<string, string> CreateMetadata(IDictionary<string, string> existingMetadata, DateTimeOffset minDueTime, DateTimeOffset maxDueTime)
    {
        var metadata = new Dictionary<string, string>(existingMetadata)
        {
            { "MinDueTime", minDueTime.ToString("o", CultureInfo.InvariantCulture) },
            { "MaxDueTime", maxDueTime.ToString("o", CultureInfo.InvariantCulture) }
        };
        return metadata;
    }

    private static (SiloAddress? owner, DateTimeOffset minDueTime, DateTimeOffset maxDueTime) ParseMetadata(IDictionary<string, string> metadata)
    {
        var owner = metadata.TryGetValue("Owner", out var ownerStr) && !string.IsNullOrEmpty(ownerStr)
            ? SiloAddress.FromParsableString(ownerStr)
            : null;

        var minDue = metadata.TryGetValue("MinDueTime", out var minStr) && DateTimeOffset.TryParse(minStr, null, DateTimeStyles.RoundtripKind, out var minDt)
            ? minDt
            : DateTimeOffset.MinValue;

        var maxDue = metadata.TryGetValue("MaxDueTime", out var maxStr) && DateTimeOffset.TryParse(maxStr, null, DateTimeStyles.RoundtripKind, out var maxDt)
            ? maxDt
            : DateTimeOffset.MaxValue;

        return (owner, minDue, maxDue);
    }

    private string MetaKeyFor(string shardId) => $"{_keyPrefix}:{shardId}:meta";
    private string StreamKeyFor(string shardId) => $"{_keyPrefix}:{shardId}:stream";

    private async Task<bool> TryTakeOwnershipAsync(string shardId, IDictionary<string, string> currentMetadata, SiloAddress newOwner, CancellationToken cancellationToken)
    {
        // Use a simple Lua script to set Owner only if not present (atomic)
        var metaKey = MetaKeyFor(shardId);
        var script = @"
            if (redis.call('HEXISTS', KEYS[1], 'Owner') == 0) then
                redis.call('HSET', KEYS[1], 'Owner', ARGV[1])
                return 1
            end
            return 0
        ";
        var result = (int)await _db!.ScriptEvaluateAsync(script, new RedisKey[] { metaKey }, new RedisValue[] { newOwner.ToParsableString() }).ConfigureAwait(false);
        return result == 1;
    }
}
