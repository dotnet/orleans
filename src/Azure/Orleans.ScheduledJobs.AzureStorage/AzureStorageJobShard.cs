using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
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

    public AzureStorageJobShard(string id, DateTimeOffset startTime, DateTimeOffset endTime, AppendBlobClient blobClient, IDictionary<string, string>? metadata, ETag? eTag)
        : base(id, startTime, endTime)
    {
        BlobClient = blobClient;
        _jobQueue = new InMemoryJobQueue();
        ETag = eTag;
        Metadata = metadata;
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

    public override async Task<IScheduledJob> ScheduleJobAsync(GrainId target, string jobName, DateTimeOffset dueTime, IReadOnlyDictionary<string, string>? metadata)
    {
        if (IsComplete)
        {
            throw new InvalidOperationException("Cannot schedule job on a complete shard.");
        }

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

    public async Task UpdateBlobMetadata(IDictionary<string, string> metadata, CancellationToken cancellationToken)
    {
        var result = await BlobClient.SetMetadataAsync(
            metadata,
            new BlobRequestConditions { IfMatch = ETag },
            cancellationToken);
        ETag = result.Value.ETag;
        Metadata = metadata;
    }

    private async Task AppendOperation(JobOperation operation)
    {
        var json = JsonSerializer.Serialize(operation, JobOperationJsonContext.Default.JobOperation);
        var content = EncodeNetstring(json);
        using var stream = new MemoryStream(content);
        var result = await BlobClient.AppendBlockAsync(
                    stream,
                    new AppendBlobAppendBlockOptions { Conditions = new AppendBlobRequestConditions { IfMatch = ETag } });
        ETag = result.Value.ETag;
    }

    private static byte[] EncodeNetstring(string data)
    {
        var dataBytes = System.Text.Encoding.UTF8.GetBytes(data);
        var lengthPrefix = System.Text.Encoding.ASCII.GetBytes($"{dataBytes.Length}:");
        var result = new byte[lengthPrefix.Length + dataBytes.Length + 1];
        
        lengthPrefix.CopyTo(result, 0);
        dataBytes.CopyTo(result, lengthPrefix.Length);
        result[^1] = (byte)',';
        
        return result;
    }

    private static async IAsyncEnumerable<string> ReadNetstringsAsync(Stream stream)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        
        while (true)
        {
            // Read length
            var lengthStr = "";
            while (true)
            {
                var ch = reader.Read();
                if (ch == -1)
                {
                    yield break;
                }
                
                if (ch == ':')
                {
                    break;
                }
                
                lengthStr += (char)ch;
            }

            if (string.IsNullOrWhiteSpace(lengthStr))
            {
                yield break;
            }

            if (!int.TryParse(lengthStr, out var length))
            {
                throw new InvalidDataException($"Invalid netstring length: {lengthStr}");
            }

            // Read data
            var buffer = new char[length];
            var totalRead = 0;
            while (totalRead < length)
            {
                var read = await reader.ReadAsync(buffer, totalRead, length - totalRead);
                if (read == 0)
                {
                    throw new InvalidDataException("Unexpected end of stream while reading netstring data");
                }
                
                totalRead += read;
            }

            // Read trailing comma
            var comma = reader.Read();
            if (comma != ',')
            {
                throw new InvalidDataException($"Expected ',' at end of netstring, got '{(char)comma}'");
            }

            yield return new string(buffer);
        }
    }

    public async ValueTask InitializeAsync()
    {
        // Load existing blob
        var response = await BlobClient.DownloadAsync();
        using var stream = response.Value.Content;

        // Rebuild state by replaying operations
        var addedJobs = new Dictionary<string, JobOperation>();
        var deletedJobs = new HashSet<string>();
        var jobRetryCounters = new Dictionary<string, (int dequeueCount, DateTimeOffset? newDueTime)>();
        
        await foreach (var netstringData in ReadNetstringsAsync(stream))
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
                            entry.dequeueCount++;
                            entry.newDueTime = operation.DueTime; 
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
        _jobQueue.MarkAsComplete();
        return Task.CompletedTask;
    }

    public override async Task RetryJobLaterAsync(IScheduledJobContext jobContext, DateTimeOffset newDueTime)
    {
        await AppendOperation(JobOperation.CreateRetryOperation(jobContext.Job.Id, newDueTime));
        _jobQueue.RetryJobLater(jobContext, newDueTime);
    }
}
