using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Runtime;
using StackExchange.Redis;

namespace Orleans.DurableJobs.Redis;

/// <summary>
/// Redis-based implementation of <see cref="JobShardManager"/> that stores job shards in Redis.
/// </summary>
public sealed partial class RedisJobShardManager : JobShardManager
{
    private readonly ILocalSiloDetails _localSiloDetails;
    private readonly IClusterMembershipService _clusterMembership;
    private readonly RedisJobShardOptions _options;
    private readonly ILogger<RedisJobShardManager> _logger;
    private readonly ILoggerFactory _loggerFactory;

    private IConnectionMultiplexer? _multiplexer;
    private IDatabase? _db;

    // in-memory cache of owned shards
    private readonly ConcurrentDictionary<string, RedisJobShard> _jobShardCache = new();

    private long _shardCounter = 0;

    // scripts
    private const string CreateShardLua = @"
            -- KEYS[1] = metaKey
            -- KEYS[2] = shardsSetKey
            -- ARGV[1] = shardId
            -- ARGV[2] = metadataJson (JSON object with all metadata fields)
            if redis.call('EXISTS', KEYS[1]) == 1 then
                return 0
            end
            local metadata = cjson.decode(ARGV[2])
            for k, v in pairs(metadata) do
                redis.call('HSET', KEYS[1], k, v)
            end
            redis.call('SADD', KEYS[2], ARGV[1])
            return 1
        ";

    private const string TryTakeOwnershipLua = @"
            -- KEYS[1] = metaKey
            -- ARGV[1] = expectedVersion
            -- ARGV[2] = newOwner
            -- ARGV[3] = newMembershipVersion
            local curr = redis.call('HGET', KEYS[1], 'version')
            if curr == false then curr = '0' end
            if curr == ARGV[1] then
                local next = tostring(tonumber(curr) + 1)
                redis.call('HSET', KEYS[1], 'Owner', ARGV[2], 'MembershipVersion', ARGV[3], 'version', next)
                return 1
            end
            return 0
        ";

    private const string ReleaseOwnershipLua = @"
            -- KEYS[1] = metaKey
            -- ARGV[1] = expectedVersion
            local curr = redis.call('HGET', KEYS[1], 'version')
            if curr == false then curr = '0' end
            if curr == ARGV[1] then
                local next = tostring(tonumber(curr) + 1)
                redis.call('HDEL', KEYS[1], 'Owner')
                redis.call('HSET', KEYS[1], 'version', next)
                return 1
            end
            return 0
        ";

