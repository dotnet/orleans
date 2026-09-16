using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableJobs;
using Orleans.DurableMessaging.Tests.Support;
using Orleans.Journaling;
using Orleans.Runtime;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Functional;

[Collection(DurableMessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class InboxCallbackOwnershipTests : DurableMessagingBehaviorTestBase
{
    [Fact]
    public async Task MissingOwnerWithWork_RepairsScheduleAndCommitBeforeOrphanRetires()
    {
        var receiver = NewGrain();
        const string route = "messages/repair-owner";
        using var envelope = CreateEnvelope(receiver, NewMessage(90, "repaired"), route);
        await receiver.SeedInboxStateAsync(envelope.Value, null, null);
        var extension = GetExtension(receiver);
        var orphan = CreateJob(receiver, "orphan", "old-shard", "old:1");
        using var schedule = Fixture.JobManagerProbe.BlockNext(ReceiverTestServices.InboxJobName);
        var write = Fixture.Storage.BlockWrite(JournalId.FromGrainId(receiver.GetGrainId()));
        using var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), route);

        await Fixture.RevertStateAsync(receiver);
        await schedule.WaitUntilEnteredAsync();
        Assert.Equal(DurableJobRunStatus.InProgress, (await ExecuteAsync(extension, orphan)).Status);
        Assert.Empty(Fixture.GetSnapshot(receiver).Effects);
        Assert.Empty(Fixture.JobManagerProbe.GetScheduledJobs(ReceiverTestServices.InboxJobName, receiver.GetGrainId()));

        schedule.Continue();
        await write.WaitUntilEnteredAsync();
        var scheduled = Assert.Single(Fixture.JobManagerProbe.GetScheduledJobs(ReceiverTestServices.InboxJobName, receiver.GetGrainId()));
        Assert.Equal(DurableJobRunStatus.InProgress, (await ExecuteAsync(extension, orphan)).Status);
        Assert.Equal(DurableJobRunStatus.InProgress, (await ExecuteAsync(extension, scheduled)).Status);
        Assert.Empty(Fixture.GetSnapshot(receiver).Effects);

        write.Release();
        await handler.WaitUntilEnteredAsync();
        Assert.Equal(DurableJobRunStatus.Completed, (await ExecuteAsync(extension, orphan)).Status);
        var owned = Fixture.GetSnapshot(receiver);
        Assert.Same(scheduled, owned.InboxJob);
        Assert.Equal(scheduled.Metadata!["orleans.messaging.ownership-id"], owned.InboxJobId);
        Assert.Equal(1, Fixture.JobManagerProbe.GetAttemptCount(ReceiverTestServices.InboxJobName, receiver.GetGrainId()));
        handler.Release();

        var completed = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal(1, Assert.Single(completed.Effects).Count);
        Assert.Equal(0, completed.InboxCount);
        Assert.Equal(1, completed.ProcessedMessageCount);
    }

    [Theory]
    [InlineData("generation-only")]
    [InlineData("handle-only")]
    [InlineData("mismatched-metadata")]
    public async Task RecoveredInvalidPair_FailsLifecycleAndCallbackWithoutRepair(string fault)
    {
        var receiver = NewGrain();
        using var envelope = CreateEnvelope(receiver, NewMessage(91, fault));
        var handle = CreateJob(receiver, "physical", "shard", fault == "mismatched-metadata" ? "other:1" : "owner:1");
        await receiver.SeedInboxStateAsync(
            envelope.Value,
            fault == "handle-only" ? null : "owner:1",
            fault == "generation-only" ? null : handle);
        await Fixture.RevertStateAsync(receiver);
        var extension = GetExtension(receiver);
        var journalId = JournalId.FromGrainId(receiver.GetGrainId());
        var writes = Fixture.Storage.GetSuccessfulWriteCount(journalId);

        var lifecycle = await Assert.ThrowsAsync<InvalidOperationException>(() => ((ILifecycleObserver)extension).OnStart(TestContext.Current.CancellationToken));
        var callback = await Assert.ThrowsAsync<InvalidOperationException>(async () => await ExecuteAsync(extension, handle));

        Assert.Equal(lifecycle.Message, callback.Message);
        Assert.Contains(fault == "mismatched-metadata" ? "metadata does not match" : "both be present or both be absent", lifecycle.Message, StringComparison.Ordinal);
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(journalId));
        Assert.Equal(0, Fixture.JobManagerProbe.GetAttemptCount(ReceiverTestServices.InboxJobName, receiver.GetGrainId()));
        Assert.Equal(1, Fixture.GetSnapshot(receiver).InboxCount);
        Assert.Empty(Fixture.GetSnapshot(receiver).Effects);
    }

    [Theory]
    [InlineData("different-id")]
    [InlineData("different-shard")]
    [InlineData("missing-metadata")]
    public async Task NonAuthoritativeCallback_RetiresWithoutChangingCommittedOwner(string fault)
    {
        var receiver = NewGrain();
        const string route = "messages/physical-owner";
        using var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), route);
        using var envelope = CreateEnvelope(receiver, NewMessage(92, fault), route);
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        await handler.WaitUntilEnteredAsync();
        var owned = Fixture.GetSnapshot(receiver);
        var handle = Assert.IsType<DurableJob>(owned.InboxJob);
        var callback = CreateJob(
            receiver,
            fault == "different-id" ? "other-id" : handle.Id,
            fault == "different-shard" ? "other-shard" : handle.ShardId,
            fault == "missing-metadata" ? null : owned.InboxJobId);

        var result = await ExecuteAsync(GetExtension(receiver), callback);

        Assert.Equal(DurableJobRunStatus.Completed, result.Status);
        Assert.Same(handle, Fixture.GetSnapshot(receiver).InboxJob);
        Assert.Equal(owned.InboxJobId, Fixture.GetSnapshot(receiver).InboxJobId);
        Assert.Empty(Fixture.GetSnapshot(receiver).Effects);
        handler.Release();
        var completed = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal(1, Assert.Single(completed.Effects).Count);
        Assert.Equal(1, completed.MaxConcurrentHandlers);
    }

    [Fact]
    public async Task CallbacksDuringRecovery_WaitUntilCommittedOwnerIsRestored()
    {
        var receiver = NewGrain();
        const string route = "messages/callback-recovery";
        using var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), route);
        using var envelope = CreateEnvelope(receiver, NewMessage(93, "recovery"), route);
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        await handler.WaitUntilEnteredAsync();
        var owned = Fixture.GetSnapshot(receiver);
        var handle = Assert.IsType<DurableJob>(owned.InboxJob);
        var extension = GetExtension(receiver);
        var orphan = CreateJob(receiver, "orphan", "orphan-shard", "stale:1");
        var read = Fixture.Storage.BlockRead(JournalId.FromGrainId(receiver.GetGrainId()));
        var recovery = Fixture.RevertStateAsync(receiver).AsTask();
        await read.WaitUntilEnteredAsync();

        Assert.Equal(DurableJobRunStatus.InProgress, (await ExecuteAsync(extension, handle)).Status);
        Assert.Equal(DurableJobRunStatus.InProgress, (await ExecuteAsync(extension, orphan)).Status);
        read.Release();
        await recovery;

        Assert.Equal(DurableJobRunStatus.Completed, (await ExecuteAsync(extension, orphan)).Status);
        var recovered = Fixture.GetSnapshot(receiver);
        Assert.Equal(handle.Id, recovered.InboxJob?.Id);
        Assert.Equal(handle.ShardId, recovered.InboxJob?.ShardId);
        Assert.Equal(owned.InboxJobId, recovered.InboxJobId);
        handler.Release();
        var completed = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal(1, Assert.Single(completed.Effects).Count);
        Assert.Equal(1, Fixture.JobManagerProbe.GetAttemptCount(ReceiverTestServices.InboxJobName, receiver.GetGrainId()));
    }

    [Fact]
    public async Task DuplicateScheduledJobs_RetireExtraHandleAfterAcceptanceCommits()
    {
        var receiver = NewGrain();
        const string route = "messages/duplicate-jobs";
        _ = await receiver.GetSnapshotAsync();
        using var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), route);
        using var envelope = CreateEnvelope(receiver, NewMessage(94, "duplicate-jobs"), route);
        Fixture.JobManagerProbe.DuplicateNext(ReceiverTestServices.InboxJobName);
        var write = Fixture.Storage.BlockWrite(JournalId.FromGrainId(receiver.GetGrainId()));
        var delivery = DeliverAsync(receiver, envelope.Value);
        await write.WaitUntilEnteredAsync();
        var jobs = Fixture.JobManagerProbe.GetScheduledJobs(ReceiverTestServices.InboxJobName, receiver.GetGrainId());
        Assert.Equal(2, jobs.Count);
        var extension = GetExtension(receiver);
        Assert.Equal(DurableJobRunStatus.InProgress, (await ExecuteAsync(extension, jobs[0])).Status);
        Assert.Equal(DurableJobRunStatus.InProgress, (await ExecuteAsync(extension, jobs[1])).Status);
        Assert.False(delivery.IsCompleted);
        write.Release();
        Assert.Equal(DeliveryStatus.Accepted, (await delivery).Status);
        await handler.WaitUntilEnteredAsync();

        Assert.Equal(DurableJobRunStatus.Completed, (await ExecuteAsync(extension, jobs[1])).Status);
        Assert.Same(jobs[0], Fixture.GetSnapshot(receiver).InboxJob);
        handler.Release();
        var completed = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal(1, Assert.Single(completed.Effects).Count);
        Assert.Equal(1, completed.MaxConcurrentHandlers);
    }

    private IDurableJobFeatureHandler GetExtension(IDurableMessagingTestGrain receiver) =>
        (IDurableJobFeatureHandler)Fixture.GetGrainContext(receiver).ActivationServices.GetRequiredService(
            ReceiverTestServices.GetImplementationType("DurableInboxExtension"));

    private static ValueTask<DurableJobRunResult> ExecuteAsync(IDurableJobFeatureHandler extension, DurableJob job) =>
        extension.ExecuteJobAsync(new CallbackContext(job), TestContext.Current.CancellationToken);

    private static DurableJob CreateJob(IDurableMessagingTestGrain receiver, string id, string shardId, string? ownershipId) =>
        new()
        {
            Id = id,
            ShardId = shardId,
            Name = ReceiverTestServices.InboxJobName,
            TargetGrainId = receiver.GetGrainId(),
            DueTime = DateTimeOffset.UtcNow,
            Metadata = ownershipId is null ? null : new Dictionary<string, string>
            {
                ["orleans.messaging.ownership-id"] = ownershipId
            }
        };

    private sealed class CallbackContext(DurableJob job) : IJobRunContext
    {
        public DurableJob Job { get; } = job;
        public string RunId { get; } = Guid.NewGuid().ToString("N");
        public int DequeueCount => 1;
    }
}
