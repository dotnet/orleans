using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableMessaging.Tests.Support;
using Orleans.Journaling;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Functional;

[Collection(DurableMessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class InboxStateEncodingTests : DurableMessagingBehaviorTestBase
{
    public InboxStateEncodingTests() : base(new FaultingInboxCodecFixture()) { }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InboxCodecFailure_FencesBeforeStorageAndFreshScopeReplaysOnlyAcknowledgedState(bool snapshot)
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
        var journal = JournalId.FromGrainId(receiver.GetGrainId());
        var writes = Fixture.Storage.GetSuccessfulWriteCount(journal);
        var failure = new IOException("Injected inbox command codec failure.");
        ((FaultingInboxCodecFixture)Fixture).NextFailure = failure;
        if (snapshot) Fixture.Storage.RequestSnapshot(journal);
        using var envelope = CreateEnvelope(receiver, NewMessage(203, "codec"));
        await Assert.ThrowsAsync<IOException>(() => DeliverAsync(receiver, envelope.Value));
        Assert.Same(failure, await grain.Faulted.Task);
        Assert.Same(failure, ((JournaledTestOutbox)context.ActivationServices.GetRequiredService<IDurableOutbox>()).Failure);
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(journal));
        Assert.Equal(1, grain.GetSnapshotForTest().InboxCount);
        Assert.Empty(grain.GetSnapshotForTest().Effects);
        await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var recovered = await receiver.GetSnapshotAsync();
        Assert.NotEqual(grain.GetSnapshotForTest().ActivationId, recovered.ActivationId);
        Assert.Equal(0, recovered.InboxCount);
        Assert.Empty(recovered.Effects);
        Assert.Equal(0, recovered.ProcessedMessageCount);
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        Assert.Equal(1, Assert.Single((await Fixture.WaitForEffectCountAsync(receiver, 1)).Effects).Count);
    }
}
