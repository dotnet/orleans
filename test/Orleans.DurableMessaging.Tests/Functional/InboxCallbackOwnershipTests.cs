using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableJobs;
using Orleans.DurableMessaging.Tests.Support;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Runtime.Diagnostics;
using Orleans.TestingHost.Diagnostics;
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
        var oldContext = Fixture.GetGrainContext(receiver);
        await receiver.RequestDeactivationAsync();
        await oldContext.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var orphan = CreateJob(receiver, "orphan", "old-shard", "old:1");
        using var schedule = Fixture.JobManagerProbe.BlockNext(ReceiverTestServices.InboxJobName);
        var write = Fixture.Storage.BlockWrite(JournalId.FromGrainId(receiver.GetGrainId()));
        using var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), route);

        var activation = receiver.GetSnapshotAsync();
        await schedule.WaitUntilEnteredAsync();
        var extension = GetExtension(receiver);
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
        await activation;
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
    public async Task RecoveredInvalidPair_FailsStartupAndStopsCallbacksWithoutRepair(string fault)
    {
        var receiver = NewGrain();
        using var envelope = CreateEnvelope(receiver, NewMessage(91, fault));
        var handle = CreateJob(receiver, "physical", "shard", fault == "mismatched-metadata" ? "other:1" : "owner:1");
        await receiver.SeedInboxStateAsync(
            envelope.Value,
            fault == "handle-only" ? null : "owner:1",
            fault == "generation-only" ? null : handle);
        var journalId = JournalId.FromGrainId(receiver.GetGrainId());
        var writes = Fixture.Storage.GetSuccessfulWriteCount(journalId);
        var previous = Fixture.GetGrainContext(receiver);
        await receiver.RequestDeactivationAsync();
        await previous.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        using var read = Fixture.Storage.BlockRead(journalId);
        var activation = receiver.GetSnapshotAsync();
        await read.WaitUntilEnteredAsync();
        var extension = GetExtension(receiver);
        var current = Fixture.GetGrainContext(receiver);
        read.Release();
        var lifecycle = await Assert.ThrowsAnyAsync<Exception>(() => activation);
        await current.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await ExecuteAsync(extension, handle));
        var delivery = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ((IDurableInboxExtension)extension).DeliverAsync(envelope.Value, TestContext.Current.CancellationToken));
        Assert.Contains(delivery.Message, lifecycle.ToString(), StringComparison.Ordinal);
        Assert.Contains(fault == "mismatched-metadata" ? "metadata does not match" : "both be present or both be absent", delivery.Message, StringComparison.Ordinal);
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(journalId));
        Assert.Equal(0, Fixture.JobManagerProbe.GetAttemptCount(ReceiverTestServices.InboxJobName, receiver.GetGrainId()));
        var replayFailure = await Assert.ThrowsAnyAsync<Exception>(() => receiver.GetSnapshotAsync());
        Assert.Contains(delivery.Message, replayFailure.ToString(), StringComparison.Ordinal);
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(journalId));
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
    public async Task CallbacksDuringFreshInitialization_WaitUntilCommittedOwnerIsRestored()
    {
        var receiver = NewGrain();
        const string route = "messages/callback-initialization";
        using var envelope = CreateEnvelope(receiver, NewMessage(93, "initialization"), route);
        var handle = CreateJob(receiver, "committed-physical", "committed-shard", "owner:1");
        await receiver.SeedInboxStateAsync(envelope.Value, "owner:1", handle);
        var oldContext = Fixture.GetGrainContext(receiver);
        await receiver.RequestDeactivationAsync();
        await oldContext.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var read = Fixture.Storage.BlockRead(JournalId.FromGrainId(receiver.GetGrainId()));
        using var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), route);
        var activation = receiver.GetSnapshotAsync();
        await read.WaitUntilEnteredAsync();
        var extension = GetExtension(receiver);
        var orphan = CreateJob(receiver, "orphan", "orphan-shard", "stale:1");
        Assert.Equal(DurableJobRunStatus.InProgress, (await ExecuteAsync(extension, handle)).Status);
        Assert.Equal(DurableJobRunStatus.InProgress, (await ExecuteAsync(extension, orphan)).Status);
        read.Release();
        await activation;
        await handler.WaitUntilEnteredAsync();
        Assert.Equal(DurableJobRunStatus.Completed, (await ExecuteAsync(extension, orphan)).Status);
        var recovered = Fixture.GetSnapshot(receiver);
        Assert.Equal(handle.Id, recovered.InboxJob?.Id);
        Assert.Equal(handle.ShardId, recovered.InboxJob?.ShardId);
        handler.Release();
        await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal(DurableJobRunStatus.Completed, (await ExecuteAsync(extension, orphan)).Status);
        Assert.Equal(0, Fixture.JobManagerProbe.GetAttemptCount(ReceiverTestServices.InboxJobName, receiver.GetGrainId()));
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

    [Fact]
    public async Task PreviousOwnerCallback_DuringReplacementPreparation_QuiescesUntilAcceptanceCommits()
    {
        var receiver = NewGrain();
        var oldJob = CreateJob(receiver, "previous-physical", "previous-shard", "previous:1");
        await receiver.SetInboxOwnershipAsync("previous:1", oldJob);
        await RefreshSeededOwnerAsync(receiver);
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
        var writes = Fixture.Storage.GetSuccessfulWriteCount(JournalId.FromGrainId(receiver.GetGrainId()));
        using var schedule = Fixture.JobManagerProbe.BlockNext(ReceiverTestServices.InboxJobName);
        using var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), "messages/replacement-quiescence");
        using var envelope = CreateEnvelope(receiver, NewMessage(116, "replacement-quiescence"), "messages/replacement-quiescence");
        using var timers = new DiagnosticEventCollector(GrainTimerEvents.ListenerName);
        var delivery = DeliverAsync(receiver, envelope.Value);
        await schedule.WaitUntilEnteredAsync();

        var callback = await InvokeDurableCallbackAsync(receiver, oldJob);

        Assert.Equal(DurableJobRunStatus.InProgress, callback.Status);
        Assert.DoesNotContain(timers.Events, item => item.Payload is GrainTimerEvents.Created created
            && ReferenceEquals(created.GrainContext, context));
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(JournalId.FromGrainId(receiver.GetGrainId())));
        Assert.False(delivery.IsCompleted);
        Assert.Equal(oldJob.Id, Fixture.GetSnapshot(receiver).InboxJob?.Id);
        schedule.Continue();
        Assert.Equal(DeliveryStatus.Accepted, (await delivery).Status);
        await handler.WaitUntilEnteredAsync();
        var accepted = Fixture.GetSnapshot(receiver);
        var replacement = Assert.Single(Fixture.JobManagerProbe.GetScheduledJobs(ReceiverTestServices.InboxJobName, receiver.GetGrainId()));
        Assert.Same(replacement, accepted.InboxJob);
        Assert.Equal(replacement.Metadata!["orleans.messaging.ownership-id"], accepted.InboxJobId);
        Assert.Equal(DurableJobRunStatus.Completed, (await InvokeDurableCallbackAsync(receiver, oldJob, dequeueCount: 2)).Status);
        Assert.False(grain.DeactivationFailure.Task.IsCompleted);
        handler.Release();
        var completed = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal(1, Assert.Single(completed.Effects).Count);
        Assert.Equal(1, completed.ProcessedMessageCount);
    }

    private static async Task<DurableJobRunResult> InvokeDurableCallbackAsync(IDurableMessagingTestGrain receiver, DurableJob job, int dequeueCount = 1)
    {
        var assembly = typeof(DurableJob).Assembly;
        var receiverType = assembly.GetType("Orleans.DurableJobs.IDurableJobReceiverExtension", throwOnError: true)!;
        var reference = receiver.AsReference(receiverType);
        var run = Activator.CreateInstance(assembly.GetType("Orleans.DurableJobs.JobRunContext", throwOnError: true)!,
            job, Guid.NewGuid().ToString("N"), dequeueCount)!;
        return await (ValueTask<DurableJobRunResult>)receiverType.GetMethod("HandleDurableJobAsync")!
            .Invoke(reference, [run, TestContext.Current.CancellationToken])!;
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
