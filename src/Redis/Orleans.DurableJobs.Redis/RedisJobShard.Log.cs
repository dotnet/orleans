using System;
using Microsoft.Extensions.Logging;

namespace Orleans.DurableJobs.Redis;

internal sealed partial class RedisJobShard
{
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Initializing shard '{ShardId}' from Redis Stream {StreamKey}"
    )]
    private static partial void LogInitializingShard(ILogger logger, string shardId, string streamKey);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Shard '{ShardId}' initialized from Redis: loaded {JobCount} job(s) in {ElapsedMilliseconds}ms"
    )]
    private static partial void LogShardInitialized(ILogger logger, string shardId, int jobCount, long elapsedMilliseconds);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Adding job '{JobId}' (Name: '{JobName}') to shard '{ShardId}' with due time {DueTime}"
    )]
    private static partial void LogAddingJob(ILogger logger, string jobId, string jobName, string shardId, DateTimeOffset dueTime);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Removing job '{JobId}' from shard '{ShardId}'"
    )]
    private static partial void LogRemovingJob(ILogger logger, string jobId, string shardId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Retrying job '{JobId}' in shard '{ShardId}' with new due time {NewDueTime}"
    )]
    private static partial void LogRetryingJob(ILogger logger, string jobId, string shardId, DateTimeOffset newDueTime);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "Flushing batch of {OperationCount} job operation(s) to shard '{ShardId}'"
    )]
    private static partial void LogFlushingBatch(ILogger logger, int operationCount, string shardId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Batch of {OperationCount} job operation(s) written to shard '{ShardId}' in {ElapsedMilliseconds}ms. Total committed blocks: {CommittedBlockCount}"
    )]
    private static partial void LogBatchWritten(ILogger logger, int operationCount, string shardId, long elapsedMilliseconds, int committedBlockCount);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "Updating metadata for shard '{ShardId}'"
    )]
    private static partial void LogUpdatingMetadata(ILogger logger, string shardId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Metadata updated for shard '{ShardId}'"
    )]
    private static partial void LogMetadataUpdated(ILogger logger, string shardId);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error writing batch of {OperationCount} operation(s) to shard '{ShardId}'"
    )]
    private static partial void LogErrorWritingBatch(ILogger logger, Exception exception, int operationCount, string shardId);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error updating metadata for shard '{ShardId}'"
    )]
    private static partial void LogErrorUpdatingMetadata(ILogger logger, Exception exception, string shardId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Stopping storage processor for shard '{ShardId}'"
    )]
    private static partial void LogStoppingProcessor(ILogger logger, string shardId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Storage processor stopped for shard '{ShardId}'"
    )]
    private static partial void LogProcessorStopped(ILogger logger, string shardId);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "Processing storage operation queue for shard '{ShardId}'"
    )]
    private static partial void LogProcessingStorageQueue(ILogger logger, string shardId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Waiting for additional operations to batch (current size: {CurrentSize}, min size: {MinSize}) for shard '{ShardId}'"
    )]
    private static partial void LogWaitingForBatch(ILogger logger, int currentSize, int minSize, string shardId);
}