    /// <summary>
    /// Initializes a new instance of the <see cref="RedisJobShardManager"/> class.
    /// </summary>
    /// <param name="localSiloDetails">The local silo details.</param>
    /// <param name="options">The Redis job shard options.</param>
    /// <param name="clusterMembership">The cluster membership service.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    public RedisJobShardManager(
        ILocalSiloDetails localSiloDetails,
        IOptions<RedisJobShardOptions> options,
        IClusterMembershipService clusterMembership,
        ILoggerFactory loggerFactory)
        : base(localSiloDetails.SiloAddress)
    {
        _localSiloDetails = localSiloDetails ?? throw new ArgumentNullException(nameof(localSiloDetails));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _clusterMembership = clusterMembership ?? throw new ArgumentNullException(nameof(clusterMembership));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = loggerFactory.CreateLogger<RedisJobShardManager>();
    }

    private async ValueTask InitializeIfNeeded(CancellationToken cancellationToken = default)
    {
        if (_db != null) return;

        _logger.LogInformation("Initializing RedisJobShardManager (shardPrefix={Prefix})", _options.ShardPrefix);
        _multiplexer = await _options.CreateMultiplexer(_options).ConfigureAwait(false);
        _db = _multiplexer.GetDatabase();
        _logger.LogInformation("RedisJobShardManager initialized");
    }

    private string ShardSetKey => $"durablejobs:shards:{_options.ShardPrefix}";
    private static string MetaKeyForShard(string shardId) => $"durablejobs:shard:{shardId}:meta";

    public override async Task<List<IJobShard>> AssignJobShardsAsync(DateTimeOffset maxShardStartTime, CancellationToken cancellationToken)
    {
        await InitializeIfNeeded(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Assigning shards up to {MaxShardStartTime} (prefix={Prefix})", maxShardStartTime, _options.ShardPrefix);

        var result = new List<IJobShard>();

        // get all shard ids
        var members = await _db!.SetMembersAsync(ShardSetKey).ConfigureAwait(false);
        var shardIds = members.Select(rv => rv.ToString()).ToArray();

        foreach (var shardId in shardIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var metaKey = MetaKeyForShard(shardId);
            var entries = await _db.HashGetAllAsync(metaKey).ConfigureAwait(false);
            var metadata = HashEntriesToDictionary(entries);

            // parse values
            metadata.TryGetValue("Owner", out var ownerStr);
            metadata.TryGetValue("MembershipVersion", out var membershipVersionStr);
            metadata.TryGetValue("MinDueTime", out var minDueStr);
            metadata.TryGetValue("MaxDueTime", out var maxDueStr);

            // refresh membership if remote higher
            if (!string.IsNullOrEmpty(membershipVersionStr) && long.TryParse(membershipVersionStr, out var memVer))
            {
                var memVersion = new MembershipVersion(memVer);
                if (memVersion > _clusterMembership.CurrentSnapshot.Version)
                {
                    await _clusterMembership.Refresh(memVersion, cancellationToken).ConfigureAwait(false);
                }
            }

            if (!string.IsNullOrEmpty(minDueStr) && DateTimeOffset.TryParse(minDueStr, null, DateTimeStyles.RoundtripKind, out var shardStartTime))
            {
                if (shardStartTime > maxShardStartTime)
                {
                    _logger.LogDebug("Shard {ShardId} is too new (start {Start}) > max {Max}, skipping", shardId, shardStartTime, maxShardStartTime);
                    continue;
                }
            }

            // If I am the owner
            if (!string.IsNullOrEmpty(ownerStr) && ownerStr == _localSiloDetails.SiloAddress.ToParsableString())
            {
                if (_jobShardCache.TryGetValue(shardId, out var cached))
                {
                    _logger.LogDebug("Shard {ShardId} is owned by this silo and in cache", shardId);
                    result.Add(cached);
                    continue;
                }
                else
                {
                    _logger.LogWarning("Shard {ShardId} metadata says owned by this silo but not in cache; releasing ownership", shardId);
                    try
                    {
                        await ReleaseOwnershipAsync(shardId, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to release ownership of shard {ShardId}", shardId);
                    }
                    continue;
                }
            }

            // If owner exists and is active, skip
            if (!string.IsNullOrEmpty(ownerStr))
            {
                try
                {
                    var ownerAddr = SiloAddress.FromParsableString(ownerStr);
                    var ownerStatus = _clusterMembership.CurrentSnapshot.GetSiloStatus(ownerAddr);
                    if (ownerStatus is not SiloStatus.Dead and not SiloStatus.None)
                    {
                        _logger.LogDebug("Shard {ShardId} owned by active silo {Owner}, skipping", shardId, ownerStr);
                        continue;
                    }
                }
                catch
                {
                    // If parsing fails, treat as orphan and try to claim
                }
            }

            // Try to claim orphaned shard
            _logger.LogInformation("Claiming orphaned shard {ShardId} (old owner: {Owner})", shardId, ownerStr);
            var expectedVersion = metadata.TryGetValue("version", out var v) ? v : "0";
            var took = await TryTakeOwnershipAsync(shardId, expectedVersion, _localSiloDetails.SiloAddress, _clusterMembership.CurrentSnapshot.Version, cancellationToken).ConfigureAwait(false);
            if (!took)
            {
                _logger.LogInformation("Failed to take ownership: another silo likely took shard {ShardId}", shardId);
                continue;
            }

            // instantiate shard and initialize
            var minDue = ParseDateTimeOffset(metadata, "MinDueTime", DateTimeOffset.MinValue);
            var maxDue = ParseDateTimeOffset(metadata, "MaxDueTime", DateTimeOffset.MaxValue);

            var shard = new RedisJobShard(shardId, minDue, maxDue, _multiplexer!, metadata, _options, _loggerFactory.CreateLogger<RedisJobShard>());
            try
            {
                await shard.InitializeAsync(cancellationToken).ConfigureAwait(false);
                // same behavior as Azure manager: shards just taken are not used to add new jobs
                await shard.MarkAsCompleteAsync(cancellationToken).ConfigureAwait(false);

                _jobShardCache[shardId] = shard;
                _logger.LogInformation("Shard {ShardId} assigned to this silo", shardId);
                result.Add(shard);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed initializing shard {ShardId} after taking ownership; releasing", shardId);
                try
                {
                    await ReleaseOwnershipAsync(shardId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception releaseEx)
                {
                    _logger.LogWarning(releaseEx, "Failed to release shard {ShardId} after failed init", shardId);
                }

                await shard.DisposeAsync();
            }
        }

        _logger.LogInformation("AssignJobShardsAsync completed, returning {Count} shards", result.Count);
        return result;
    }

    public override async Task<IJobShard> CreateShardAsync(DateTimeOffset minDueTime, DateTimeOffset maxDueTime, IDictionary<string, string> metadata, CancellationToken cancellationToken)
    {
        await InitializeIfNeeded(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Registering new shard (min={Min}, max={Max})", minDueTime, maxDueTime);

        var i = 0;
        while (true)
        {
            i++;
            var counter = Interlocked.Increment(ref _shardCounter);
            var shardId = $"{_options.ShardPrefix}-{minDueTime:yyyyMMddHHmm}-{_localSiloDetails.SiloAddress.ToParsableString()}-{counter}";
            var metaKey = MetaKeyForShard(shardId);

            var metadataInfo = new Dictionary<string, string>(metadata)
            {
                ["MinDueTime"] = minDueTime.ToString("o"),
                ["MaxDueTime"] = maxDueTime.ToString("o"),
                ["MembershipVersion"] = _clusterMembership.CurrentSnapshot.Version.Value.ToString(CultureInfo.InvariantCulture),
                ["Owner"] = _localSiloDetails.SiloAddress.ToParsableString(),
                ["Creator"] = _localSiloDetails.SiloAddress.ToParsableString(),
                ["version"] = "1"
            };

            try
            {
                var metadataJson = JsonSerializer.Serialize(metadataInfo);
                var res = (int)await _db!.ScriptEvaluateAsync(CreateShardLua,
                    new RedisKey[] { metaKey, ShardSetKey },
                    new RedisValue[] {
                        shardId,
                        metadataJson
                    }).ConfigureAwait(false);

                if (res == 0)
                {
                    _logger.LogWarning("Shard id collision for {ShardId}, retrying (attempt {Attempt})", shardId, i);
                    if (i >= _options.MaxShardCreationRetries) throw new InvalidOperationException($"Failed to create shard '{shardId}' after {i} attempts");
                    continue;
                }

                var shard = new RedisJobShard(shardId, minDueTime, maxDueTime, _multiplexer!, metadataInfo, _options, _loggerFactory.CreateLogger<RedisJobShard>());
                await shard.InitializeAsync(cancellationToken).ConfigureAwait(false);
                _jobShardCache[shardId] = shard;
                _logger.LogInformation("Shard {ShardId} registered and assigned to this silo", shardId);
                return shard;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error creating shard {ShardId} attempt {Attempt}", shardId, i);
                if (i >= _options.MaxShardCreationRetries) throw new InvalidOperationException($"Failed to create shard '{shardId}' after {i} attempts", ex);
            }
        }
    }

    public override async Task UnregisterShardAsync(IJobShard shard, CancellationToken cancellationToken)
    {
        if (shard is not RedisJobShard redisShard)
        {
            _logger.LogWarning("UnregisterShardAsync called with non-RedisJobShard instance; disposing generically");
            await shard.DisposeAsync();
            return;
        }

        var shardId = redisShard.Id;
        _logger.LogInformation("Unregistering shard {ShardId}", shardId);

        // Stop the background storage processor to ensure no more changes can happen
        await redisShard.StopProcessorAsync(cancellationToken).ConfigureAwait(false);

        // Now we can safely get a consistent view of the state
        var count = await shard.GetJobCountAsync().ConfigureAwait(false);

        // Remove from cache
        _jobShardCache.TryRemove(shardId, out _);

        if (count > 0)
        {
            // There are still jobs in the shard, just release ownership
            try
            {
                await ReleaseOwnershipAsync(shardId, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Released ownership of shard {ShardId} with {Count} remaining jobs", shardId, count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to release ownership for shard {ShardId}", shardId);
            }
        }
        else
        {
            // No jobs left, delete the shard data entirely
            try
            {
                await DeleteShardAsync(shardId, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Deleted shard {ShardId} (no remaining jobs)", shardId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete shard {ShardId}", shardId);
            }
        }

        // Dispose the shard's resources
        try
        {
            await redisShard.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disposing shard {ShardId}", shardId);
        }
    }

    private async Task DeleteShardAsync(string shardId, CancellationToken cancellationToken)
    {
        await InitializeIfNeeded(cancellationToken).ConfigureAwait(false);

        var streamKey = $"durablejobs:shard:{shardId}:stream";
        var metaKey = $"durablejobs:shard:{shardId}:meta";
        var leaseKey = $"durablejobs:shard:{shardId}:lease";

        // Delete all shard-related keys
        await _db!.KeyDeleteAsync(new RedisKey[] { streamKey, metaKey, leaseKey }).ConfigureAwait(false);

        // Remove from the shard set
        await _db.SetRemoveAsync(ShardSetKey, shardId).ConfigureAwait(false);
    }

    private async Task<bool> TryTakeOwnershipAsync(string shardId, string expectedVersion, SiloAddress newOwner, MembershipVersion membershipVersion, CancellationToken cancellationToken)
    {
        await InitializeIfNeeded(cancellationToken).ConfigureAwait(false);
        var metaKey = MetaKeyForShard(shardId);
        var res = (int)await _db!.ScriptEvaluateAsync(TryTakeOwnershipLua,
            new RedisKey[] { metaKey },
            new RedisValue[] { expectedVersion ?? "0", newOwner.ToParsableString(), membershipVersion.Value.ToString() }).ConfigureAwait(false);
        return res == 1;
    }

    private async Task<bool> ReleaseOwnershipAsync(string shardId, CancellationToken cancellationToken)
    {
        await InitializeIfNeeded(cancellationToken).ConfigureAwait(false);
        var metaKey = MetaKeyForShard(shardId);
        var entries = await _db!.HashGetAllAsync(metaKey).ConfigureAwait(false);
        var metadata = HashEntriesToDictionary(entries);
        var version = metadata.TryGetValue("version", out var v) ? v : "0";

        var res = (int)await _db.ScriptEvaluateAsync(ReleaseOwnershipLua, new RedisKey[] { metaKey }, new RedisValue[] { version }).ConfigureAwait(false);
        return res == 1;
    }

    private static IDictionary<string, string> HashEntriesToDictionary(HashEntry[] entries)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries) dict[e.Name.ToString()] = e.Value.ToString();
        return dict;
    }

    private static DateTimeOffset ParseDateTimeOffset(IDictionary<string, string> meta, string key, DateTimeOffset @default)
    {
        if (meta.TryGetValue(key, out var s) && DateTimeOffset.TryParse(s, null, DateTimeStyles.RoundtripKind, out var dt)) return dt;
        return @default;
    }
}
