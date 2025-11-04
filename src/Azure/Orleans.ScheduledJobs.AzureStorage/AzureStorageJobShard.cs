using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Transactions;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Serialization.Buffers.Adaptors;

namespace Orleans.ScheduledJobs.AzureStorage;

internal sealed class AzureStorageJobShard : JobShard
{
    private readonly Channel<StorageOperation> _storageOperationChannel;
    private readonly Task _storageProcessorTask;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly AzureStorageJobShardOptions _options;

    internal AppendBlobClient BlobClient { get; init; }
    internal ETag? ETag { get; private set; }
    internal int CommitedBlockCount { get; private set; }

    public AzureStorageJobShard(string id, DateTimeOffset startTime, DateTimeOffset endTime, AppendBlobClient blobClient, IDictionary<string, string>? metadata, ETag? eTag, AzureStorageJobShardOptions options)
        : base(id, startTime, endTime)
    {
        BlobClient = blobClient;
        ETag = eTag;
        Metadata = metadata;
        _options = options;
        
        // Create unbounded channel for storage operations
        _storageOperationChannel = Channel.CreateUnbounded<StorageOperation>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        
        // Start the background task that processes storage operations
        _storageProcessorTask = ProcessStorageOperationsAsync();
    }

    protected override async Task PersistAddJobAsync(string jobId, string jobName, DateTimeOffset dueTime, GrainId target, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken)
    {
        var operation = JobOperation.CreateAddOperation(jobId, jobName, dueTime, target, metadata);
        await EnqueueStorageOperationAsync(StorageOperation.CreateAppendOperation(operation), cancellationToken);
    }

    protected override async Task PersistRemoveJobAsync(string jobId, CancellationToken cancellationToken)
    {
        var operation = JobOperation.CreateRemoveOperation(jobId);
        await EnqueueStorageOperationAsync(StorageOperation.CreateAppendOperation(operation), cancellationToken);
    }

    protected override async Task PersistRetryJobAsync(string jobId, DateTimeOffset newDueTime, CancellationToken cancellationToken)
    {
        var operation = JobOperation.CreateRetryOperation(jobId, newDueTime);
        await EnqueueStorageOperationAsync(StorageOperation.CreateAppendOperation(operation), cancellationToken);
    }

