using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableMessaging.Tests.Support;
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
[TestCategory("BVT"), TestCategory("Journaling")]
public sealed class DedupeExpiryBehaviorTests(DedupeExpiryClusterFixture fixture)
{
    [Fact]
    public async Task DedupeExpiry_AfterCompaction_AllowsASecondTerminalEffect()
    {
        var receiver = fixture.Client.GetGrain<IDurableMessagingTestGrain>(Guid.NewGuid());
        var original = new DurableTestMessage(Guid.NewGuid(), 15, "expires");
        using var first = CreateEnvelope(receiver, original);

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, first.Value)).Status);
        await fixture.WaitForEffectCountAsync(receiver, 1);
        fixture.Clock.Advance(TimeSpan.FromMinutes(11));

        using var compactionTrigger = CreateEnvelope(
            receiver,
            new DurableTestMessage(Guid.NewGuid(), 16, "compact"));
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, compactionTrigger.Value)).Status);
        await fixture.WaitForEffectCountAsync(receiver, 2);

        using var expiredDuplicate = CreateEnvelope(receiver, original, first.Value.MessageId);
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, expiredDuplicate.Value)).Status);
        var state = await fixture.WaitForEffectCountAsync(receiver, 3);

        Assert.Equal(2, state.Effects.Single(effect => effect.LogicalId == original.LogicalId).Count);
        Assert.Equal(1, state.Effects.Single(effect => effect.Sequence == 16).Count);
    }

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
