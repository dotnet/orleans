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
public sealed class InboxAcceptanceBehaviorTests : DurableMessagingBehaviorTestBase
{
    [Fact]
    public async Task Deliver_AcceptedOnlyAfterInboxAndStableJobOwnershipAreDurable()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var journalId = JournalId.FromGrainId(receiver.GetGrainId());
        var barrier = Fixture.Storage.BlockWrite(journalId);
        using var envelope = CreateEnvelope(receiver, NewMessage(1, "durability"));

        var delivery = DeliverAsync(receiver, envelope.Value);
        await barrier.WaitUntilEnteredAsync();

        Assert.False(delivery.IsCompleted);
        var staged = Fixture.GetSnapshot(receiver);
        Assert.Equal(1, staged.InboxCount);
        Assert.Empty(staged.Effects);
        var scheduledJob = Assert.Single(
            Fixture.JobManagerProbe.GetScheduledJobs("orleans.messaging.inbox-drain", receiver.GetGrainId()));
        Assert.Equal(staged.InboxJobId, scheduledJob.Metadata!["orleans.messaging.ownership-id"]);
        Assert.Equal(scheduledJob.Id, staged.InboxJob?.Id);
        Assert.Equal(scheduledJob.ShardId, staged.InboxJob?.ShardId);

        barrier.Release();
        var result = await delivery;
        Assert.Equal(DeliveryStatus.Accepted, result.Status);
        DurableEndpointSnapshot completed;
        try
        {
            completed = await Fixture.WaitForEffectCountAsync(receiver, 1);
        }
        catch (TimeoutException exception)
        {
            var snapshot = await receiver.GetSnapshotAsync();
            throw new TimeoutException(
                $"Accepted message did not drain. Inbox={snapshot.InboxCount}, effects={snapshot.Effects.Count}, deadLetters={snapshot.InboxDeadLetters.Count}, job={snapshot.InboxJobId}.",
                exception);
        }
        Assert.Single(completed.Effects);
        Assert.Equal(0, completed.InboxCount);
        Assert.Null(completed.InboxJob);
        Assert.True(Fixture.Storage.GetSuccessfulWriteCount(journalId) >= 2);
    }

    [Fact]
    public async Task Deliver_CancellationStopsWaitingForInboxGate()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var barrier = Fixture.Storage.BlockWrite(JournalId.FromGrainId(receiver.GetGrainId()));
        using var firstEnvelope = CreateEnvelope(receiver, NewMessage(72, "holds-gate"));
        using var secondEnvelope = CreateEnvelope(receiver, NewMessage(73, "canceled"));
        var firstDelivery = DeliverAsync(receiver, firstEnvelope.Value);
        await barrier.WaitUntilEnteredAsync();
        using var cancellation = new CancellationTokenSource();

        var canceledDelivery = DeliverWithCancellationAsync(
            receiver,
            secondEnvelope.Value,
            cancellation.Token);
        Assert.False(canceledDelivery.IsCompleted);
        cancellation.Cancel();

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledDelivery);
        }
        finally
        {
            barrier.Release();
        }

        Assert.Equal(DeliveryStatus.Accepted, (await firstDelivery).Status);
    }

    [Fact]
    public async Task ConcurrentWriteCannotCaptureInboxAcceptanceBeforeScheduling()
    {
        var receiver = NewGrain();
        using var schedule = Fixture.JobManagerProbe.BlockNext("orleans.messaging.inbox-drain");
        using var envelope = CreateEnvelope(receiver, NewMessage(74, "schedule-barrier"));

        var delivery = DeliverAsync(receiver, envelope.Value);
        await schedule.WaitUntilEnteredAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Fixture.WriteStateAsync(receiver).AsTask());
        Assert.Contains("waiting for job scheduling", exception.Message, StringComparison.Ordinal);

        schedule.Continue();
        Assert.Equal(DeliveryStatus.Accepted, (await delivery).Status);
        var completed = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal("schedule-barrier", Assert.Single(completed.Effects).Value);
    }

    [Fact]
    public async Task ConcurrentDuplicateDeliveries_ConvergeToOneEffectWithinRetention()
    {
        var receiver = NewGrain();
        using var barrier = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), "messages/blocked-duplicate");
        using var envelope = CreateEnvelope(receiver, NewMessage(11, "duplicate"), "messages/blocked-duplicate");

        var first = await DeliverAsync(receiver, envelope.Value);
        await barrier.WaitUntilEnteredAsync();
        var second = DeliverAsync(receiver, envelope.Value);

        Assert.Equal(DeliveryStatus.Accepted, first.Status);
        Assert.False(second.IsCompleted);
        Assert.Equal(1, Fixture.GetSnapshot(receiver).InboxCount);

        barrier.Release();
        Assert.Equal(DeliveryStatus.Duplicate, (await second).Status);
        var state = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal(1, Assert.Single(state.Effects).Count);
        Assert.Equal(1, state.MaxConcurrentHandlers);
    }

    [Fact]
    public async Task DuplicateAfterReactivationWithinRetention_RemainsEffectivelyOnce()
    {
        var receiver = NewGrain();
        using var envelope = CreateEnvelope(receiver, NewMessage(13, "reactivation"));

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        var before = await Fixture.WaitForEffectCountAsync(receiver, 1);
        await receiver.RequestDeactivationAsync();
        var after = await receiver.GetSnapshotAsync();

        Assert.NotEqual(before.ActivationId, after.ActivationId);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, envelope.Value)).Status);
        Assert.Equal(1, Assert.Single((await receiver.GetSnapshotAsync()).Effects).Count);
    }

    [Fact]
    public async Task ReorderedDistinctAndDuplicateMessages_ConvergeByApplicationSequence()
    {
        var receiver = NewGrain();
        var messages = new[]
        {
            NewMessage(3, "third"),
            NewMessage(1, "first"),
            NewMessage(2, "second"),
        };

        foreach (var message in messages)
        {
            using var envelope = CreateEnvelope(receiver, message);
            Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
            Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, envelope.Value)).Status);
        }

        var state = await Fixture.WaitForEffectCountAsync(receiver, 3);
        Assert.Equal([1, 2, 3], state.Effects.Select(static effect => effect.Sequence));
        Assert.All(state.Effects, static effect => Assert.Equal(1, effect.Count));
    }

    [Fact]
    public async Task ConcurrentDelivery_WaitsWhileHandlersRemainSequential()
    {
        var receiver = NewGrain();
        using var barrier = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), "messages/sequential");
        using var first = CreateEnvelope(receiver, NewMessage(21, "first"), "messages/sequential");
        using var second = CreateEnvelope(receiver, NewMessage(22, "second"), "messages/sequential");

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, first.Value)).Status);
        await WaitForBarrierAsync(receiver, barrier);
        var secondDelivery = DeliverAsync(receiver, second.Value);
        Assert.False(secondDelivery.IsCompleted);

        barrier.Release();
        Assert.Equal(DeliveryStatus.Accepted, (await secondDelivery).Status);
        DurableEndpointSnapshot state;
        try
        {
            state = await Fixture.WaitForEffectCountAsync(receiver, 2);
        }
        catch (TimeoutException exception)
        {
            var snapshot = await receiver.GetSnapshotAsync();
            throw new TimeoutException(
                $"Second message did not complete. Inbox={snapshot.InboxCount}, effects={snapshot.Effects.Count}, deadLetters={snapshot.InboxDeadLetters.Count}.",
                exception);
        }
        Assert.Equal(1, state.MaxConcurrentHandlers);
        Assert.Equal([21, 22], state.Effects.Select(static effect => effect.Sequence));
    }
}
