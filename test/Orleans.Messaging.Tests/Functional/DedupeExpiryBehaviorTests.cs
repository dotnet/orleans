using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling;
using Orleans.Messaging.Tests.Support;
using Orleans.Runtime;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.Messaging.Tests.Functional;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DedupeExpiryClusterCollection : ICollectionFixture<DedupeExpiryClusterFixture>
{
    public const string Name = "Messaging dedupe expiry cluster";
}

[Collection(DedupeExpiryClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class DedupeExpiryBehaviorTests(DedupeExpiryClusterFixture fixture)
{
    [Fact]
    public async Task IdleReplay_AtDeduplicationBoundary_IsAcceptedWithoutCompactionTrigger()
    {
        var receiver = fixture.Client.GetGrain<IMessagingTestGrain>(Guid.NewGuid());
        var original = new TestMessage(TestApplicationProtocol.NewMessageId(), 15, "expires");
        var first = CreateEnvelope(receiver, original);

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, first)).Status);
        await fixture.WaitForEffectCountAsync(receiver, 1);
        await WaitForIdleInboxAsync(receiver);
        fixture.Clock.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromTicks(1));

        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, first)).Status);
        Assert.Equal(1, Assert.Single((await receiver.GetSnapshotAsync()).Effects).Count);

        fixture.Clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, first)).Status);
        var state = await fixture.WaitForEffectCountAsync(receiver, 2);

        Assert.Equal(2, state.Effects.Single(effect => effect.LogicalId == original.LogicalId).Count);
    }

    [Fact]
    public async Task ExpiryReplacementFailure_ReplaysDedupeRecordBeforeExpiryMaintenance()
    {
        var receiver = fixture.Client.GetGrain<IMessagingTestGrain>(Guid.NewGuid());
        var envelope = CreateEnvelope(
            receiver,
            new TestMessage(TestApplicationProtocol.NewMessageId(), 16, "failed-expiry-replacement"));

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope)).Status);
        await fixture.WaitForEffectCountAsync(receiver, 1);
        await WaitForIdleInboxAsync(receiver);
        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        var oldContext = fixture.GetGrainContext(receiver);
        fixture.Storage.FailWrite(JournalId.FromGrainId(receiver.GetGrainId()));

        await Assert.ThrowsAnyAsync<Exception>(() => DeliverAsync(receiver, envelope));

        await oldContext.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var failed = await receiver.GetSnapshotAsync();
        Assert.Equal(0, failed.InboxCount);
        Assert.Equal(1, Assert.Single(failed.Effects).Count);
        var replayed = Assert.IsType<MessagingTestGrain>(fixture.GetGrainContext(receiver).GrainInstance).ReplayedSnapshot;
        Assert.Equal(1, Assert.IsType<EndpointSnapshot>(replayed).ProcessedMessageCount);
        // Expired dedupe records remain until a snapshot compacts them, but they no longer deduplicate.
        Assert.Equal(1, failed.ProcessedMessageCount);

        await receiver.RequestDeactivationAsync();
        var recovered = await receiver.GetSnapshotAsync();
        Assert.NotEqual(failed.ActivationId, recovered.ActivationId);
        Assert.Equal(0, recovered.InboxCount);
        Assert.Equal(1, recovered.ProcessedMessageCount);
        Assert.Equal(1, Assert.Single(recovered.Effects).Count);

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope)).Status);
        var retried = await fixture.WaitForEffectCountAsync(receiver, 2);
        Assert.Equal(2, Assert.Single(retried.Effects).Count);
    }

    private Task<EndpointSnapshot> WaitForIdleInboxAsync(IMessagingTestGrain receiver) =>
        fixture.SnapshotProbe.WaitAsync(
            receiver.GetGrainId(),
            static snapshot => snapshot.InboxCount == 0);

    private static async Task<DeliveryResult> DeliverAsync(
        IMessagingTestGrain receiver,
        InboxMessage envelope) =>
        await receiver.AsReference<IMessagingExtension>().DeliverAsync(envelope);

    private InboxMessage CreateEnvelope(
        IMessagingTestGrain receiver,
        TestMessage message,
        MessageId? messageId = null)
    {
        var sessions = fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var sender = GrainId.Create("expiry-test-sender", "stable");
        var built = TestApplicationProtocol.CreateInbox(sessions, sender, "messages/expiry", message);
        if (messageId is { } id)
        {
            built = TestApplicationProtocol.With(built, sessions, messageId: id);
        }

        return built;
    }
}
