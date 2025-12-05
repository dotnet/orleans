using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
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
    private RedisOperationsManager? _redisOps;

    // in-memory cache of owned shards
    private readonly ConcurrentDictionary<string, RedisJobShard> _jobShardCache = new();

    private long _shardCounter = 0;

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
        if (_redisOps is not null)
        {
            return;
        }

        LogInitializing(_logger, _options.ShardPrefix);
        _multiplexer = await _options.CreateMultiplexer(_options).ConfigureAwait(false);
        var db = _multiplexer.GetDatabase();
        _redisOps = new RedisOperationsManager(db);
        LogInitialized(_logger);
    }

    private string ShardSetKey => $"durablejobs:shards:{_options.ShardPrefix}";
    private static string MetaKeyForShard(string shardId) => $"durablejobs:shard:{shardId}:meta";

    public override async Task<List<IJobShard>> AssignJobShardsAsync(DateTimeOffset maxShardStartTime, CancellationToken cancellationToken)
    {
        await InitializeIfNeeded(cancellationToken).ConfigureAwait(false);
        LogAssigningShards(_logger, maxShardStartTime, _options.ShardPrefix);

        var result = new List<IJobShard>();

        // get all shard ids
        var shardIds = await _redisOps!.GetSetMembersAsync(ShardSetKey).ConfigureAwait(false);

        foreach (var shardId in shardIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var metaKey = MetaKeyForShard(shardId);
            var metadata = await _redisOps.GetHashAllAsync(metaKey).ConfigureAwait(false);

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
                    LogShardTooNew(_logger, shardId, shardStartTime, maxShardStartTime);
                    continue;
                }
            }

            // If I am the owner
            if (!string.IsNullOrEmpty(ownerStr) && ownerStr == _localSiloDetails.SiloAddress.ToParsableString())
            {
                if (_jobShardCache.TryGetValue(shardId, out var cached))
                {
                    LogShardOwnedByThisSilo(_logger, shardId);
                    result.Add(cached);
                    continue;
                }
                else
                {
                    LogShardOwnedButNotInCache(_logger, shardId);
                    try
                    {
                        await ReleaseOwnershipAsync(shardId, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        LogFailedToReleaseOwnership(_logger, ex, shardId);
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
                        LogShardOwnedByActiveSilo(_logger, shardId, ownerStr);
                        continue;
                    }
                }
                catch
                {
                    // If parsing fails, treat as orphan and try to claim
                }
            }

            // Try to claim orphaned shard
            LogClaimingOrphanedShard(_logger, shardId, ownerStr);
            var expectedVersion = metadata.TryGetValue("version", out var v) ? v : "0";
            var took = await _redisOps!.TryTakeOwnershipAsync(
                metaKey,
                expectedVersion,
                _localSiloDetails.SiloAddress.ToParsableString(),
                _clusterMembership.CurrentSnapshot.Version.Value.ToString()).ConfigureAwait(false);
            if (!took)
            {
                LogFailedToTakeOwnership(_logger, shardId);
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
                LogShardAssigned(_logger, shardId);
                result.Add(shard);
            }
            catch (Exception ex)
            {
                LogFailedInitializingShard(_logger, ex, shardId);
                try
                {
                    await ReleaseOwnershipAsync(shardId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception releaseEx)
                {
                    LogFailedToReleaseShardAfterFailedInit(_logger, releaseEx, shardId);
                }

                await shard.DisposeAsync();
            }
        }

        LogAssignmentCompleted(_logger, result.Count);
        return result;
    }

    public override async Task<IJobShard> CreateShardAsync(DateTimeOffset minDueTime, DateTimeOffset maxDueTime, IDictionary<string, string> metadata, CancellationToken cancellationToken)
    {
        await InitializeIfNeeded(cancellationToken).ConfigureAwait(false);
        LogRegisteringShard(_logger, minDueTime, maxDueTime);

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
                var created = await _redisOps!.CreateShardAsync(metaKey, ShardSetKey, shardId, metadataInfo).ConfigureAwait(false);

                if (!created)
                {
                    LogShardIdCollision(_logger, shardId, i);
                    if (i >= _options.MaxShardCreationRetries)
                    {
                        throw new InvalidOperationException($"Failed to create shard '{shardId}' after {i} attempts");
                    }
                    continue;
                }

                var shard = new RedisJobShard(shardId, minDueTime, maxDueTime, _multiplexer!, metadataInfo, _options, _loggerFactory.CreateLogger<RedisJobShard>());
                await shard.InitializeAsync(cancellationToken).ConfigureAwait(false);
                _jobShardCache[shardId] = shard;
                LogShardRegistered(_logger, shardId);
                return shard;
            }
            catch (Exception ex)
            {
                LogErrorCreatingShard(_logger, ex, shardId, i);
                if (i >= _options.MaxShardCreationRetries)
                {
                    throw new InvalidOperationException($"Failed to create shard '{shardId}' after {i} attempts", ex);
                }
            }
        }
    }

    public override async Task UnregisterShardAsync(IJobShard shard, CancellationToken cancellationToken)
    {
        if (shard is not RedisJobShard redisShard)
        {
            LogUnregisterNonRedisJobShard(_logger);
            await shard.DisposeAsync();
            return;
        }

        var shardId = redisShard.Id;
        LogUnregisteringShard(_logger, shardId);

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
                LogShardOwnershipReleased(_logger, shardId, count);
            }
            catch (Exception ex)
            {
                LogFailedToReleaseOwnershipForShard(_logger, ex, shardId);
            }
        }
        else
        {
            // No jobs left, delete the shard data entirely
            try
            {
                await DeleteShardAsync(shardId, cancellationToken).ConfigureAwait(false);
                LogShardDeleted(_logger, shardId);
            }
            catch (Exception ex)
            {
                LogFailedToDeleteShard(_logger, ex, shardId);
            }
        }

        // Dispose the shard's resources
        try
        {
            await redisShard.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogErrorDisposingShard(_logger, ex, shardId);
        }
    }

    private async Task DeleteShardAsync(string shardId, CancellationToken cancellationToken)
    {
        await InitializeIfNeeded(cancellationToken).ConfigureAwait(false);

        var streamKey = $"durablejobs:shard:{shardId}:stream";
        var metaKey = $"durablejobs:shard:{shardId}:meta";
        var leaseKey = $"durablejobs:shard:{shardId}:lease";

        // Delete all shard-related keys
        await _redisOps!.DeleteKeysAsync(new RedisKey[] { streamKey, metaKey, leaseKey }).ConfigureAwait(false);

        // Remove from the shard set
        await _redisOps.SetRemoveAsync(ShardSetKey, shardId).ConfigureAwait(false);
    }

    private async Task<bool> ReleaseOwnershipAsync(string shardId, CancellationToken cancellationToken)
    {
        await InitializeIfNeeded(cancellationToken).ConfigureAwait(false);
        var metaKey = MetaKeyForShard(shardId);
        var metadata = await _redisOps!.GetHashAllAsync(metaKey).ConfigureAwait(false);
        var version = metadata.TryGetValue("version", out var v) ? v : "0";

        return await _redisOps.ReleaseOwnershipAsync(metaKey, version).ConfigureAwait(false);
    }

    private static DateTimeOffset ParseDateTimeOffset(IDictionary<string, string> meta, string key, DateTimeOffset @default)
    {
        if (meta.TryGetValue(key, out var s) && DateTimeOffset.TryParse(s, null, DateTimeStyles.RoundtripKind, out var dt))
        {
            return dt;
        }
        return @default;
    }
}
