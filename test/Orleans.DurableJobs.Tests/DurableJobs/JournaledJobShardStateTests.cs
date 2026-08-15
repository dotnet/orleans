using System;
using System.Linq;
using System.Text.Json;
using Orleans.DurableJobs;
using Orleans.Runtime;
using Xunit;

namespace Tester.DurableJobs;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableJobs")]
[TestCategory("BVT"), TestCategory("DurableJobs")]
public class JournaledJobShardStateTests
{
    [Fact]
    public void Replay_FoldsScheduleRetryAndRemoveOperations()
    {
        var shardId = new JobShardId("shard-a");
        var start = DateTimeOffset.UtcNow;
        var state = new JournaledJobShardState(shardId, start, start.AddHours(1));
        var job = CreateJob(shardId, "job-1", "job", start.AddMinutes(1));
        var retryDueTime = start.AddHours(2);

        state.Apply(DurableJobShardJournalRecord.ForSchedule(job));
        state.Apply(DurableJobShardJournalRecord.ForRetry(job.Id, retryDueTime, dequeueCount: 1));

        var retrySnapshot = state.CaptureSnapshot();
        var retried = Assert.Single(retrySnapshot.Jobs);
        Assert.Equal(job.Id, retried.Job.Id);
        Assert.Equal(retryDueTime, retried.Job.DueTime);
        Assert.Equal(shardId.Value, retried.Job.ShardId);
        Assert.Equal(1, retried.DequeueCount);

        state.Apply(DurableJobShardJournalRecord.ForRemove(job.Id));
        state.Apply(DurableJobShardJournalRecord.ForRemove(job.Id));

        Assert.Equal(0, state.Count);
        Assert.Empty(state.CaptureSnapshot().Jobs);
    }

    [Fact]
    public void Snapshot_ReplacesLiveJobsAndOmitsRemovedHistory()
    {
        var shardId = new JobShardId("shard-b");
        var start = DateTimeOffset.UtcNow;
        var source = new JournaledJobShardState(shardId, start, start.AddHours(1));
        var removed = CreateJob(shardId, "removed", "removed", start.AddMinutes(1));
        var live = CreateJob(shardId, "live", "live", start.AddMinutes(2), priority: DurableJobPriority.Low);

        source.Apply(DurableJobShardJournalRecord.ForSchedule(removed));
        source.Apply(DurableJobShardJournalRecord.ForSchedule(live));
        source.Apply(DurableJobShardJournalRecord.ForRetry(live.Id, start.AddMinutes(3), dequeueCount: 2));
        source.Apply(DurableJobShardJournalRecord.ForRemove(removed.Id));

        var snapshot = source.CaptureSnapshot();
        Assert.DoesNotContain(typeof(DurableJobShardSnapshot).GetProperties(), property => property.Name == nameof(IJobRunContext.RunId));
        Assert.DoesNotContain(typeof(DurableJobShardSnapshotEntry).GetProperties(), property => property.Name == nameof(IJobRunContext.RunId));

        var target = new JournaledJobShardState(shardId, start, start.AddHours(1));
        target.Apply(DurableJobShardJournalRecord.ForSnapshot(snapshot));

        var entry = Assert.Single(target.CaptureSnapshot().Jobs);
        Assert.Equal(live.Id, entry.Job.Id);
        Assert.Equal(start.AddMinutes(3), entry.Job.DueTime);
        Assert.Equal(DurableJobPriority.Low, entry.Job.Priority);
        Assert.Equal(2, entry.DequeueCount);
        Assert.DoesNotContain(target.CaptureSnapshot().Jobs, item => item.Job.Id == removed.Id);
    }

    [Fact]
    public void Retry_KeepsJobInSameShardWhenDueTimeMovesOutsideOriginalWindow()
    {
        var shardId = new JobShardId("shard-c");
        var start = DateTimeOffset.UtcNow;
        var end = start.AddMinutes(10);
        var state = new JournaledJobShardState(shardId, start, end);
        var job = CreateJob(shardId, "job-1", "job", start.AddMinutes(1));
        var retryDueTime = end.AddDays(1);

        state.Apply(DurableJobShardJournalRecord.ForSchedule(job));
        state.Apply(DurableJobShardJournalRecord.ForRetry(job.Id, retryDueTime, dequeueCount: 1));

        var entry = Assert.Single(state.CaptureSnapshot().Jobs);
        Assert.Equal(shardId.Value, entry.Job.ShardId);
        Assert.Equal(retryDueTime, entry.Job.DueTime);
    }

