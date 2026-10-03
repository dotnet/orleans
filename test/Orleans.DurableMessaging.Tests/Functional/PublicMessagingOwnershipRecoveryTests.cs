using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableJobs;
using Orleans.DurableMessaging.Tests.Support;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Functional;

[Collection(DurableMessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class PublicMessagingOwnershipRecoveryTests : DurableMessagingBehaviorTestBase
{
    public PublicMessagingOwnershipRecoveryTests() : base(receiverOnly: false)
    {
    }

    [Fact]
    public async Task CommittedOutbox_DeactivationBeforeLocalFollowUp_RecoversDurableJobOwnership()
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        var before = await sender.GetSnapshotAsync();
        _ = await receiver.GetSnapshotAsync();
        var receiverWrite = Fixture.Storage.BlockWrite(JournalId.FromGrainId(receiver.GetGrainId()));

        await sender.SendAndDeactivateAsync(
            receiver.GetGrainId(),
            "messages/outbox-crash-window",
            NewMessage(52, "durable-wakeup"));
        await receiverWrite.WaitUntilEnteredAsync();
        var committed = Fixture.GetSnapshot(sender);

        Assert.Equal(1, committed.OutboxCount);
        Assert.False(string.IsNullOrEmpty(committed.OutboxJobId));
        var scheduledJob = Assert.Single(
            Fixture.JobManagerProbe.GetScheduledJobs("orleans.messaging.outbox-flush", sender.GetGrainId()));
        Assert.Equal(committed.OutboxJobId, scheduledJob.Metadata!["orleans.messaging.ownership-id"]);
        Assert.Equal(scheduledJob.Id, committed.OutboxJob?.Id);
        Assert.Equal(scheduledJob.ShardId, committed.OutboxJob?.ShardId);

        receiverWrite.Release();
        var delivered = await Fixture.WaitForEffectCountAsync(receiver, 1);
        var recovered = await Fixture.SnapshotProbe.WaitAsync(
            sender.GetGrainId(),
            snapshot => snapshot.ActivationId != before.ActivationId
                && snapshot.OutboxCount == 0
                && snapshot.OutboxJobId is null);

        Assert.Equal("durable-wakeup", Assert.Single(delivered.Effects).Value);
        Assert.NotEqual(before.ActivationId, recovered.ActivationId);
        Assert.Equal(0, recovered.OutboxCount);
        Assert.Null(recovered.OutboxJobId);
    }

    [Fact]
    public async Task Outbox_PrecommitCrash_ReclaimsScheduledOrphanAfterRecovery()
    {
        const string jobName = "orleans.messaging.outbox-flush";
        var sender = NewGrain();
        var receiver = NewGrain();
        var before = await sender.GetSnapshotAsync();
        _ = await receiver.GetSnapshotAsync();
        var oldContext = Fixture.GetGrainContext(sender);
        var barrier = Fixture.Storage.BlockWrite(JournalId.FromGrainId(sender.GetGrainId()));
        var attemptBaseline = Fixture.Metrics.GetCount("orleans-durablejobs-job-attempts-started");
        var completionBaseline = Fixture.Metrics.GetCount("orleans-durablejobs-jobs-completed");
        var orphanBaseline = Fixture.Metrics.GetCount(
            "orleans-durable-messaging-orphaned-jobs-reclaimed",
            jobName);
        Fixture.JobManagerProbe.DuplicateNext(jobName);

        var send = sender.SendAsync(
            receiver.GetGrainId(),
            "messages/outbox-orphan",
            NewMessage(54, "outbox-orphan"));
        await barrier.WaitUntilEnteredAsync();
        await Fixture.Metrics.WaitForCountAsync(
            "orleans-durablejobs-job-attempts-started",
            attemptBaseline + 2);

        Assert.Equal(
            orphanBaseline,
            Fixture.Metrics.GetCount("orleans-durable-messaging-orphaned-jobs-reclaimed", jobName));
        barrier.Fail();
        await Assert.ThrowsAnyAsync<Exception>(() => send);

        await oldContext.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var recovered = await sender.GetSnapshotAsync();
        Assert.NotEqual(before.ActivationId, recovered.ActivationId);
        await Fixture.Metrics.WaitForCountAsync(
            "orleans-durable-messaging-orphaned-jobs-reclaimed",
            orphanBaseline + 2,
            jobName);
        await Fixture.Metrics.WaitForCountAsync(
            "orleans-durablejobs-jobs-completed",
            completionBaseline + 2);

        Assert.Equal(0, recovered.OutboxCount);
        Assert.Null(recovered.OutboxJobId);
        Assert.Null(recovered.OutboxJob);
        Assert.Empty((await receiver.GetSnapshotAsync()).Effects);
        Assert.Equal(
            orphanBaseline + 2,
            Fixture.Metrics.GetCount("orleans-durable-messaging-orphaned-jobs-reclaimed", jobName));
    }

    [Fact]
    public async Task OutboxJobClearWriteFailure_FencesOldManagerAndFreshActivationCleansUp()
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        var before = await sender.GetSnapshotAsync();
        var oldContext = Fixture.GetGrainContext(sender);
        var oldManager = oldContext.ActivationServices.GetRequiredService<IJournaledStateManager>();
        var oldGrain = Assert.IsType<DurableMessagingTestGrain>(oldContext.GrainInstance);
        Fixture.Storage.FailWrite(JournalId.FromGrainId(sender.GetGrainId()), matchingWrite: 3);

        await sender.SendAsync(receiver.GetGrainId(), "messages/outbox-clear-retry", NewMessage(56, "outbox-clear-retry"));
        _ = await Fixture.WaitForEffectCountAsync(receiver, 1);
        await oldContext.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.IsType<IOException>(await oldGrain.DeactivationFailure.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => oldManager.WriteStateAsync(CancellationToken.None).AsTask());
        _ = await sender.GetSnapshotAsync();
        var cleaned = await Fixture.SnapshotProbe.WaitAsync(sender.GetGrainId(),
            snapshot => snapshot.ActivationId != before.ActivationId && snapshot.OutboxCount == 0 && snapshot.OutboxJobId is null);

        Assert.NotEqual(before.ActivationId, cleaned.ActivationId);
        Assert.NotSame(oldManager, Fixture.GetGrainContext(sender).ActivationServices.GetRequiredService<IJournaledStateManager>());
        Assert.Null(cleaned.OutboxJob);
        Assert.Equal(1, Assert.Single((await receiver.GetSnapshotAsync()).Effects).Count);
        await sender.RequestDeactivationAsync();
        var recovered = await sender.GetSnapshotAsync();
        Assert.Null(recovered.OutboxJobId);
        Assert.Null(recovered.OutboxJob);
        Assert.Equal(0, recovered.OutboxCount);
    }

    [Fact]
    public async Task Outbox_StaleGenerationCompletesWithoutClearingNewerOwner()
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        _ = await sender.GetSnapshotAsync();
        _ = await receiver.GetSnapshotAsync();
        var receiverWrite = Fixture.Storage.BlockWrite(JournalId.FromGrainId(receiver.GetGrainId()));

        await sender.SendAsync(
            receiver.GetGrainId(),
            "messages/stale-outbox-generation",
            NewMessage(61, "newer-outbox-owner"));
        await receiverWrite.WaitUntilEnteredAsync();
        var owned = Fixture.GetSnapshot(sender);
        Assert.False(string.IsNullOrEmpty(owned.OutboxJobId));
        var completionBaseline = Fixture.Metrics.GetCount("orleans-durablejobs-jobs-completed");
        var manager = Fixture.Cluster.Silos[0].ServiceProvider.GetRequiredService<ILocalDurableJobManager>();

        try
        {
            await manager.ScheduleJobAsync(
                new ScheduleJobRequest
                {
                    Target = sender.GetGrainId(),
                    JobName = "orleans.messaging.outbox-flush",
                    DueTime = DateTimeOffset.UtcNow,
                    Metadata = new Dictionary<string, string>
                    {
                        ["orleans.messaging.ownership-id"] = "0"
                    }
                },
                TestContext.Current.CancellationToken);
            await Fixture.Metrics.WaitForCountAsync(
                "orleans-durablejobs-jobs-completed",
                completionBaseline + 1);

            Assert.Equal(owned.OutboxJobId, Fixture.GetSnapshot(sender).OutboxJobId);
        }
        finally
        {
            receiverWrite.Release();
        }

        Assert.Equal("newer-outbox-owner", Assert.Single((await Fixture.WaitForEffectCountAsync(receiver, 1)).Effects).Value);
    }

    [Fact]
    public async Task Outbox_AmbiguousCommit_FreshReplayRetainsExactOwnerBeforeDelivery()
    {
        const string jobName = "orleans.messaging.outbox-flush";
        var sender = NewGrain();
        var receiver = NewGrain();
        var before = await sender.GetSnapshotAsync();
        _ = await receiver.GetSnapshotAsync();
        var oldContext = Fixture.GetGrainContext(sender);
        var journal = JournalId.FromGrainId(sender.GetGrainId());
        var recoveryRead = Fixture.Storage.BlockRead(journal);
        var receiverWrite = Fixture.Storage.BlockWrite(JournalId.FromGrainId(receiver.GetGrainId()));
        Fixture.Storage.FailAfterWrite(journal);

        await Assert.ThrowsAsync<IOException>(() => sender.SendAsync(
            receiver.GetGrainId(), "messages/recovery-visibility", NewMessage(62, "recovery-visibility")));
        await oldContext.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var activation = sender.GetSnapshotAsync();
        try
        {
            await recoveryRead.WaitUntilEnteredAsync();
            var owner = Assert.Single(Fixture.JobManagerProbe.GetScheduledJobs(jobName, sender.GetGrainId()));
            Assert.Empty((await receiver.GetSnapshotAsync()).Effects);
            recoveryRead.Release();
            await receiverWrite.WaitUntilEnteredAsync();
            var recovered = await activation;

            Assert.NotEqual(before.ActivationId, recovered.ActivationId);
            Assert.Equal(owner.Metadata!["orleans.messaging.ownership-id"], recovered.OutboxJobId);
            Assert.Equal(owner.Id, recovered.OutboxJob?.Id);
            Assert.Equal(owner.ShardId, recovered.OutboxJob?.ShardId);
            Assert.Equal(1, recovered.OutboxCount);
            Assert.Single(Fixture.JobManagerProbe.GetScheduledJobs(jobName, sender.GetGrainId()));
        }
        finally
        {
            recoveryRead.Release();
            receiverWrite.Release();
        }

        var delivered = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal("recovery-visibility", Assert.Single(delivered.Effects).Value);
        Assert.Equal(1, Assert.Single(delivered.Effects).Count);
        Assert.Equal(0, (await Fixture.WaitForOutboxCountAsync(sender, 0)).OutboxCount);
    }

}
