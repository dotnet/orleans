using System;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans.DurableJobs.Redis;

public sealed partial class RedisJobShardManager
{
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Initializing RedisJobShardManager (shardPrefix={Prefix})"
    )]
    private static partial void LogInitializing(ILogger logger, string prefix);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "RedisJobShardManager initialized"
    )]
    private static partial void LogInitialized(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Assigning shards up to {MaxShardStartTime} (prefix={Prefix})"
    )]
    private static partial void LogAssigningShards(ILogger logger, DateTimeOffset maxShardStartTime, string prefix);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Shard {ShardId} is too new (start {Start}) > max {Max}, skipping"
    )]
    private static partial void LogShardTooNew(ILogger logger, string shardId, DateTimeOffset start, DateTimeOffset max);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Shard {ShardId} is owned by this silo and in cache"
    )]
    private static partial void LogShardOwnedByThisSilo(ILogger logger, string shardId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Shard {ShardId} metadata says owned by this silo but not in cache; releasing ownership"
    )]
    private static partial void LogShardOwnedButNotInCache(ILogger logger, string shardId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to release ownership of shard {ShardId}"
    )]
    private static partial void LogFailedToReleaseOwnership(ILogger logger, Exception exception, string shardId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Shard {ShardId} owned by active silo {Owner}, skipping"
    )]
    private static partial void LogShardOwnedByActiveSilo(ILogger logger, string shardId, string owner);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Claiming orphaned shard {ShardId} (old owner: {Owner})"
    )]
    private static partial void LogClaimingOrphanedShard(ILogger logger, string shardId, string? owner);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Failed to take ownership: another silo likely took shard {ShardId}"
    )]
    private static partial void LogFailedToTakeOwnership(ILogger logger, string shardId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Shard {ShardId} assigned to this silo"
    )]
    private static partial void LogShardAssigned(ILogger logger, string shardId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed initializing shard {ShardId} after taking ownership; releasing"
    )]
    private static partial void LogFailedInitializingShard(ILogger logger, Exception exception, string shardId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to release shard {ShardId} after failed init"
    )]
    private static partial void LogFailedToReleaseShardAfterFailedInit(ILogger logger, Exception exception, string shardId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "AssignJobShardsAsync completed, returning {Count} shards"
    )]
    private static partial void LogAssignmentCompleted(ILogger logger, int count);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Registering new shard (min={Min}, max={Max})"
    )]
    private static partial void LogRegisteringShard(ILogger logger, DateTimeOffset min, DateTimeOffset max);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Shard id collision for {ShardId}, retrying (attempt {Attempt})"
    )]
    private static partial void LogShardIdCollision(ILogger logger, string shardId, int attempt);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Shard {ShardId} registered and assigned to this silo"
    )]
    private static partial void LogShardRegistered(ILogger logger, string shardId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Error creating shard {ShardId} attempt {Attempt}"
    )]
    private static partial void LogErrorCreatingShard(ILogger logger, Exception exception, string shardId, int attempt);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "UnregisterShardAsync called with non-RedisJobShard instance; disposing generically"
    )]
    private static partial void LogUnregisterNonRedisJobShard(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Unregistering shard {ShardId}"
    )]
    private static partial void LogUnregisteringShard(ILogger logger, string shardId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Released ownership of shard {ShardId} with {Count} remaining jobs"
    )]
    private static partial void LogShardOwnershipReleased(ILogger logger, string shardId, int count);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to release ownership for shard {ShardId}"
    )]
    private static partial void LogFailedToReleaseOwnershipForShard(ILogger logger, Exception exception, string shardId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Deleted shard {ShardId} (no remaining jobs)"
    )]
    private static partial void LogShardDeleted(ILogger logger, string shardId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to delete shard {ShardId}"
    )]
    private static partial void LogFailedToDeleteShard(ILogger logger, Exception exception, string shardId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Error disposing shard {ShardId}"
    )]
    private static partial void LogErrorDisposingShard(ILogger logger, Exception exception, string shardId);
}
