using System;
using System.Linq;
using Orleans.DurableJobs;
using Orleans.Runtime;
using Xunit;

namespace Tester.DurableJobs;

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
        var live = CreateJob(shardId, "live", "live", start.AddMinutes(2));

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
    public async Task ConsumeDurableJobsAsync_YieldsDueJobsInDueTimeOrderAndIncrementsDequeueCount()
    {
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
        await foreach (var jobContext in state.ConsumeDurableJobsAsync())
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
    public void JobShardId_MapsToJournalStorageIdentityWithoutExposingRawIds()
    {
        var shardId = new JobShardId("silo/with/slashes:job");

        var storageId = shardId.ToJournalId();

        Assert.True(JobShardId.StoragePrefix.IsPrefixOf(storageId));
        Assert.Equal(shardId, JobShardId.FromJournalId(storageId));
    }

    private static DurableJob CreateJob(JobShardId shardId, string id, string name, DateTimeOffset dueTime) => new()
    {
        Id = id,
        Name = name,
        DueTime = dueTime,
        TargetGrainId = GrainId.Create("type", id),
        ShardId = shardId.Value
    };
}
