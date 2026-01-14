using System;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans.DurableJobs;

internal sealed partial class ShardExecutor
{
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
        Level = LogLevel.Debug,
        Message = "Shard {ShardId} processing cancelled"
    )]
    private static partial void LogShardCancelled(ILogger logger, string shardId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Overload detected for shard {ShardId}, pausing job processing"
    )]
    private static partial void LogOverloadDetected(ILogger logger, string shardId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Overload cleared for shard {ShardId}, resuming job processing"
    )]
    private static partial void LogOverloadCleared(ILogger logger, string shardId);
}
