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
public sealed class OutboxDeliveryBehaviorTests : DurableMessagingBehaviorTestBase
{
    public OutboxDeliveryBehaviorTests() : base(receiverOnly: false)
    {
    }

    [Fact]
    public async Task StagedOutboxWithoutCommit_IsRemovedOnReactivationAndNeverDispatched()
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        var message = NewMessage(51, "uncommitted");

        await sender.StageWithoutCommitAsync(receiver.GetGrainId(), "messages/uncommitted", message);
        Assert.Equal(1, (await sender.GetSnapshotAsync()).OutboxCount);
        await sender.RequestDeactivationAsync();
        var reactivated = await sender.GetSnapshotAsync();

        Assert.Equal(0, reactivated.OutboxCount);
        Assert.Empty((await receiver.GetSnapshotAsync()).Effects);
    }

    [Fact]
    public async Task DuplicateOutboxEnqueue_PersistsOneStableJobOwnership()
    {
        var sender = NewGrain();
        var receiver = NewGrain();

        await sender.SendDuplicateAsync(
            receiver.GetGrainId(),
            "messages/duplicate-outbox-enqueue",
            NewMessage(54, "duplicate-enqueue"));
        var delivered = await Fixture.WaitForEffectCountAsync(receiver, 1);

        Assert.Equal(1, Assert.Single(delivered.Effects).Count);
        Assert.Equal(
            1,
            Fixture.JobManagerProbe.GetSuccessCount(
                "orleans.messaging.outbox-flush",
                sender.GetGrainId()));
    }

    [Fact]
    public async Task CommittedOutbox_ToSameGrain_DeliversLocally()
    {
        var grain = NewGrain();

        await grain.SendAsync(
            grain.GetGrainId(),
            "messages/loopback",
            NewMessage(82, "loopback"));
        var delivered = await Fixture.WaitForEffectCountAsync(grain, 1);
        delivered = await Fixture.WaitForOutboxCountAsync(grain, 0);

        Assert.Equal("loopback", Assert.Single(delivered.Effects).Value);
        Assert.Equal(0, delivered.InboxCount);
        Assert.Empty(delivered.InboxDeadLetters);
        Assert.Empty(delivered.OutboxDeadLetters);
    }

    [Fact]
    public async Task ReciprocalOutboxPumps_DoNotBlockInboxDelivery()
    {
        var first = NewGrain();
        var second = NewGrain();
        _ = await first.GetSnapshotAsync();
        _ = await second.GetSnapshotAsync();
        using var pumpBarrier = Fixture.OutboxPumpTimerProbe.BlockNext();

        var firstSend = first.SendAsync(
            second.GetGrainId(),
            "messages/reciprocal",
            NewMessage(83, "first-to-second"));
        var secondSend = second.SendAsync(
            first.GetGrainId(),
            "messages/reciprocal",
            NewMessage(84, "second-to-first"));
        try
        {
            await Task.WhenAll(firstSend, secondSend);
            await pumpBarrier.WaitUntilEnteredAsync();
        }
        finally
        {
            pumpBarrier.Release();
        }

        var completed = await Task.WhenAll(
            Fixture.SnapshotProbe.WaitAsync(
                first.GetGrainId(),
                static snapshot => snapshot.Effects.Any(effect => effect.Value == "second-to-first"),
                TimeSpan.FromSeconds(10)),
            Fixture.SnapshotProbe.WaitAsync(
                second.GetGrainId(),
                static snapshot => snapshot.Effects.Any(effect => effect.Value == "first-to-second"),
                TimeSpan.FromSeconds(10)));
        await Task.WhenAll(
            Fixture.WaitForOutboxCountAsync(first, 0),
            Fixture.WaitForOutboxCountAsync(second, 0));

        Assert.Contains(completed[0].Effects, effect => effect.Value == "second-to-first");
        Assert.Contains(completed[1].Effects, effect => effect.Value == "first-to-second");
    }

    [Fact]
    public async Task OutboxSchedulingFailure_FencesOldActivationAndFreshSendUsesNewOwnership()
    {
        const string jobName = "orleans.messaging.outbox-flush";
        var sender = NewGrain();
        var receiver = NewGrain();
        var before = await sender.GetSnapshotAsync();
        var oldContext = Fixture.GetGrainContext(sender);
        var oldManager = oldContext.ActivationServices.GetRequiredService<IJournaledStateManager>();
        var oldGrain = Assert.IsType<DurableMessagingTestGrain>(oldContext.GrainInstance);
        var message = NewMessage(55, "schedule-retry");
        Fixture.JobManagerProbe.FailAfterNext(jobName);

        await Assert.ThrowsAsync<IOException>(() => sender.SendAsync(receiver.GetGrainId(), "messages/schedule-retry", message));
        await oldContext.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.IsType<IOException>(await oldGrain.Faulted.Task);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => oldManager.WriteStateAsync(CancellationToken.None).AsTask());
        Assert.Empty((await receiver.GetSnapshotAsync()).Effects);
        var fresh = await sender.GetSnapshotAsync();
        Assert.NotEqual(before.ActivationId, fresh.ActivationId);
        Assert.Equal(0, fresh.OutboxCount);
        Assert.Null(fresh.OutboxJob);
        var orphan = Assert.Single(Fixture.JobManagerProbe.GetScheduledJobs(jobName, sender.GetGrainId()));

        var receiverWrite = Fixture.Storage.BlockWrite(JournalId.FromGrainId(receiver.GetGrainId()));
        try
        {
            await sender.SendAsync(receiver.GetGrainId(), "messages/schedule-retry", message);
            await receiverWrite.WaitUntilEnteredAsync();
            var committed = Fixture.GetSnapshot(sender);
            var jobs = Fixture.JobManagerProbe.GetScheduledJobs(jobName, sender.GetGrainId());
            Assert.Equal(2, jobs.Count);
            var owner = Assert.Single(jobs, job => job.Id != orphan.Id);
            Assert.NotEqual(orphan.Metadata!["orleans.messaging.ownership-id"], owner.Metadata!["orleans.messaging.ownership-id"]);
            Assert.Equal(owner.Metadata!["orleans.messaging.ownership-id"], committed.OutboxJobId);
            Assert.Equal(owner.Id, committed.OutboxJob?.Id);
            Assert.Equal(owner.ShardId, committed.OutboxJob?.ShardId);
        }
        finally
        {
            receiverWrite.Release();
        }
        var delivered = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal("schedule-retry", Assert.Single(delivered.Effects).Value);
        Assert.Equal(1, Assert.Single(delivered.Effects).Count);
        Assert.Equal(2, Fixture.JobManagerProbe.GetAttemptCount(jobName, sender.GetGrainId()));
    }

    [Fact]
    public async Task FailedAtomicWrite_FencesOldObjectsAndFreshReplayOmitsEffectAndOutgoingMessage()
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        var before = await sender.GetSnapshotAsync();
        var oldContext = Fixture.GetGrainContext(sender);
        var oldManager = oldContext.ActivationServices.GetRequiredService<IJournaledStateManager>();
        var oldGrain = Assert.IsType<DurableMessagingTestGrain>(oldContext.GrainInstance);
        await sender.StageEffectAsync(new DurableEffect(Guid.NewGuid(), 1, 53, "failed-write"));
        Fixture.Storage.FailWrite(JournalId.FromGrainId(sender.GetGrainId()));

        var failure = await Assert.ThrowsAsync<IOException>(() => sender.SendAsync(
            receiver.GetGrainId(), "messages/write-failure", NewMessage(53, "failed-write")));
        await oldContext.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(failure.Message, Assert.IsType<IOException>(await oldGrain.Faulted.Task).Message);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => oldManager.WriteStateAsync(CancellationToken.None).AsTask());
        var recovered = await sender.GetSnapshotAsync();

        Assert.NotEqual(before.ActivationId, recovered.ActivationId);
        Assert.NotSame(oldManager, Fixture.GetGrainContext(sender).ActivationServices.GetRequiredService<IJournaledStateManager>());
        Assert.Equal(0, recovered.OutboxCount);
        Assert.Empty(recovered.Effects);
        Assert.Empty((await receiver.GetSnapshotAsync()).Effects);
        Assert.Single(Fixture.JobManagerProbe.GetScheduledJobs("orleans.messaging.outbox-flush", sender.GetGrainId()));
    }

    [Fact]
    public async Task DeleteThenWrite_DiscardsPendingOutboxWithoutPoisoningNextWrite()
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        await sender.StageWithoutCommitAsync(
            receiver.GetGrainId(),
            "messages/delete-then-write",
            NewMessage(75, "delete-then-write"));

        await sender.DeleteThenWriteStateAsync();

        Assert.Equal(0, (await sender.GetSnapshotAsync()).OutboxCount);
        Assert.Empty((await receiver.GetSnapshotAsync()).Effects);
    }

    [Fact]
    public async Task BlockedInboxHandler_DoesNotStopIndependentOutboxAndInboxPumps()
    {
        var blocked = NewGrain();
        var independentSender = NewGrain();
        var independentReceiver = NewGrain();
        using var barrier = Fixture.HandlerProbe.Arm(blocked.GetGrainId(), "messages/blocked-pump");
        using var blockedEnvelope = CreateEnvelope(blocked, NewMessage(61, "blocked"), "messages/blocked-pump");

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(blocked, blockedEnvelope.Value)).Status);
        await barrier.WaitUntilEnteredAsync();
        await independentSender.SendAsync(
            independentReceiver.GetGrainId(),
            "messages/independent",
            NewMessage(62, "independent"));
        var independent = await Fixture.WaitForEffectCountAsync(independentReceiver, 1);

        Assert.Equal("independent", Assert.Single(independent.Effects).Value);
        Assert.Empty(Fixture.GetSnapshot(blocked).Effects);
        barrier.Release();
        Assert.Equal("blocked", Assert.Single((await Fixture.WaitForEffectCountAsync(blocked, 1)).Effects).Value);
    }
}
