using System;
using System.Linq;
using Orleans.Journaling;

namespace Orleans.DurableJobs;

internal sealed class JournaledJobShardState : IJournaledState, IDurableValueCommandHandler<DurableJobShardJournalRecord>
{
    public const string StateName = "jobs";

    private readonly JobShardId _shardId;
    private readonly IDurableValueCommandCodec<DurableJobShardJournalRecord>? _codec;
    private InMemoryJobQueue _jobQueue = new();
    private JournalStreamWriter _writer;

    public JournaledJobShardState(
        JobShardId shardId,
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        IDurableValueCommandCodec<DurableJobShardJournalRecord> codec)
        : this(shardId, startTime, endTime, codec, isAddingCompleted: false)
    {
        ArgumentNullException.ThrowIfNull(codec);
    }

    internal JournaledJobShardState(JobShardId shardId, DateTimeOffset startTime, DateTimeOffset endTime)
        : this(shardId, startTime, endTime, codec: null, isAddingCompleted: false)
    {
    }

    private JournaledJobShardState(
        JobShardId shardId,
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        IDurableValueCommandCodec<DurableJobShardJournalRecord>? codec,
        bool isAddingCompleted)
    {
        if (endTime < startTime)
        {
            throw new ArgumentOutOfRangeException(nameof(endTime), "Shard end time must be greater than or equal to the start time.");
        }

        _shardId = shardId;
        _codec = codec;
        StartTime = startTime;
        EndTime = endTime;
        IsAddingCompleted = isAddingCompleted;
    }

    public string Id => _shardId.Value;

    public DateTimeOffset StartTime { get; }

    public DateTimeOffset EndTime { get; }

    public bool IsAddingCompleted { get; private set; }

    public int Count => _jobQueue.Count;

    public IAsyncEnumerable<IJobRunContext> ConsumeDurableJobsAsync() => _jobQueue;

    public DurableJob? TryScheduleJob(ScheduleJobRequest request)
    {
        if (IsAddingCompleted)
        {
            return null;
        }

        if (request.DueTime < StartTime || request.DueTime > EndTime)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Scheduled time is out of shard bounds.");
        }

        var job = new DurableJob
        {
            Id = Guid.NewGuid().ToString(),
            TargetGrainId = request.Target,
            Name = request.JobName,
            DueTime = request.DueTime,
            ShardId = Id,
            Metadata = request.Metadata
        };

        Write(DurableJobShardJournalRecord.ForSchedule(job));
        ApplySchedule(job);
        return job;
    }

    public bool RemoveJob(string jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        Write(DurableJobShardJournalRecord.ForRemove(jobId));
        return ApplyRemove(jobId);
    }

    public bool RetryJobLater(IJobRunContext jobContext, DateTimeOffset newDueTime)
    {
        ArgumentNullException.ThrowIfNull(jobContext);
        return RetryJobLater(jobContext.Job.Id, newDueTime, jobContext.DequeueCount);
    }

    public bool RetryJobLater(string jobId, DateTimeOffset newDueTime, int dequeueCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ValidateDequeueCount(dequeueCount);

        Write(DurableJobShardJournalRecord.ForRetry(jobId, newDueTime, dequeueCount));
        return ApplyRetry(jobId, newDueTime, dequeueCount);
    }

    public void MarkAsComplete()
    {
        IsAddingCompleted = true;
        _jobQueue.MarkAsComplete();
    }

    internal DurableJobShardSnapshot CaptureSnapshot()
    {
        var jobs = _jobQueue.GetSnapshot()
            .OrderBy(static item => item.Job.DueTime)
            .ThenBy(static item => item.Job.Id, StringComparer.Ordinal)
            .Select(static item => new DurableJobShardSnapshotEntry
            {
                Job = item.Job,
                DequeueCount = item.DequeueCount
            })
            .ToList();

        return new() { Jobs = jobs };
    }

    internal void Apply(DurableJobShardJournalRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        switch (record.Kind)
        {
            case DurableJobShardJournalRecordKind.Schedule:
                ApplySchedule(GetRequired(record.Schedule, nameof(record.Schedule)).Job);
                break;
            case DurableJobShardJournalRecordKind.Remove:
                ApplyRemove(GetRequired(record.Remove, nameof(record.Remove)).JobId);
                break;
            case DurableJobShardJournalRecordKind.Retry:
                var retry = GetRequired(record.Retry, nameof(record.Retry));
                ApplyRetry(retry.JobId, retry.DueTime, retry.DequeueCount);
                break;
            case DurableJobShardJournalRecordKind.Snapshot:
                ApplySnapshot(GetRequired(record.Snapshot, nameof(record.Snapshot)));
                break;
            default:
                throw new NotSupportedException($"DurableJobs shard journal record kind '{record.Kind}' is not supported.");
        }
    }

    void IJournaledState.ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
        context.GetRequiredCommandCodec(entry.FormatKey, GetCodec()).Apply(entry.Reader, this);

    void IDurableValueCommandHandler<DurableJobShardJournalRecord>.ApplySet(DurableJobShardJournalRecord value) => Apply(value);

    void IJournaledState.Reset(JournalStreamWriter writer)
    {
        _jobQueue = new();
        IsAddingCompleted = false;
        _writer = writer;
    }

    void IJournaledState.AppendEntries(JournalStreamWriter writer)
    {
    }

    void IJournaledState.AppendSnapshot(JournalStreamWriter writer)
    {
        GetCodec().WriteSet(DurableJobShardJournalRecord.ForSnapshot(CaptureSnapshot()), writer);
    }

    IJournaledState IJournaledState.DeepCopy() => throw new NotSupportedException();

    private void Write(DurableJobShardJournalRecord record) => GetCodec().WriteSet(record, _writer);

    private void ApplySchedule(DurableJob job) => _jobQueue.Enqueue(job, dequeueCount: 0);

    private bool ApplyRemove(string jobId) => _jobQueue.CancelJob(jobId);

    private bool ApplyRetry(string jobId, DateTimeOffset dueTime, int dequeueCount)
    {
        ValidateDequeueCount(dequeueCount);
        return _jobQueue.RetryJobLater(jobId, dueTime, dequeueCount);
    }

    private void ApplySnapshot(DurableJobShardSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        _jobQueue.Clear();
        foreach (var entry in snapshot.Jobs)
        {
            ArgumentNullException.ThrowIfNull(entry.Job);
            ValidateDequeueCount(entry.DequeueCount);
            _jobQueue.Enqueue(entry.Job, entry.DequeueCount);
        }
    }

    private IDurableValueCommandCodec<DurableJobShardJournalRecord> GetCodec()
        => _codec ?? throw new InvalidOperationException("A DurableJobs shard journal operation codec is required before journal entries can be appended.");

    private static T GetRequired<T>(T? value, string propertyName) where T : class
        => value ?? throw new InvalidOperationException($"DurableJobs shard journal record is missing required '{propertyName}' payload.");

    private static void ValidateDequeueCount(int dequeueCount)
    {
        if (dequeueCount < 0)
        {
            throw new InvalidOperationException("DurableJobs shard journal dequeue count must not be negative.");
        }
    }
}

