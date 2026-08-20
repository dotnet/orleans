using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableMessaging.Tests.Support;
using Orleans.Runtime;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Functional;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class InboxCapacityCollection : ICollectionFixture<InboxCapacityClusterFixture>
{
    public const string Name = "Durable messaging inbox capacity";
}

[Collection(InboxCapacityCollection.Name)]
[TestCategory("BVT"), TestCategory("Journaling")]
public sealed class InboxCapacityBehaviorTests(InboxCapacityClusterFixture fixture)
{
    [Fact]
    public async Task InboxAtCapacity_BackpressuresWithoutPersistenceAndRecoversWhenCapacityFrees()
    {
        var receiver = fixture.Client.GetGrain<IDurableMessagingTestGrain>(Guid.NewGuid());
        var sessions = fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var sender = GrainId.Create("capacity-test-sender", Guid.NewGuid().ToString("N"));
        var poison = new DurableEnvelopeBuilder(sessions, sender)
            .To(receiver.GetGrainId(), "messages/capacity")
            .WithBody(new DurableTestMessage(Guid.NewGuid(), 31, "poison", ThrowAfterStaging: true))
            .Build();
        var rejected = new DurableEnvelopeBuilder(sessions, sender)
            .To(receiver.GetGrainId(), "messages/capacity")
            .WithBody(new DurableTestMessage(Guid.NewGuid(), 32, "accepted-after-capacity"))
            .Build();

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, poison)).Status);
        var full = await fixture.WaitForInboxCountAsync(receiver, 1);
        Assert.Empty(full.Effects);
        Assert.Equal(DeliveryStatus.Backpressured, (await DeliverAsync(receiver, rejected)).Status);
        Assert.Equal(1, (await receiver.GetSnapshotAsync()).InboxCount);

        fixture.Clock.Advance(TimeSpan.FromHours(2));
        await receiver.RequestDeactivationAsync();
        _ = await receiver.GetSnapshotAsync();
        await fixture.WaitForDeadLetterCountAsync(receiver, 1);

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, rejected)).Status);
        var recovered = await fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal("accepted-after-capacity", Assert.Single(recovered.Effects).Value);
        Assert.Single(recovered.InboxDeadLetters);
    }

    private static async Task<DeliveryResult> DeliverAsync(
        IDurableMessagingTestGrain receiver,
        DurableEnvelope envelope) =>
        await receiver.AsReference<IDurableInboxExtension>().DeliverAsync(envelope);
}
