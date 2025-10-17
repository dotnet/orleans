using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Orleans.Runtime;

namespace Orleans.ScheduledJobs.AzureStorage;

internal sealed class AzureStorageJobShard : JobShard
{
    internal AppendBlobClient BlobClient { get; init; }
    internal ETag? ETag { get; private set; }

    private InMemoryJobQueue _jobQueue;
    private int _jobCount = 0;

    public AzureStorageJobShard(string id, DateTimeOffset startTime, DateTimeOffset endTime, AppendBlobClient blobClient)
        : base(id, startTime, endTime)
    {
        BlobClient = blobClient;
        _jobQueue = new InMemoryJobQueue();
    }

    public override ValueTask<int> GetJobCount()
    {
        return ValueTask.FromResult(_jobCount);
    }

    public override IAsyncEnumerable<IScheduledJobContext> ConsumeScheduledJobsAsync()
    {
        return _jobQueue;
    }

    public override async Task RemoveJobAsync(string jobId)
    {
        var operation = JobOperation.CreateRemoveOperation(jobId);
        await AppendOperation(operation);
        _jobQueue.CancelJob(jobId);
        _jobCount--;
    }

    public override async Task<IScheduledJob> ScheduleJobAsync(GrainId target, string jobName, DateTimeOffset dueTime, IReadOnlyDictionary<string, string>? metadata = null)
    {
        if (IsComplete)
            throw new InvalidOperationException("Cannot schedule job on a complete shard.");

        var jobId = Guid.NewGuid().ToString();
        var operation = JobOperation.CreateAddOperation(jobId, jobName, dueTime, target, metadata);
        await AppendOperation(operation);
        _jobCount++;
        var job = new ScheduledJob
        {
            Id = jobId,
            Name = jobName,
            DueTime = dueTime,
            TargetGrainId = target,
            ShardId = Id,
            Metadata = metadata,
        };
        _jobQueue.Enqueue(job, 0);
        return job;
    }

    private async Task AppendOperation(JobOperation operation)
    {
        var content = BinaryData.FromObjectAsJson(operation).ToString() + Environment.NewLine;
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        var result = await BlobClient.AppendBlockAsync(
                    stream,
                    new AppendBlobAppendBlockOptions { Conditions = new AppendBlobRequestConditions { IfMatch = ETag } });
        ETag = result.Value.ETag;
    }

    public async ValueTask InitializeAsync()
    {
        if (ETag is not null) return; // already initialized

        // Load existing blob
        var response = await BlobClient.DownloadAsync();
        using var stream = response.Value.Content;
        using var reader = new StreamReader(stream);

        // Rebuild state by replaying operations
        var addedJobs = new Dictionary<string, JobOperation>();
        var deletedJobs = new HashSet<string>();
        var jobRetryCounters = new Dictionary<string, int>();
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) continue;
            var operation = JsonSerializer.Deserialize<JobOperation>(line);
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
                            jobRetryCounters[operation.Id] = 1;
                        }
                        else
                        {
                            jobRetryCounters[operation.Id]++;
                        }
                    }
                    break;
            }
        }
        // Rebuild the priority queue
        foreach (var op in addedJobs.Values)
        {
            jobRetryCounters.TryGetValue(op.Id, out var retryCounter);
            _jobQueue.Enqueue(new ScheduledJob
            {
                Id = op.Id,
                Name = op.Name!,
                DueTime = op.DueTime!.Value,
                TargetGrainId = op.TargetGrainId!.Value,
                ShardId = Id,
                Metadata = op.Metadata,
            },
            retryCounter);
        }
        _jobCount = addedJobs.Count;

        ETag = response.Value.Details.ETag;
    }

    public override Task MarkAsComplete()
    {
        IsComplete = true;
        _jobQueue.MarkAsFrozen();
        return Task.CompletedTask;
    }

    public override async Task RetryJobLaterAsync(IScheduledJobContext jobContext, DateTimeOffset newDueTime)
    {
        await AppendOperation(JobOperation.CreateRetryOperation(jobContext.Job.Id));
        _jobQueue.RetryJobLater(jobContext, newDueTime);
    }
}

internal struct JobOperation
{
    public enum OperationType
    {
        Add,
        Remove,
        Retry,
    }

    public OperationType Type { get; init; }

    public string Id { get; init; }
    public string? Name { get; init; }
    public DateTimeOffset? DueTime { get; init; }
    public GrainId? TargetGrainId { get; init; }
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    public static JobOperation CreateAddOperation(string id, string name, DateTimeOffset dueTime, GrainId targetGrainId, IReadOnlyDictionary<string,string>? metadata) =>
        new() { Type = OperationType.Add, Id = id, Name = name, DueTime = dueTime, TargetGrainId = targetGrainId, Metadata = metadata };

    public static JobOperation CreateRemoveOperation(string id) =>
        new() { Type = OperationType.Remove, Id = id };

    public static JobOperation CreateRetryOperation(string id) =>
        new() { Type = OperationType.Retry, Id = id };
}