[GenerateSerializer]
[Alias("Orleans.DurableJobs.DurableJobShardJournalRecordKind")]
internal enum DurableJobShardJournalRecordKind : byte
{
    Schedule = 0,
    Remove = 1,
    Retry = 2,
    Snapshot = 3
}

[GenerateSerializer]
[Alias("Orleans.DurableJobs.DurableJobShardJournalRecord")]
internal sealed class DurableJobShardJournalRecord
{
    [Id(0)]
    public DurableJobShardJournalRecordKind Kind { get; init; }

    [Id(1)]
    public DurableJobShardScheduleOperation? Schedule { get; init; }

    [Id(2)]
    public DurableJobShardRemoveOperation? Remove { get; init; }

    [Id(3)]
    public DurableJobShardRetryOperation? Retry { get; init; }

    [Id(4)]
    public DurableJobShardSnapshot? Snapshot { get; init; }

    public static DurableJobShardJournalRecord ForSchedule(DurableJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        return new()
        {
            Kind = DurableJobShardJournalRecordKind.Schedule,
            Schedule = new() { Job = job }
        };
    }

    public static DurableJobShardJournalRecord ForRemove(string jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        return new()
        {
            Kind = DurableJobShardJournalRecordKind.Remove,
            Remove = new() { JobId = jobId }
        };
    }

    public static DurableJobShardJournalRecord ForRetry(string jobId, DateTimeOffset dueTime, int dequeueCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        return new()
        {
            Kind = DurableJobShardJournalRecordKind.Retry,
            Retry = new()
            {
                JobId = jobId,
                DueTime = dueTime,
                DequeueCount = dequeueCount
            }
        };
    }

    public static DurableJobShardJournalRecord ForSnapshot(DurableJobShardSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new()
        {
            Kind = DurableJobShardJournalRecordKind.Snapshot,
            Snapshot = snapshot
        };
    }
}

[GenerateSerializer]
[Alias("Orleans.DurableJobs.DurableJobShardScheduleOperation")]
internal sealed class DurableJobShardScheduleOperation
{
    [Id(0)]
    public DurableJob Job { get; init; } = null!;
}

[GenerateSerializer]
[Alias("Orleans.DurableJobs.DurableJobShardRemoveOperation")]
internal sealed class DurableJobShardRemoveOperation
{
    [Id(0)]
    public string JobId { get; init; } = string.Empty;
}

[GenerateSerializer]
[Alias("Orleans.DurableJobs.DurableJobShardRetryOperation")]
internal sealed class DurableJobShardRetryOperation
{
    [Id(0)]
    public string JobId { get; init; } = string.Empty;

    [Id(1)]
    public DateTimeOffset DueTime { get; init; }

    [Id(2)]
    public int DequeueCount { get; init; }
}

[GenerateSerializer]
[Alias("Orleans.DurableJobs.DurableJobShardSnapshot")]
internal sealed class DurableJobShardSnapshot
{
    [Id(0)]
    public List<DurableJobShardSnapshotEntry> Jobs { get; init; } = [];
}

[GenerateSerializer]
[Alias("Orleans.DurableJobs.DurableJobShardSnapshotEntry")]
internal sealed class DurableJobShardSnapshotEntry
{
    [Id(0)]
    public DurableJob Job { get; init; } = null!;

    [Id(1)]
    public int DequeueCount { get; init; }
}
