using System;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans.DurableJobs;

internal partial class LocalDurableJobManager
{
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Scheduling job '{JobName}' for grain {TargetGrain} at {DueTime}"
    )]
    private static partial void LogSchedulingJob(ILogger logger, string jobName, GrainId targetGrain, DateTimeOffset dueTime);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Job '{JobName}' (ID: {JobId}) scheduled to shard {ShardId} for grain {TargetGrain}"
    )]
    private static partial void LogJobScheduled(ILogger logger, string jobName, string jobId, string shardId, GrainId targetGrain);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "LocalDurableJobManager starting"
    )]
    private static partial void LogStarting(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "LocalDurableJobManager started"
    )]
    private static partial void LogStarted(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "LocalDurableJobManager stopping. Running shards: {RunningShardCount}"
    )]
    private static partial void LogStopping(ILogger logger, int runningShardCount);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "LocalDurableJobManager stopped"
    )]
    private static partial void LogStopped(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Attempting to cancel job {JobId} (Name: '{JobName}') in shard {ShardId}"
    )]
    private static partial void LogCancellingJob(ILogger logger, string jobId, string jobName, string shardId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to cancel job {JobId} (Name: '{JobName}') - shard {ShardId} not found in cache"
    )]
    private static partial void LogJobCancellationFailed(ILogger logger, string jobId, string jobName, string shardId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Job {JobId} (Name: '{JobName}') cancelled from shard {ShardId}"
    )]
    private static partial void LogJobCancelled(ILogger logger, string jobId, string jobName, string shardId);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error processing cluster membership update"
    )]
    private static partial void LogErrorProcessingClusterMembership(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Checking for unassigned shards"
    )]
    private static partial void LogCheckingForUnassignedShards(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Assigned {ShardCount} shard(s)"
    )]
    private static partial void LogAssignedShards(ILogger logger, int shardCount);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "No unassigned shards found"
    )]
    private static partial void LogNoShardsToAssign(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Starting shard {ShardId} (Start: {StartTime}, End: {EndTime})"
    )]
    private static partial void LogStartingShard(ILogger logger, string shardId, DateTimeOffset startTime, DateTimeOffset endTime);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Shard {ShardId} not ready yet. Start time: {StartTime}"
    )]
    private static partial void LogShardNotReadyYet(ILogger logger, string shardId, DateTimeOffset startTime);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "Checking for pending shards to start"
    )]
    private static partial void LogCheckingPendingShards(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error in periodic shard check"
    )]
    private static partial void LogErrorInPeriodicCheck(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Unregistered shard {ShardId}"
    )]
    private static partial void LogUnregisteredShard(ILogger logger, string shardId);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error unregistering shard {ShardId}"
    )]
    private static partial void LogErrorUnregisteringShard(ILogger logger, Exception exception, string shardId);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error disposing shard {ShardId}"
    )]
    private static partial void LogErrorDisposingShard(ILogger logger, Exception exception, string shardId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Creating new shard for key {ShardKey}"
    )]
    private static partial void LogCreatingNewShard(ILogger logger, DateTimeOffset shardKey);
}
