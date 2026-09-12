using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableMessaging.Tests.Support;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Functional;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DedupeExpiryClusterCollection : ICollectionFixture<DedupeExpiryClusterFixture>
{
    public const string Name = "Durable messaging dedupe expiry cluster";
}

[Collection(DedupeExpiryClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class DedupeExpiryBehaviorTests(DedupeExpiryClusterFixture fixture)
{
    [Fact]
    public async Task IdleReplay_AtDeduplicationBoundary_IsAcceptedWithoutCompactionTrigger()
    {
        var receiver = fixture.Client.GetGrain<IDurableMessagingTestGrain>(Guid.NewGuid());
        var original = new DurableTestMessage(Guid.NewGuid(), 15, "expires");
        using var first = CreateEnvelope(receiver, original);

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, first.Value)).Status);
        await fixture.WaitForEffectCountAsync(receiver, 1);
        await WaitForIdleInboxAsync(receiver);
        fixture.Clock.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromTicks(1));

        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, first.Value)).Status);
        Assert.Equal(1, Assert.Single((await receiver.GetSnapshotAsync()).Effects).Count);

        fixture.Clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, first.Value)).Status);
        var state = await fixture.WaitForEffectCountAsync(receiver, 2);

        Assert.Equal(2, state.Effects.Single(effect => effect.LogicalId == original.LogicalId).Count);
    }

    [Fact]
    public async Task ExpiryReplacement_WhenJournalWriteFails_RetainsDedupeRecord()
    {
        var receiver = fixture.Client.GetGrain<IDurableMessagingTestGrain>(Guid.NewGuid());
        using var envelope = CreateEnvelope(
            receiver,
            new DurableTestMessage(Guid.NewGuid(), 16, "failed-expiry-replacement"));

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        await fixture.WaitForEffectCountAsync(receiver, 1);
        await WaitForIdleInboxAsync(receiver);
        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        fixture.Storage.FailWrite(JournalId.FromGrainId(receiver.GetGrainId()));

        await Assert.ThrowsAnyAsync<Exception>(() => DeliverAsync(receiver, envelope.Value));

        var failed = await receiver.GetSnapshotAsync();
        Assert.Equal(0, failed.InboxCount);
        Assert.Equal(1, Assert.Single(failed.Effects).Count);
        Assert.Equal(1, failed.ProcessedMessageCount);

        await receiver.RequestDeactivationAsync();
        var recovered = await receiver.GetSnapshotAsync();
        Assert.NotEqual(failed.ActivationId, recovered.ActivationId);
        Assert.Equal(0, recovered.InboxCount);
        Assert.Equal(1, recovered.ProcessedMessageCount);
        Assert.Equal(1, Assert.Single(recovered.Effects).Count);

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        var retried = await fixture.WaitForEffectCountAsync(receiver, 2);
        Assert.Equal(2, Assert.Single(retried.Effects).Count);
    }

    private Task<DurableEndpointSnapshot> WaitForIdleInboxAsync(IDurableMessagingTestGrain receiver) =>
        fixture.SnapshotProbe.WaitAsync(
            receiver.GetGrainId(),
            static snapshot => snapshot.InboxCount == 0 && string.IsNullOrEmpty(snapshot.InboxJobId));

    private static async Task<DeliveryResult> DeliverAsync(
        IDurableMessagingTestGrain receiver,
        DurableEnvelope envelope) =>
        await receiver.AsReference<IDurableInboxExtension>().DeliverAsync(envelope);

    private EnvelopeLease CreateEnvelope(
        IDurableMessagingTestGrain receiver,
        DurableTestMessage message,
        Guid? messageId = null)
    {
        var sessions = fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var sender = GrainId.Create("expiry-test-sender", "stable");
        var built = new DurableEnvelopeBuilder(sessions, sender)
            .To(receiver.GetGrainId(), "messages/expiry")
            .WithBody(message)
            .Build();
        if (messageId is { } id)
        {
            built = new DurableEnvelope
            {
                MessageId = id,
                SenderId = built.SenderId,
                ReceiverId = built.ReceiverId,
                RouteKey = built.RouteKey,
                CorrelationKey = built.CorrelationKey,
                ReplyTo = built.ReplyTo,
                Data = built.Data,
                CreatedAt = built.CreatedAt,
            };
        }

        return new EnvelopeLease(built);
    }

    private sealed class EnvelopeLease(DurableEnvelope value) : IDisposable
    {
        public DurableEnvelope Value { get; } = value;
        public void Dispose()
        {
        }
    }
}
