using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling;
using Orleans.Messaging.Tests.Support;
using Xunit;

namespace Orleans.Messaging.Tests.Functional;

[Collection(MessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class InboxStateEncodingTests : MessagingBehaviorTestBase
{
    public InboxStateEncodingTests() : base(new FaultingInboxCodecFixture()) { }

    [Fact]
    public async Task CompleteCommandCodecFailure_PreservesOriginalCauseAndReplaysAcknowledgedState()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<MessagingTestGrain>(context.GrainInstance);
        var outbox = (JournaledTestOutbox)context.ActivationServices.GetRequiredService<IOutbox>();
        using var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), "messages/complete-codec-fault");
        var envelope = CreateEnvelope(receiver, NewMessage(204, "complete-codec-fault"), "messages/complete-codec-fault");
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope)).Status);
        await handler.WaitUntilEnteredAsync();
        var journal = JournalId.FromGrainId(receiver.GetGrainId());
        var writes = Fixture.Storage.GetSuccessfulWriteCount(journal);
        var failure = new IOException("Completion remove command failed.");
        ((FaultingInboxCodecFixture)Fixture).NextFailure = failure;
        handler.Release();
        Assert.Same(failure, await grain.DeactivationFailure.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(journal));
        Assert.Empty(outbox.Messages);
        _ = await receiver.GetSnapshotAsync();
        var fresh = Assert.IsType<MessagingTestGrain>(Fixture.GetGrainContext(receiver).GrainInstance);
        var replayed = Assert.IsType<EndpointSnapshot>(fresh.ReplayedSnapshot);
        Assert.Equal(1, replayed.InboxCount);
        Assert.Equal(0, replayed.ProcessedMessageCount);
        Assert.Empty(replayed.Effects);
        Assert.Equal(0, replayed.OutboxCount);
        var completed = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal(1, Assert.Single(completed.Effects).Count);
        Assert.Equal(1, completed.ProcessedMessageCount);
        Assert.Empty(completed.InboxDeadLetters);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InboxCodecFailure_PreventsStorageAndRetriedDeliveryProcessesOnce(bool snapshot)
    {
        var receiver = NewGrain();
        var before = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<MessagingTestGrain>(context.GrainInstance);
        var outbox = (JournaledTestOutbox)context.ActivationServices.GetRequiredService<IOutbox>();
        var journal = JournalId.FromGrainId(receiver.GetGrainId());
        var writes = Fixture.Storage.GetSuccessfulWriteCount(journal);
        var failure = new IOException("Injected inbox command codec failure.");
        var faulting = (FaultingInboxCodecFixture)Fixture;
        faulting.NextFailure = failure;
        faulting.FailOnSnapshot = snapshot;
        if (snapshot) Fixture.Storage.RequestSnapshot(journal);
        var envelope = CreateEnvelope(receiver, NewMessage(203, "codec"));
        await Assert.ThrowsAsync<IOException>(() => DeliverAsync(receiver, envelope));
        Assert.Equal(snapshot, faulting.FailedSnapshot);
        Assert.Empty(outbox.Messages);
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(journal));
        if (snapshot)
        {
            // The snapshot fails while the write is encoded, after the acceptance was applied, so the activation is discarded.
            Assert.Same(failure, await grain.DeactivationFailure.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.Equal(1, grain.GetSnapshotForTest().InboxCount);
            Assert.Empty(grain.GetSnapshotForTest().Effects);
            var recovered = await receiver.GetSnapshotAsync();
            Assert.NotEqual(before.ActivationId, recovered.ActivationId);
            Assert.Equal(0, recovered.InboxCount);
            Assert.Empty(recovered.Effects);
            Assert.Equal(0, recovered.ProcessedMessageCount);
        }
        else
        {
            // Commands are encoded before they are applied, so a rejected acceptance leaves the activation's state unchanged.
            Assert.False(grain.DeactivationFailure.Task.IsCompleted);
            var current = await receiver.GetSnapshotAsync();
            Assert.Equal(before.ActivationId, current.ActivationId);
            Assert.Equal(0, current.InboxCount);
            Assert.Empty(current.Effects);
            Assert.Equal(0, current.ProcessedMessageCount);
        }

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope)).Status);
        Assert.Equal(1, Assert.Single((await Fixture.WaitForEffectCountAsync(receiver, 1)).Effects).Count);
    }
}