    public async Task UpdateBlobMetadata(IDictionary<string, string> metadata, CancellationToken cancellationToken)
    {
        await EnqueueStorageOperationAsync(StorageOperation.CreateMetadataOperation(metadata), cancellationToken);
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        // Load existing blob
        var response = await BlobClient.DownloadAsync(cancellationToken: cancellationToken);
        using var stream = response.Value.Content;

        // Rebuild state by replaying operations
        var addedJobs = new Dictionary<string, JobOperation>();
        var deletedJobs = new HashSet<string>();
        var jobRetryCounters = new Dictionary<string, (int dequeueCount, DateTimeOffset? newDueTime)>();

        await foreach (var operation in NetstringJsonSerializer<JobOperation>.DecodeAsync(stream, JobOperationJsonContext.Default.JobOperation, cancellationToken))
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
                            var entry = jobRetryCounters[operation.Id];
                            jobRetryCounters[operation.Id] = (entry.dequeueCount + 1, operation.DueTime);
                        }
                    }
                    break;
            }
        }

        // Rebuild the priority queue
        foreach (var op in addedJobs.Values)
        {
            var retryCounter = 0;
            var dueTime = op.DueTime!.Value;
            if (jobRetryCounters.TryGetValue(op.Id, out var retryEntries))
            {
                retryCounter = retryEntries.dequeueCount;
                dueTime = retryEntries.newDueTime ?? dueTime;
            }

            EnqueueJob(new ScheduledJob
            {
                Id = op.Id,
                Name = op.Name!,
                DueTime = dueTime,
                TargetGrainId = op.TargetGrainId!.Value,
                ShardId = Id,
                Metadata = op.Metadata,
            },
            retryCounter);
        }

        ETag = response.Value.Details.ETag;
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
        // TODO: AppendBlob has a limit of 50,000 blocks. Implement blob rotation when this limit is approached.
        var batchOperations = new List<StorageOperation>(_options.MaxBatchSize);
        
        try
        {
            while (await _storageOperationChannel.Reader.WaitToReadAsync(cancellationToken))
            {
                // Read first operation
                if (!_storageOperationChannel.Reader.TryRead(out var firstOperation))
                {
                    continue;
                }

                // Handle metadata operations immediately (cannot be batched)
                if (firstOperation.Type is StorageOperationType.UpdateMetadata)
                {
                    try
                    {
                        await UpdateMetadataAsync(firstOperation.Metadata!, cancellationToken);
                        firstOperation.CompletionSource.TrySetResult();
                    }
                    catch (Exception ex)
                    {
                        firstOperation.CompletionSource?.TrySetException(ex);
                    }
                    continue;
                }

                // Collect job operations for batching
                batchOperations.Add(firstOperation);

                // Try to collect more operations up to the maximum batch size
                if (TryCollectJobOperationsForBatch(batchOperations))
                {
                    // Not enough operations to meet the minimum batch size, wait for more or timeout
                    await Task.Delay(_options.BatchFlushInterval, cancellationToken);
                    TryCollectJobOperationsForBatch(batchOperations);
                }

                // Process the batch of job operations
                if (batchOperations.Count > 0)
                {
                    try
                    {
                        await AppendJobOperationBatchAsync(batchOperations, cancellationToken);

                        // Mark all operations as completed
                        foreach (var op in batchOperations)
                        {
                            op.CompletionSource.TrySetResult();
                        }
                    }
                    catch (Exception ex)
                    {
                        // Mark all operations as failed
                        foreach (var op in batchOperations)
                        {
                            op.CompletionSource?.TrySetException(ex);
                        }
                    }
                    finally
                    {
                        batchOperations.Clear();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Ignore
        }
        finally
        {
            // Expected during shutdown - cancel all pending operations
            while (_storageOperationChannel.Reader.TryRead(out var operation))
            {
                operation.CompletionSource?.TrySetCanceled(cancellationToken);
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
        using var stream = PooledBufferStream.Rent();
        try
        {
            stream.Position = 0; // TODO Remove that once PooledBufferStream fixed
            
            // Encode all job operations into a single stream
            foreach (var operation in operations)
            {
                NetstringJsonSerializer<JobOperation>.Encode(operation.JobOperation!.Value, stream, JobOperationJsonContext.Default.JobOperation);
            }
            var str = System.Text.Encoding.UTF8.GetString(stream.ToArray());
            stream.Position = 0;
            var result = await BlobClient.AppendBlockAsync(
                stream,
                new AppendBlobAppendBlockOptions { Conditions = new AppendBlobRequestConditions { IfMatch = ETag } },
                cancellationToken);
            ETag = result.Value.ETag;
            CommitedBlockCount = result.Value.BlobCommittedBlockCount;
        }
        finally
        {
            PooledBufferStream.Return(stream);
        }
    }

    private async Task UpdateMetadataAsync(IDictionary<string, string> metadata, CancellationToken cancellationToken)
    {
        var result = await BlobClient.SetMetadataAsync(
            metadata,
            new BlobRequestConditions { IfMatch = ETag },
            cancellationToken);
        ETag = result.Value.ETag;
        Metadata = metadata;
    }

    /// <summary>
    /// Stops the background storage processor and waits for all pending operations to complete.
    /// After calling this method, no new storage operations can be enqueued.
    /// This method is idempotent and can be called multiple times safely.
    /// </summary>
    internal async Task StopProcessorAsync(CancellationToken cancellationToken)
    {
        // Complete the channel to stop accepting new operations (idempotent operation)
        if (_storageOperationChannel.Writer.TryComplete())
        {
            _shutdownCts.Cancel();
        }

        // Wait for the background processor to finish all pending operations
        try
        {
            await _storageProcessorTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Expected during normal shutdown
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
    public TaskCompletionSource CompletionSource { get; init; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public static StorageOperation CreateAppendOperation(JobOperation jobOperation)
    {
        return new StorageOperation
        {
            Type = StorageOperationType.AppendJobOperation,
            JobOperation = jobOperation
        };
    }

    public static StorageOperation CreateMetadataOperation(IDictionary<string, string> metadata)
    {
        return new StorageOperation
        {
            Type = StorageOperationType.UpdateMetadata,
            Metadata = metadata
        };
    }
}
