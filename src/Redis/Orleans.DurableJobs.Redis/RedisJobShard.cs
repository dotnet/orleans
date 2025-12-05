using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;
using Orleans.Runtime;
using StackExchange.Redis;

namespace Orleans.DurableJobs.Redis;

internal sealed partial class RedisJobShard : JobShard
{
    private readonly IConnectionMultiplexer _redis;
    private readonly RedisOperationsManager _redisOps;
    private readonly Channel<StorageOperation> _storageOperationChannel;
    private readonly Task _storageProcessorTask;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly RedisJobShardOptions _options;
    private readonly ILogger<RedisJobShard> _logger;

    private readonly string _streamKey;
    private readonly string _metaKey;
    private readonly string _leaseKey;

    internal long MetadataVersion { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="RedisJobShard"/> class.
    /// </summary>
    /// <param name="shardId">The unique identifier for this shard.</param>
    /// <param name="startTime">The start time of the shard's time range.</param>
    /// <param name="endTime">The end time of the shard's time range.</param>
    /// <param name="redis">The Redis connection multiplexer.</param>
    /// <param name="metadata">The shard metadata.</param>
    /// <param name="options">The Redis job shard options.</param>
    /// <param name="logger">The logger.</param>
    public RedisJobShard(string shardId,
            DateTimeOffset startTime,
            DateTimeOffset endTime,
            IConnectionMultiplexer redis,
            IDictionary<string, string> metadata,
            RedisJobShardOptions options,
            ILogger<RedisJobShard> logger)
            : base(shardId, startTime, endTime)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        var db = _redis.GetDatabase();
        _redisOps = new RedisOperationsManager(db);
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Metadata = metadata;

        // Initialize metadata version from metadata dictionary
        if (metadata.TryGetValue("version", out var versionStr) && long.TryParse(versionStr, out var version))
        {
            MetadataVersion = version;
        }

        _streamKey = $"durablejobs:shard:{Id}:stream";
        _metaKey = $"durablejobs:shard:{Id}:meta";
        _leaseKey = $"durablejobs:shard:{Id}:lease";

        _storageOperationChannel = Channel.CreateUnbounded<StorageOperation>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _storageProcessorTask = ProcessStorageOperationsAsync();
    }

    // Initialize: replay the Redis stream and rebuild in-memory queue (same logic as Azure)
    // TODO: Reviewed
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        LogInitializingShard(_logger, Id, _streamKey);
        var sw = Stopwatch.StartNew();

        // Replay stream from beginning
        var addedJobs = new Dictionary<string, JobOperation>();
        var deletedJobs = new HashSet<string>();
        var jobRetryCounters = new Dictionary<string, (int dequeueCount, DateTimeOffset? newDueTime)>();

        // StreamRange with "-" .. "+" returns all entries. If stream does not exist, returns empty.
        var streamEntries = _redisOps.StreamRange(_streamKey, "-", "+");

        await foreach (var operation in RedisStreamJsonSerializer<JobOperation>.DecodeAsync(streamEntries, JobOperationJsonContext.Default.JobOperation, cancellationToken))
        {
            switch (operation.Type)
            {
                case JobOperation.OperationType.Add:
                    if (!deletedJobs.Contains(operation.Id))
                    {
                        addedJobs[operation.Id] = operation;
                    }
                    break;
                case JobOperation.OperationType.Remove:
                    deletedJobs.Add(operation.Id);
                    addedJobs.Remove(operation.Id);
                    jobRetryCounters.Remove(operation.Id);
                    break;
                case JobOperation.OperationType.Retry:
                    if (!deletedJobs.Contains(operation.Id))
                    {
                        if (!jobRetryCounters.ContainsKey(operation.Id))
                        {
                            jobRetryCounters[operation.Id] = (1, operation.DueTime);
                        }
                        else
                        {
                            var entry2 = jobRetryCounters[operation.Id];
                            jobRetryCounters[operation.Id] = (entry2.dequeueCount + 1, operation.DueTime);
                        }
                    }
                    break;
            }
        }