    [Fact]
    public void Replay_RestoresSuccessfulRescheduleGeneration()
    {
        var shardId = new JobShardId("shard-generation");
        var start = DateTimeOffset.UtcNow;
        var state = new JournaledJobShardState(shardId, start, start.AddHours(1));
        var job = CreateJob(shardId, "job-1", "job", start.AddMinutes(1));

        state.Apply(DurableJobShardJournalRecord.ForSchedule(job));
        state.Apply(DurableJobShardJournalRecord.ForRetry(
            job.Id,
            start.AddMinutes(2),
            dequeueCount: 0,
            executionGeneration: 1));

        var entry = Assert.Single(state.CaptureSnapshot().Jobs);
        Assert.Equal(1, entry.Job.ExecutionGeneration);
        Assert.Equal(0, entry.DequeueCount);
    }

    [Fact]
    public void JsonSnapshot_RoundTripsSuccessfulRescheduleGeneration()
    {
        var shardId = new JobShardId("shard-json-generation");
        var start = DateTimeOffset.UtcNow;
        var state = new JournaledJobShardState(shardId, start, start.AddHours(1));
        var job = CreateJob(shardId, "job-1", "job", start.AddMinutes(1));

        state.Apply(DurableJobShardJournalRecord.ForSchedule(job));
        state.Apply(DurableJobShardJournalRecord.ForRetry(
            job.Id,
            start.AddMinutes(2),
            dequeueCount: 0,
            executionGeneration: 1));

        var json = JsonSerializer.Serialize(
            state.CaptureSnapshot(),
            DurableJobsJsonContext.Default.DurableJobShardSnapshot);
        var snapshot = JsonSerializer.Deserialize(
            json,
            DurableJobsJsonContext.Default.DurableJobShardSnapshot);

        Assert.NotNull(snapshot);
        var entry = Assert.Single(snapshot.Jobs);
        Assert.Equal(1, entry.Job.ExecutionGeneration);
        Assert.Equal(0, entry.DequeueCount);
    }

    [Fact]
    public void TryScheduleJob_WhenShardReachedConfiguredCapacity_ReturnsNullWithoutWriting()
    {
        var shardId = new JobShardId("shard-capacity");
        var start = DateTimeOffset.UtcNow;
        var state = new JournaledJobShardState(shardId, start, start.AddHours(1), maxJobCount: 2);
        state.Apply(DurableJobShardJournalRecord.ForSchedule(CreateJob(shardId, "first", "first", start.AddMinutes(1))));
        state.Apply(DurableJobShardJournalRecord.ForSchedule(CreateJob(shardId, "second", "second", start.AddMinutes(1))));

        var result = state.TryScheduleJob(new ScheduleJobRequest
        {
            Target = GrainId.Create("type", "third"),
            JobName = "third",
            DueTime = start.AddMinutes(1),
        });

        Assert.Null(result);
        Assert.Equal(2, state.Count);
    }

    [Fact]
    public async Task ConsumeDurableJobsAsync_YieldsDueJobsInDueTimeOrderAndIncrementsDequeueCount()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var shardId = new JobShardId("shard-d");
        var start = DateTimeOffset.UtcNow.AddMinutes(-1);
        var state = new JournaledJobShardState(shardId, start, DateTimeOffset.UtcNow.AddMinutes(1));
        var third = CreateJob(shardId, "third", "third", DateTimeOffset.UtcNow.AddSeconds(-3));
        var first = CreateJob(shardId, "first", "first", DateTimeOffset.UtcNow.AddSeconds(-9));
        var second = CreateJob(shardId, "second", "second", DateTimeOffset.UtcNow.AddSeconds(-6));

        state.Apply(DurableJobShardJournalRecord.ForSchedule(third));
        state.Apply(DurableJobShardJournalRecord.ForSchedule(first));
        state.Apply(DurableJobShardJournalRecord.ForSchedule(second));

