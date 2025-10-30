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

    public AzureStorageJobShard(string id, DateTimeOffset startTime, DateTimeOffset endTime, AppendBlobClient blobClient, IDictionary<string, string>? metadata, ETag? eTag)
        : base(id, startTime, endTime)
    {
        BlobClient = blobClient;
        ETag = eTag;
        Metadata = metadata;
    }

    protected override async Task PersistAddJobAsync(string jobId, string jobName, DateTimeOffset dueTime, GrainId target, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken)
    {
        var operation = JobOperation.CreateAddOperation(jobId, jobName, dueTime, target, metadata);
        await AppendOperation(operation, cancellationToken);
    }

    protected override async Task PersistRemoveJobAsync(string jobId, CancellationToken cancellationToken)
    {
        var operation = JobOperation.CreateRemoveOperation(jobId);
        await AppendOperation(operation, cancellationToken);
    }

    protected override async Task PersistRetryJobAsync(string jobId, DateTimeOffset newDueTime, CancellationToken cancellationToken)
    {
        var operation = JobOperation.CreateRetryOperation(jobId, newDueTime);
        await AppendOperation(operation, cancellationToken);
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

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        // Load existing blob
        var response = await BlobClient.DownloadAsync(cancellationToken: cancellationToken);
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

    private async Task AppendOperation(JobOperation operation, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(operation, JobOperationJsonContext.Default.JobOperation);
        var content = EncodeNetstring(json);
        using var stream = new MemoryStream(content);
        var result = await BlobClient.AppendBlockAsync(
                    stream,
                    new AppendBlobAppendBlockOptions { Conditions = new AppendBlobRequestConditions { IfMatch = ETag } },
                    cancellationToken);
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
}
