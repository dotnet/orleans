using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableJobs;
using Orleans.Journaling;
using Orleans.Messaging.Tests.Support;
using Xunit;

namespace Orleans.Messaging.Tests.Functional;

[Collection(MessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class OutboxJournalRecoveryTests : MessagingBehaviorTestBase
{
    public OutboxJournalRecoveryTests() : base(receiverOnly: false)
    {
    }

    [Fact]
    public async Task CommittedOutbox_DeactivationBeforeLocalFollowUp_RecoversPendingWork()
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

        Assert.Equal(1, Fixture.GetSnapshot(sender).OutboxCount);
        Assert.NotEmpty(Fixture.JobManagerProbe.GetScheduledJobs(MessagingPump.JobName, sender.GetGrainId()));

        receiverWrite.Release();
        var delivered = await Fixture.WaitForEffectCountAsync(receiver, 1);
        var recovered = await Fixture.SnapshotProbe.WaitAsync(
            sender.GetGrainId(),
            snapshot => snapshot.ActivationId != before.ActivationId && snapshot.OutboxCount == 0);

        Assert.Equal("durable-wakeup", Assert.Single(delivered.Effects).Value);
        Assert.NotEqual(before.ActivationId, recovered.ActivationId);
    }

    [Fact]
    public async Task OutboxRemovalWriteFailure_FencesOldManagerAndFreshActivationCleansUp()
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        var before = await sender.GetSnapshotAsync();
        var oldContext = Fixture.GetGrainContext(sender);
        var oldManager = oldContext.ActivationServices.GetRequiredService<IJournaledStateManager>();
        var oldGrain = Assert.IsType<MessagingTestGrain>(oldContext.GrainInstance);
        Fixture.Storage.FailWrite(JournalId.FromGrainId(sender.GetGrainId()), matchingWrite: 2);

        await sender.SendAsync(receiver.GetGrainId(), "messages/outbox-clear-retry", NewMessage(56, "outbox-clear-retry"));
        _ = await Fixture.WaitForEffectCountAsync(receiver, 1);
        await oldContext.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.IsType<IOException>(await oldGrain.DeactivationFailure.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        _ = await sender.GetSnapshotAsync();
        var cleaned = await Fixture.SnapshotProbe.WaitAsync(sender.GetGrainId(),
            snapshot => snapshot.ActivationId != before.ActivationId && snapshot.OutboxCount == 0);

        Assert.NotSame(oldManager, Fixture.GetGrainContext(sender).ActivationServices.GetRequiredService<IJournaledStateManager>());
        Assert.Equal(1, Assert.Single((await receiver.GetSnapshotAsync()).Effects).Count);
        await sender.RequestDeactivationAsync();
        Assert.Equal(0, (await sender.GetSnapshotAsync()).OutboxCount);
    }

    [Fact]
    public async Task Outbox_SpuriousWakeupPreservesBlockedPendingWork()
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        _ = await sender.GetSnapshotAsync();
        _ = await receiver.GetSnapshotAsync();
        var receiverWrite = Fixture.Storage.BlockWrite(JournalId.FromGrainId(receiver.GetGrainId()));

        await sender.SendAsync(receiver.GetGrainId(), "messages/spurious-outbox-wakeup", NewMessage(61, "spurious-wakeup"));
        await receiverWrite.WaitUntilEnteredAsync();
        Assert.Equal(1, Fixture.GetSnapshot(sender).OutboxCount);
        var attemptBaseline = Fixture.Metrics.GetCount("orleans-durablejobs-job-attempts-started");
        var manager = Fixture.Cluster.Silos[0].ServiceProvider.GetRequiredService<ILocalDurableJobManager>();

        try
        {
            await manager.ScheduleJobAsync(
                new ScheduleJobRequest
                {
                    Target = sender.GetGrainId(),
                    JobName = MessagingPump.JobName,
                    DueTime = DateTimeOffset.UtcNow
                },
                TestContext.Current.CancellationToken);
            await Fixture.Metrics.WaitForCountAsync("orleans-durablejobs-job-attempts-started", attemptBaseline + 1);

            Assert.Equal(1, Fixture.GetSnapshot(sender).OutboxCount);
        }
        finally
        {
            receiverWrite.Release();
        }

        Assert.Equal("spurious-wakeup", Assert.Single((await Fixture.WaitForEffectCountAsync(receiver, 1)).Effects).Value);
        Assert.Equal(0, (await Fixture.WaitForOutboxCountAsync(sender, 0)).OutboxCount);
    }

    [Fact]
    public async Task Outbox_AmbiguousCommit_FreshReplayRestoresMessagesBeforeDelivery()
    {
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
            Assert.Empty((await receiver.GetSnapshotAsync()).Effects);
            recoveryRead.Release();
            await receiverWrite.WaitUntilEnteredAsync();
            var recovered = await activation;

            Assert.NotEqual(before.ActivationId, recovered.ActivationId);
            Assert.Equal(1, recovered.OutboxCount);
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
