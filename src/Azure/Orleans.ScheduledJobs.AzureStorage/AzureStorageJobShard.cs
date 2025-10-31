using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Orleans.Runtime;

namespace Orleans.ScheduledJobs.AzureStorage;

internal sealed class AzureStorageJobShard : JobShard
{
    private readonly Channel<StorageOperation> _storageOperationChannel;
    private readonly Task _storageProcessorTask;
    private readonly CancellationTokenSource _shutdownCts = new();

    internal AppendBlobClient BlobClient { get; init; }
    internal ETag? ETag { get; private set; }

    public AzureStorageJobShard(string id, DateTimeOffset startTime, DateTimeOffset endTime, AppendBlobClient blobClient, IDictionary<string, string>? metadata, ETag? eTag)
        : base(id, startTime, endTime)
    {
        BlobClient = blobClient;
        ETag = eTag;
        Metadata = metadata;
        
        // Create unbounded channel for storage operations
        // In the future, we could add batching here
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
        
        await foreach (var netstringData in NetstringEncoder.DecodeAsync(stream))
        {
            var operation = JsonSerializer.Deserialize(netstringData, JobOperationJsonContext.Default.JobOperation);
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
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        operation.CompletionSource = tcs;
        
        await _storageOperationChannel.Writer.WriteAsync(operation, cancellationToken);
        await tcs.Task;
    }

    private async Task ProcessStorageOperationsAsync()
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.ForceYielding);

        var cancellationToken = _shutdownCts.Token;
        // TODO: AppendBlob has a limit of 50,000 blocks. Implement blob rotation when this limit is approached.
        try
        {
            await foreach (var operation in _storageOperationChannel.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    switch (operation.Type)
                    {
                        case StorageOperationType.AppendJobOperation:
                            await AppendJobOperationAsync(operation.JobOperation!.Value, cancellationToken);
                            break;
                        case StorageOperationType.UpdateMetadata:
                            await UpdateMetadataAsync(operation.Metadata!, cancellationToken);
                            break;
                    }
                    
                    operation.CompletionSource?.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    operation.CompletionSource?.TrySetException(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown - cancel all pending operations
            while (_storageOperationChannel.Reader.TryRead(out var operation))
            {
                operation.CompletionSource?.TrySetCanceled(cancellationToken);
            }
        }
    }

    private async Task AppendJobOperationAsync(JobOperation operation, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(operation, JobOperationJsonContext.Default.JobOperation);
        var content = NetstringEncoder.Encode(json);
        using var stream = new MemoryStream(content);
        var result = await BlobClient.AppendBlockAsync(
            stream,
            new AppendBlobAppendBlockOptions { Conditions = new AppendBlobRequestConditions { IfMatch = ETag } },
            cancellationToken);
        ETag = result.Value.ETag;
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
    internal async Task StopProcessorAsync()
    {
        // Complete the channel to stop accepting new operations (idempotent operation)
        if (_storageOperationChannel.Writer.TryComplete())
        {
            _shutdownCts.Cancel();
        }

        // Wait for the background processor to finish all pending operations
        try
        {
            await _storageProcessorTask;
        }
        catch (OperationCanceledException)
        {
            // Expected during normal shutdown
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await StopProcessorAsync();
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
    public TaskCompletionSource<bool>? CompletionSource { get; set; }

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
