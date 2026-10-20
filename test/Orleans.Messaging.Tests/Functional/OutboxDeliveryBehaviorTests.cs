using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableJobs;
using Orleans.Journaling;
using Orleans.Messaging.Tests.Support;
using Orleans.Runtime;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.Messaging.Tests.Functional;

[Collection(MessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class OutboxDeliveryBehaviorTests : MessagingBehaviorTestBase
{
    public OutboxDeliveryBehaviorTests() : base(receiverOnly: false)
    {
    }

    [Fact]
    public async Task OutboxSends_ReportTheirStagedChanges()
    {
        // The outbox journals every send when it is staged, so a failed handler attempt can tell whether it sent
        // anything, and an unrelated failure doesn't cost the activation.
        var sender = NewGrain();
        _ = await sender.GetSnapshotAsync();
        var pump = Fixture.GetGrainContext(sender).ActivationServices.GetRequiredService<MessagingPump>();
        var before = await Fixture.OnGrainAsync(sender, () => pump.StagedChangeCount);

        _ = await sender.StageWithoutCommitAsync(NewGrain().GetGrainId(), "messages/staged-change", NewMessage(150, "staged-change"));

        Assert.True(before >= 0);
        Assert.True(await Fixture.OnGrainAsync(sender, () => pump.StagedChangeCount) > before);
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
    public async Task TransactionalSend_IsDeliveredOnceTheTransactionReturns()
    {
        var sender = NewGrain();
        var receiver = NewGrain();

        // The transaction returns once the write which holds its send completed, so the send needs no further write.
        await sender.SendInTransactionAsync(receiver.GetGrainId(), "messages/transactional", NewMessage(56, "transactional"), abort: false);
        var delivered = await Fixture.WaitForEffectCountAsync(receiver, 1);

        Assert.Equal("transactional", Assert.Single(delivered.Effects).Value);
        await Fixture.WaitForOutboxCountAsync(sender, 0);
    }

    [Fact]
    public async Task AbortedTransactionalSend_IsNeverDelivered()
    {
        var sender = NewGrain();
        var receiver = NewGrain();

        await sender.SendInTransactionAsync(receiver.GetGrainId(), "messages/transactional", NewMessage(57, "aborted"), abort: true);
        Assert.Equal(0, (await sender.GetSnapshotAsync()).OutboxCount);

        // Once a later send is delivered and the sender's outbox is empty, nothing else can arrive from the sender.
        await sender.SendAsync(receiver.GetGrainId(), "messages/transactional", NewMessage(58, "committed"));
        await Fixture.WaitForEffectCountAsync(receiver, 1);
        await Fixture.WaitForOutboxCountAsync(sender, 0);
        Assert.Equal("committed", Assert.Single((await receiver.GetSnapshotAsync()).Effects).Value);
        await sender.RequestDeactivationAsync();
        Assert.Equal(0, (await sender.GetSnapshotAsync()).OutboxCount);
    }

    [Fact]
    public async Task TransactionalSendToSameGrain_IsProcessedOnceAfterTheCommit()
    {
        var grain = NewGrain();

        await grain.SendInTransactionAsync(grain.GetGrainId(), "messages/loopback", NewMessage(59, "loopback"), abort: false);
        var delivered = await Fixture.WaitForEffectCountAsync(grain, 1);
        Assert.Equal("loopback", Assert.Single(delivered.Effects).Value);

        // The inbox's acceptance and the message's completion are durable in that order, so recovery doesn't process it again.
        await grain.RequestDeactivationAsync();
        var recovered = await grain.GetSnapshotAsync();
        Assert.Equal("loopback", Assert.Single(recovered.Effects).Value);
        Assert.Equal(0, recovered.InboxCount);
    }

    [Fact]
    public async Task AbortedTransactionalSendToSameGrain_IsNeverProcessed()
    {
        var grain = NewGrain();

        await grain.SendInTransactionAsync(grain.GetGrainId(), "messages/loopback", NewMessage(60, "aborted"), abort: true);
        Assert.Equal(0, (await grain.GetSnapshotAsync()).InboxCount);

        // A later send to the grain is processed; the aborted one never is, live or after recovery.
        await grain.SendAsync(grain.GetGrainId(), "messages/loopback", NewMessage(61, "committed"));
        await Fixture.WaitForEffectCountAsync(grain, 1);
        Assert.Equal("committed", Assert.Single((await grain.GetSnapshotAsync()).Effects).Value);
        await grain.RequestDeactivationAsync();
        var recovered = await grain.GetSnapshotAsync();
        Assert.Equal("committed", Assert.Single(recovered.Effects).Value);
        Assert.Equal(0, recovered.InboxCount);
    }
    [Fact]
    public async Task DuplicateOutboxEnqueue_UsesOneDurableWakeup()
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
                MessagingPump.JobName,
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

        var firstSend = first.SendAsync(
            second.GetGrainId(),
            "messages/reciprocal",
            NewMessage(83, "first-to-second"));
        var secondSend = second.SendAsync(
            first.GetGrainId(),
            "messages/reciprocal",
            NewMessage(84, "second-to-first"));
        await Task.WhenAll(firstSend, secondSend);

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
    public async Task OutboxSchedulingPrerequisiteFailure_KeepsActivationHealthyAndRetryEstablishesWakeup()
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        var before = await sender.GetSnapshotAsync();
        var oldContext = Fixture.GetGrainContext(sender);
        var oldManager = oldContext.ActivationServices.GetRequiredService<IJournaledStateManager>();
        var message = NewMessage(55, "schedule-retry");
        Fixture.JobManagerProbe.FailAfterNext(MessagingPump.JobName);

        var prerequisite = await Assert.ThrowsAsync<JournaledStatePreCommitException>(() => sender.SendAsync(receiver.GetGrainId(), "messages/schedule-retry", message));
        Assert.IsType<IOException>(prerequisite.InnerException);
        Assert.False(oldContext.Deactivated.IsCompleted);
        Assert.Empty((await receiver.GetSnapshotAsync()).Effects);
        var healthy = await sender.GetSnapshotAsync();
        Assert.Equal(before.ActivationId, healthy.ActivationId);
        Assert.Same(oldManager, Fixture.GetGrainContext(sender).ActivationServices.GetRequiredService<IJournaledStateManager>());
        Assert.Equal(1, healthy.OutboxCount);
        Assert.Single(Fixture.JobManagerProbe.GetScheduledJobs(MessagingPump.JobName, sender.GetGrainId()));

        var receiverWrite = Fixture.Storage.BlockWrite(JournalId.FromGrainId(receiver.GetGrainId()));
        try
        {
            await sender.RetryWriteStateAsync();
            await receiverWrite.WaitUntilEnteredAsync();
            var committed = Fixture.GetSnapshot(sender);
            var jobs = Fixture.JobManagerProbe.GetScheduledJobs(MessagingPump.JobName, sender.GetGrainId());
            Assert.NotEmpty(jobs);
            Assert.All(jobs, job =>
            {
                Assert.Null(job.Metadata);
                Assert.Equal(sender.GetGrainId(), job.TargetGrainId);
            });
            Assert.Equal(1, committed.OutboxCount);
        }
        finally
        {
            receiverWrite.Release();
        }
        var delivered = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal("schedule-retry", Assert.Single(delivered.Effects).Value);
        Assert.Equal(1, Assert.Single(delivered.Effects).Count);
        Assert.True(Fixture.JobManagerProbe.GetAttemptCount(MessagingPump.JobName, sender.GetGrainId()) >= 1);
    }

    [Fact]
    public async Task FailedAtomicWrite_FencesOldObjectsAndFreshReplayOmitsEffectAndOutgoingMessage()
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        var before = await sender.GetSnapshotAsync();
        var oldContext = Fixture.GetGrainContext(sender);
        var oldManager = oldContext.ActivationServices.GetRequiredService<IJournaledStateManager>();
        var oldGrain = Assert.IsType<MessagingTestGrain>(oldContext.GrainInstance);
        await sender.StageEffectAsync(new MessageEffect(MessageId.Create("outbox", "failed-write"), 1, 53, "failed-write"));
        Fixture.Storage.FailWrite(JournalId.FromGrainId(sender.GetGrainId()));

        var failure = await Assert.ThrowsAsync<IOException>(() => sender.SendAsync(
            receiver.GetGrainId(), "messages/write-failure", NewMessage(53, "failed-write")));
        await oldContext.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(failure.Message, Assert.IsType<IOException>(
            await oldGrain.DeactivationFailure.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)).Message);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => oldManager.WriteStateAsync(CancellationToken.None).AsTask());
        var recovered = await sender.GetSnapshotAsync();

        Assert.NotEqual(before.ActivationId, recovered.ActivationId);
        Assert.NotSame(oldManager, Fixture.GetGrainContext(sender).ActivationServices.GetRequiredService<IJournaledStateManager>());
        Assert.Equal(0, recovered.OutboxCount);
        Assert.Empty(recovered.Effects);
        Assert.Empty((await receiver.GetSnapshotAsync()).Effects);
        Assert.Single(Fixture.JobManagerProbe.GetScheduledJobs(MessagingPump.JobName, sender.GetGrainId()));
    }

    [Fact]
    public async Task StopDeleteAndDeactivate_DiscardsUncommittedOutboxAndFreshOwnerCanSend()
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        await sender.StageWithoutCommitAsync(
            receiver.GetGrainId(),
            "messages/delete-uncommitted",
            NewMessage(75, "deleted-intent"));

        var before = await sender.GetSnapshotAsync();
        var oldContext = Fixture.GetGrainContext(sender);
        Assert.Equal(1, before.OutboxCount);
        await sender.DeleteStateAndDeactivateAsync();
        await oldContext.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var fresh = await sender.GetSnapshotAsync();

        Assert.NotEqual(before.ActivationId, fresh.ActivationId);
        Assert.Equal(0, fresh.OutboxCount);
        Assert.Empty((await receiver.GetSnapshotAsync()).Effects);

        var effect = new MessageEffect(MessageId.Create("outbox", "fresh-owner"), 1, 76, "fresh-owner");
        await sender.StageEffectAsync(effect);
        var drained = Fixture.SnapshotProbe.WaitAsync(
            sender.GetGrainId(),
            snapshot => snapshot.ActivationId == fresh.ActivationId
                && snapshot.OutboxCount == 0
                && snapshot.Effects.Contains(effect));
        await sender.SendAsync(receiver.GetGrainId(), "messages/after-delete", NewMessage(76, "after-delete"));
        var delivered = await Fixture.WaitForEffectCountAsync(receiver, 1);
        await drained;
        Assert.Equal(0, (await sender.GetSnapshotAsync()).OutboxCount);
        Assert.Equal("after-delete", Assert.Single(delivered.Effects).Value);
    }

    [Fact]
    public async Task BlockedInboxHandler_DoesNotStopIndependentOutboxAndInboxPumps()
    {
        var blocked = NewGrain();
        var independentSender = NewGrain();
        var independentReceiver = NewGrain();
        using var barrier = Fixture.HandlerProbe.Arm(blocked.GetGrainId(), "messages/blocked-pump");
        var blockedEnvelope = CreateEnvelope(blocked, NewMessage(61, "blocked"), "messages/blocked-pump");

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(blocked, blockedEnvelope)).Status);
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
