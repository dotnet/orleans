using System;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans.ScheduledJobs;

internal partial class LocalScheduledJobManager
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
        Message = "Creating new shard for key {ShardKey}"
    )]
    private static partial void LogCreatingNewShard(ILogger logger, DateTimeOffset shardKey);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "LocalScheduledJobManager starting"
    )]
    private static partial void LogStarting(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "LocalScheduledJobManager started"
    )]
    private static partial void LogStarted(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "LocalScheduledJobManager stopping. Running shards: {RunningShardCount}"
    )]
    private static partial void LogStopping(ILogger logger, int runningShardCount);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "LocalScheduledJobManager stopped"
    )]
    private static partial void LogStopped(ILogger logger);

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
        Message = "Waiting {Delay} for shard {ShardId} start time {StartTime}"
    )]
    private static partial void LogWaitingForShardStartTime(ILogger logger, string shardId, TimeSpan delay, DateTimeOffset startTime);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Begin processing shard {ShardId}"
    )]
    private static partial void LogBeginProcessingShard(ILogger logger, string shardId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Executing job {JobId} (Name: '{JobName}') for grain {TargetGrain}, due at {DueTime}"
    )]
    private static partial void LogExecutingJob(ILogger logger, string jobId, string jobName, GrainId targetGrain, DateTimeOffset dueTime);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Job {JobId} (Name: '{JobName}') executed successfully"
    )]
    private static partial void LogJobExecutedSuccessfully(ILogger logger, string jobId, string jobName);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error executing job {JobId}"
    )]
    private static partial void LogErrorExecutingJob(ILogger logger, Exception exception, string jobId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Retrying job {JobId} (Name: '{JobName}') at {RetryTime}. Dequeue count: {DequeueCount}"
    )]
    private static partial void LogRetryingJob(ILogger logger, string jobId, string jobName, DateTimeOffset retryTime, int dequeueCount);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Job {JobId} (Name: '{JobName}') failed after {DequeueCount} attempts and will not be retried"
    )]
    private static partial void LogJobFailedNoRetry(ILogger logger, string jobId, string jobName, int dequeueCount);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Completed processing shard {ShardId}"
    )]
    private static partial void LogCompletedProcessingShard(ILogger logger, string shardId);

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
        Level = LogLevel.Debug,
        Message = "Shard {ShardId} processing cancelled"
    )]
    private static partial void LogShardCancelled(ILogger logger, string shardId);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error disposing shard {ShardId}"
    )]
    private static partial void LogErrorDisposingShard(ILogger logger, Exception exception, string shardId);

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
}