        // Rebuild the priority queue in memory (use EnqueueJob)
        foreach (var op in addedJobs.Values)
        {
            var retryCounter = 0;
            var dueTime = op.DueTime!.Value;
            if (jobRetryCounters.TryGetValue(op.Id, out var retryEntries))
            {
                retryCounter = retryEntries.dequeueCount;
                dueTime = retryEntries.newDueTime ?? dueTime;
            }

            EnqueueJob(new DurableJob
            {
                Id = op.Id,
                Name = op.Name!,
                DueTime = dueTime,
                TargetGrainId = op.TargetGrainId!.Value,
                ShardId = Id,
                Metadata = op.Metadata,
            }, retryCounter);
        }

        sw.Stop();
        LogShardInitialized(_logger, Id, addedJobs.Count, sw.ElapsedMilliseconds);
    }
    protected override async Task PersistAddJobAsync(string jobId, string jobName, DateTimeOffset dueTime, GrainId target, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken)
    {
        LogAddingJob(_logger, jobId, jobName, Id, dueTime);
        var operation = JobOperation.CreateAddOperation(jobId, jobName, dueTime, target, metadata);
        await EnqueueStorageOperationAsync(StorageOperation.CreateAppendOperation(operation), cancellationToken);
    }

    protected override async Task PersistRemoveJobAsync(string jobId, CancellationToken cancellationToken)
    {
        LogRemovingJob(_logger, jobId, Id);
        var operation = JobOperation.CreateRemoveOperation(jobId);
        await EnqueueStorageOperationAsync(StorageOperation.CreateAppendOperation(operation), cancellationToken);
    }

    protected override async Task PersistRetryJobAsync(string jobId, DateTimeOffset newDueTime, CancellationToken cancellationToken)
    {
        LogRetryingJob(_logger, jobId, Id, newDueTime);
        var operation = JobOperation.CreateRetryOperation(jobId, newDueTime);
        await EnqueueStorageOperationAsync(StorageOperation.CreateAppendOperation(operation), cancellationToken);
    }

    public async Task UpdateShardMetadataAsync(IDictionary<string, string> metadata, long expectedVersion, CancellationToken cancellationToken)
    {
        LogUpdatingMetadata(_logger, Id);
        await EnqueueStorageOperationAsync(StorageOperation.CreateMetadataOperation(metadata, expectedVersion), cancellationToken);
    }

    private async Task EnqueueStorageOperationAsync(StorageOperation operation, CancellationToken cancellationToken)
    {
        await _storageOperationChannel.Writer.WriteAsync(operation, cancellationToken);
        await operation.CompletionSource.Task;
    }

    private async Task ProcessStorageOperationsAsync()
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.ForceYielding);
        var cancellationToken = _shutdownCts.Token;
        var batchOperations = new List<StorageOperation>(_options.MaxBatchSize);

        try
        {
            while (await _storageOperationChannel.Reader.WaitToReadAsync(cancellationToken))
            {
                if (!_storageOperationChannel.Reader.TryRead(out var firstOperation))
                {
                    continue;
                }

                // Metadata ops are handled immediately and atomically via Lua
                if (firstOperation.Type == StorageOperationType.UpdateMetadata)
                {
                    try
                    {
                        var success = await _redisOps.UpdateMetadataAsync(_metaKey, firstOperation.Metadata!, firstOperation.ExpectedVersion).ConfigureAwait(false);
                        if (!success)
                        {
                            throw new InvalidOperationException("Metadata CAS failed - version mismatch.");
                        }

                        // Update local metadata tracking
                        Metadata = new Dictionary<string, string>(firstOperation.Metadata!);
                        var newVersion = (firstOperation.ExpectedVersion + 1).ToString();
                        Metadata["version"] = newVersion;
                        MetadataVersion = long.Parse(newVersion);

                        LogMetadataUpdated(_logger, Id);
                        firstOperation.CompletionSource.TrySetResult();
                    }
                    catch (Exception ex)
                    {
                        LogErrorUpdatingMetadata(_logger, ex, Id);
                        firstOperation.CompletionSource.TrySetException(ex);
                    }
                    continue;
                }

                // collect job ops for batch
                batchOperations.Add(firstOperation);

                // Try to collect more operations up to the maximum batch size
                if (TryCollectJobOperationsForBatch(batchOperations))
                {
                    // Not enough operations to meet the minimum batch size, wait for more or timeout
                    if (batchOperations.Count < _options.MinBatchSize)
                    {
                        LogWaitingForBatch(_logger, batchOperations.Count, _options.MinBatchSize, Id);
                    }
                    await Task.Delay(_options.BatchFlushInterval, cancellationToken);
                    TryCollectJobOperationsForBatch(batchOperations);
                }

                if (batchOperations.Count > 0)
                {
                    try
                    {
                        LogFlushingBatch(_logger, batchOperations.Count, Id);
                        await AppendJobOperationBatchAsync(batchOperations, cancellationToken);
                        foreach (var op in batchOperations) op.CompletionSource.TrySetResult();
                    }
                    catch (Exception ex)
                    {
                        LogErrorWritingBatch(_logger, ex, batchOperations.Count, Id);
                        foreach (var op in batchOperations) op.CompletionSource.TrySetException(ex);
                    }
                    finally
                    {
                        batchOperations.Clear();
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            while (_storageOperationChannel.Reader.TryRead(out var op))
            {
                op.CompletionSource?.TrySetCanceled(cancellationToken);
            }
        }

        // Local function to collect job operations for batching. Returns true if more operations can be collected.
        bool TryCollectJobOperationsForBatch(List<StorageOperation> batchOperations)
        {
            // Collect more jobs, up to a maximum batch size
            while (batchOperations.Count < _options.MaxBatchSize && _storageOperationChannel.Reader.TryPeek(out var nextOperation))
            {
                if (nextOperation.Type is StorageOperationType.UpdateMetadata)
                {
                    // Stop batching if we encounter a metadata operation
                    return false;
                }
                _storageOperationChannel.Reader.TryRead(out var operation);
                Debug.Assert(operation != null);
                batchOperations.Add(operation!);
            }
            return batchOperations.Count != _options.MaxBatchSize;
        }
    }

    private async Task AppendJobOperationBatchAsync(List<StorageOperation> operations, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        var jobOperations = operations.Select(op => op.JobOperation!.Value);
        var payloads = RedisStreamJsonSerializer<JobOperation>.Encode(jobOperations, JobOperationJsonContext.Default.JobOperation);

        var result = await _redisOps.AppendJobOperationBatchAsync(_streamKey, payloads).ConfigureAwait(false);
        // result is array of ids, but we don't use them for now
        sw.Stop();
        LogBatchWritten(_logger, operations.Count, Id, sw.ElapsedMilliseconds, -1);
    }

    internal async Task StopProcessorAsync(CancellationToken cancellationToken)
    {
        LogStoppingProcessor(_logger, Id);

        if (_storageOperationChannel.Writer.TryComplete())
        {
            _shutdownCts.Cancel();
        }

        try
        {
            await _storageProcessorTask.WaitAsync(cancellationToken);
            LogProcessorStopped(_logger, Id);
        }
        catch (OperationCanceledException)
        {
            LogProcessorStopped(_logger, Id);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await StopProcessorAsync(CancellationToken.None);
        _shutdownCts.Dispose();
        await base.DisposeAsync();
    }
}

internal enum StorageOperationType
{
    AppendJobOperation,
    UpdateMetadata
}

internal sealed class StorageOperation
{
    public required StorageOperationType Type { get; init; }
    public JobOperation? JobOperation { get; init; }
    public IDictionary<string, string>? Metadata { get; init; }
    public long ExpectedVersion { get; init; }
    public TaskCompletionSource CompletionSource { get; init; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static StorageOperation CreateAppendOperation(JobOperation jobOperation)
    {
        return new StorageOperation
        {
            Type = StorageOperationType.AppendJobOperation,
            JobOperation = jobOperation
        };
    }

    public static StorageOperation CreateMetadataOperation(IDictionary<string, string> metadata, long expectedVersion)
    {
        return new StorageOperation
        {
            Type = StorageOperationType.UpdateMetadata,
            Metadata = metadata,
            ExpectedVersion = expectedVersion
        };
    }
}
