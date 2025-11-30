using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
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
    private readonly IDatabase _db;
    private readonly Channel<StorageOperation> _storageOperationChannel;
    private readonly Task _storageProcessorTask;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly RedisJobShardOptions _options;
    private readonly ILogger<RedisJobShard> _logger;

    private readonly string _streamKey;
    private readonly string _metaKey;
    private readonly string _leaseKey;

    internal new IDictionary<string, string>? Metadata { get; private set; }
    internal long MetadataVersion { get; private set; }

    // Lua scripts
    private const string MultiXAddLua = @"
            local streamKey = KEYS[1]
            local n = tonumber(ARGV[1])
            local ids = {}
            local idx = 2
            for i=1,n do
                local payload = ARGV[idx]
                idx = idx + 1
                local id = redis.call('XADD', streamKey, '*', 'payload', payload)
                table.insert(ids, id)
            end
            return ids
        ";

    private const string UpdateMetaLua = @"
            local metaKey = KEYS[1]
            local expectedVersion = ARGV[1]
            local newVersion = ARGV[2]
            local fieldsJson = ARGV[3]
            local curr = redis.call('HGET', metaKey, 'version')
            if curr == false then curr = '' end
            if curr == expectedVersion then
                local obj = cjson.decode(fieldsJson)
                for k,v in pairs(obj) do
                    redis.call('HSET', metaKey, k, v)
                end
                redis.call('HSET', metaKey, 'version', newVersion)
                return 1
            else
                return 0
            end
        ";

    /// <summary>
    /// Initializes a new instance of the <see cref="RedisJobShard"/> class.
    /// </summary>
    /// <param name="shardId">The unique identifier for this shard.</param>
    /// <param name="startTime">The start time of the shard's time range.</param>
    /// <param name="endTime">The end time of the shard's time range.</param>
    /// <param name="redis">The Redis connection multiplexer.</param>
    /// <param name="options">The Redis job shard options.</param>
    /// <param name="logger">The logger.</param>
    public RedisJobShard(string shardId,
            DateTimeOffset startTime,
            DateTimeOffset endTime,
            IConnectionMultiplexer redis,
            RedisJobShardOptions options,
            ILogger<RedisJobShard> logger)
            : base(shardId, startTime, endTime)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _db = _redis.GetDatabase();
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

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
        var streamEntries = _db.StreamRange(_streamKey, "-", "+");

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
        await Task.CompletedTask.ConfigureAwait(false);
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
                        var success = await ExecuteUpdateMetadataAsync(firstOperation.Metadata!, firstOperation.ExpectedVersion);
                        if (!success)
                        {
                            throw new InvalidOperationException("Metadata CAS failed - version mismatch.");
                        }
                        LogMetadataUpdated(_logger, Id);
                        firstOperation.CompletionSource.TrySetResult(new object());
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

                // try gather more ops
                while (batchOperations.Count < _options.MaxBatchSize && _storageOperationChannel.Reader.TryRead(out var nextOp))
                {
                    if (nextOp.Type == StorageOperationType.UpdateMetadata)
                    {
                        // push metadata back to channel (best-effort)
                        _storageOperationChannel.Writer.TryWrite(nextOp);
                        break;
                    }
                    batchOperations.Add(nextOp);
                }

                // if batch is smaller than min, wait for flush interval
                if (batchOperations.Count < _options.MinBatchSize)
                {
                    LogWaitingForBatch(_logger, batchOperations.Count, _options.MinBatchSize, Id);
                    try
                    {
                        await Task.Delay(_options.BatchFlushInterval, cancellationToken);
                    }
                    catch (OperationCanceledException) { }
                    // try to take more after delay
                    while (batchOperations.Count < _options.MaxBatchSize && _storageOperationChannel.Reader.TryRead(out var next2))
                    {
                        if (next2.Type == StorageOperationType.UpdateMetadata)
                        {
                            _storageOperationChannel.Writer.TryWrite(next2);
                            break;
                        }
                        batchOperations.Add(next2);
                    }
                }

                if (batchOperations.Count > 0)
                {
                    try
                    {
                        LogFlushingBatch(_logger, batchOperations.Count, Id);
                        await AppendJobOperationBatchAsync(batchOperations, cancellationToken);
                        foreach (var op in batchOperations) op.CompletionSource.TrySetResult(new object());
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
    }

    private async Task AppendJobOperationBatchAsync(List<StorageOperation> operations, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        var jobOperations = operations.Select(op => op.JobOperation!.Value);
        var payloads = RedisStreamJsonSerializer<JobOperation>.Encode(jobOperations, JobOperationJsonContext.Default.JobOperation);
        var args = new RedisValue[1 + payloads.Length];
        args[0] = payloads.Length;
        for (int i = 0; i < payloads.Length; i++) args[i + 1] = payloads[i];

        var result = await _db.ScriptEvaluateAsync(MultiXAddLua, new RedisKey[] { _streamKey }, args);
        // result is array of ids, but we don't use them for now
        sw.Stop();
        LogBatchWritten(_logger, operations.Count, Id, sw.ElapsedMilliseconds, -1);
    }

    private async Task<bool> ExecuteUpdateMetadataAsync(IDictionary<string, string> metadata, long expectedVersion)
    {
        var newVersion = (MetadataVersion + 1).ToString();
        var fieldsJson = JsonSerializer.Serialize(metadata);
        var res = await _db.ScriptEvaluateAsync(UpdateMetaLua, new RedisKey[] { _metaKey }, new RedisValue[] { expectedVersion.ToString(), newVersion, fieldsJson });
        var ok = (int)res == 1;
        if (ok)
        {
            // update local view
            Metadata = new Dictionary<string, string>(metadata);
            Metadata["version"] = newVersion;
            MetadataVersion = long.Parse(newVersion);
        }
        return ok;
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
    public TaskCompletionSource<object?> CompletionSource { get; init; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
