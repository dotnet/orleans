using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using StackExchange.Redis;

namespace Orleans.DurableJobs.Redis;

internal sealed partial class RedisJobShard : JobShard
{
    private readonly IDatabase _db;
    private readonly string _streamKey;   // e.g. "shard:{shardId}:stream"
    private readonly string _metaKey;     // e.g. "shard:{shardId}:meta"
    private readonly ILogger<RedisJobShard> _logger;

    // Keep the original simple ctor for tests/compat if needed.
    //public RedisJobShard(string id, DateTimeOffset startTime, DateTimeOffset endTime)
    //    : base(id, startTime, endTime)
    //{
    //}

    // Preferred ctor to use in manager: fornece DB, prefix e logger.
    public RedisJobShard(string id, DateTimeOffset startTime, DateTimeOffset endTime, IDatabase db, string keyPrefix, ILogger<RedisJobShard> logger)
        : base(id, startTime, endTime)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _streamKey = $"{keyPrefix}:{id}:stream";
        _metaKey = $"{keyPrefix}:{id}:meta";
    }

    // Persist add -> XADD stream
    protected override Task PersistAddJobAsync(string jobId, string jobName, DateTimeOffset dueTime, GrainId target, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken)
    {
        var op = JobOperation.CreateAddOperation(jobId, jobName, dueTime, target, metadata);
        var json = JsonSerializer.Serialize(op, JobOperationJsonContext.Default.JobOperation);
        _db.StreamAdd(_streamKey, [new NameValueEntry("op", json)]);
        _logger.LogDebug("Persisted Add op for job {JobId} to redis stream {StreamKey}", jobId, _streamKey);
        return Task.CompletedTask;
    }

    // Persist remove -> XADD stream
    protected override Task PersistRemoveJobAsync(string jobId, CancellationToken cancellationToken)
    {
        var op = JobOperation.CreateRemoveOperation(jobId);
        var json = JsonSerializer.Serialize(op, JobOperationJsonContext.Default.JobOperation);
        _db.StreamAdd(_streamKey, [new NameValueEntry("op", json)]);
        _logger.LogDebug("Persisted Remove op for job {JobId} to redis stream {StreamKey}", jobId, _streamKey);
        return Task.CompletedTask;
    }

    // Persist retry -> XADD stream
    protected override Task PersistRetryJobAsync(string jobId, DateTimeOffset newDueTime, CancellationToken cancellationToken)
    {
        var op = JobOperation.CreateRetryOperation(jobId, newDueTime);
        var json = JsonSerializer.Serialize(op, JobOperationJsonContext.Default.JobOperation);
        _db.StreamAdd(_streamKey, [new NameValueEntry("op", json)]);
        _logger.LogDebug("Persisted Retry op for job {JobId} to redis stream {StreamKey}", jobId, _streamKey);
        return Task.CompletedTask;
    }

    // Update metadata (ownership etc.) - simple HSET (replace). Consider using LUA for CAS semantics.
    public Task UpdateMetadataAsync(IDictionary<string, string> metadata, CancellationToken cancellationToken)
    {
        var entries = new HashEntry[metadata.Count];
        var i = 0;
        foreach (var kv in metadata)
        {
            entries[i++] = new HashEntry(kv.Key, kv.Value);
        }
        _db.HashSet(_metaKey, entries);
        _logger.LogDebug("Updated metadata for shard {ShardId} in key {MetaKey}", Id, _metaKey);
        return Task.CompletedTask;
    }

    // Initialize: replay the Redis stream and rebuild in-memory queue (same logic as Azure)
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Initializing shard '{ShardId}' from Redis stream '{StreamKey}'", Id, _streamKey);
        var sw = Stopwatch.StartNew();

        // Replay stream from beginning
        var addedJobs = new Dictionary<string, JobOperation>();
        var deletedJobs = new HashSet<string>();
        var jobRetryCounters = new Dictionary<string, (int dequeueCount, DateTimeOffset? newDueTime)>();

        // StreamRange with "-" .. "+" returns all entries. If stream does not exist, returns empty.
        var entries = _db.StreamRange(_streamKey, "-", "+", 0);
        foreach (var entry in entries)
        {
            // Expect field "op" with JSON
            foreach (var nv in entry.Values)
            {
                if (nv.Name == "op")
                {
                    var json = (string)nv.Value!;
                    var operation = JsonSerializer.Deserialize<JobOperation>(json, JobOperationJsonContext.Default.JobOperation);
                    //if (operation is null) continue;

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
            }
            if (cancellationToken.IsCancellationRequested) break;
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
        _logger.LogInformation("Shard '{ShardId}' initialized from Redis: loaded {JobCount} job(s) in {ElapsedMilliseconds}ms", Id, addedJobs.Count, sw.ElapsedMilliseconds);
        await Task.CompletedTask;
    }
}