        var consumed = new List<IJobRunContext>();
        await foreach (var jobContext in state.ConsumeDurableJobsAsync().WithCancellation(cancellationToken))
        {
            consumed.Add(jobContext);
            if (consumed.Count == 3)
            {
                break;
            }
        }

        Assert.Equal(["first", "second", "third"], consumed.Select(context => context.Job.Id).ToArray());
        Assert.All(consumed, context => Assert.Equal(1, context.DequeueCount));
    }

    [Fact]
    public async Task Snapshot_RoundTripsThirtyThousandBackloggedJobsWithoutLossOrDuplicates()
    {
        const int bucketCount = 6;
        const int jobsPerBucket = 5_000;
        const int jobCount = bucketCount * jobsPerBucket;
        var shardId = new JobShardId("shard-scale");
        var now = DateTimeOffset.UtcNow;
        var source = new JournaledJobShardState(shardId, now.AddMinutes(-10), now.AddMinutes(1));

        for (var bucketIndex = bucketCount - 1; bucketIndex >= 0; bucketIndex--)
        {
            var dueTime = now.AddMinutes(bucketIndex - bucketCount);
            for (var jobIndex = 0; jobIndex < jobsPerBucket; jobIndex++)
            {
                var priority = (DurableJobPriority)((jobIndex % 3) - 1);
                var id = $"bucket-{bucketIndex:D2}-job-{jobIndex:D4}";
                var job = CreateJob(shardId, id, id, dueTime, priority);
                source.Apply(DurableJobShardJournalRecord.ForSchedule(job));
            }
        }

        var snapshot = source.CaptureSnapshot();
        Assert.Equal(jobCount, snapshot.Jobs.Count);

        var restored = new JournaledJobShardState(shardId, source.StartTime, source.EndTime);
        restored.Apply(DurableJobShardJournalRecord.ForSnapshot(snapshot));
        restored.MarkAsComplete();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        DateTimeOffset? previousDueTime = null;
        var previousPriority = DurableJobPriority.High;
        await foreach (var context in restored.ConsumeDurableJobsAsync())
        {
            Assert.True(seen.Add(context.Job.Id), $"Job '{context.Job.Id}' was restored more than once.");

            if (previousDueTime == context.Job.DueTime)
            {
                Assert.True(context.Job.Priority <= previousPriority);
            }
            else
            {
                Assert.True(previousDueTime is null || context.Job.DueTime > previousDueTime);
            }

            previousDueTime = context.Job.DueTime;
            previousPriority = context.Job.Priority;
            restored.Apply(DurableJobShardJournalRecord.ForRemove(context.Job.Id));
        }

        Assert.Equal(jobCount, seen.Count);
        Assert.Equal(0, restored.Count);
    }

    [Fact]
    public void JobShardId_MapsToJournalStorageIdentityWithoutExposingRawIds()
    {
        var shardId = new JobShardId("silo/with/slashes:job");

        var storageId = shardId.ToJournalId();

        Assert.True(JobShardId.StoragePrefix.IsPrefixOf(storageId));
        Assert.Equal(shardId, JobShardId.FromJournalId(storageId));
    }

    [Fact]
    public void Apply_DefaultKind_Throws()
    {
        var shardId = new JobShardId("shard-default-kind");
        var start = DateTimeOffset.UtcNow;
        var state = new JournaledJobShardState(shardId, start, start.AddHours(1));

        // A default-constructed record has Kind == DurableJobShardJournalRecordKind.None.
        // This simulates an uninitialized record or a JSON payload where 'kind' was omitted
        // because the serializer is configured to skip default-valued properties.
        var record = new DurableJobShardJournalRecord();

        var exception = Assert.Throws<InvalidOperationException>(() => state.Apply(record));
        Assert.Contains("uninitialized Kind", exception.Message, StringComparison.Ordinal);
    }

    private static DurableJob CreateJob(
        JobShardId shardId,
        string id,
        string name,
        DateTimeOffset dueTime,
        DurableJobPriority priority = DurableJobPriority.Normal) => new()
    {
        Id = id,
        Name = name,
        DueTime = dueTime,
        TargetGrainId = GrainId.Create("type", id),
        ShardId = shardId.Value,
        Priority = priority
    };
}
