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
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class InboxCapacityBehaviorTests(InboxCapacityClusterFixture fixture)
{
    [Fact]
    public async Task InboxAtCapacity_BackpressuresWithoutPersistenceAndRecoversWhenCapacityFrees()
    {
        var receiver = fixture.Client.GetGrain<IDurableMessagingTestGrain>(Guid.NewGuid());
        var sessions = fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var sender = GrainId.Create("capacity-test-sender", Guid.NewGuid().ToString("N"));
        using var poison = TestApplicationProtocol.Create(sessions, sender, receiver.GetGrainId(), "messages/capacity", new DurableTestMessage(TestApplicationProtocol.NewMessageId(), 31, "poison", ThrowDuringPreparation: true));
        using var rejected = TestApplicationProtocol.Create(sessions, sender, receiver.GetGrainId(), "messages/capacity", new DurableTestMessage(TestApplicationProtocol.NewMessageId(), 32, "accepted-after-capacity"));
        const string processedInstrument = "orleans-durable-messaging-inbox-messages-processed";
        var firstRetryAcknowledged = fixture.Metrics.WaitForCountAsync(processedInstrument, 1, "retry");

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, poison)).Status);
        var full = await fixture.WaitForInboxCountAsync(receiver, 1);
        Assert.Empty(full.Effects);
        Assert.Equal(DeliveryStatus.Backpressured, (await DeliverAsync(receiver, rejected)).Status);
        Assert.Equal(1, (await receiver.GetSnapshotAsync()).InboxCount);

        await firstRetryAcknowledged;
        Assert.Equal(1, fixture.Metrics.GetCount(processedInstrument, "retry"));
        fixture.Clock.Advance(TimeSpan.FromHours(2));
        var previous = fixture.GetGrainContext(receiver);
        await receiver.RequestDeactivationAsync();
        await previous.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        _ = await receiver.GetSnapshotAsync();
        Assert.NotSame(previous, fixture.GetGrainContext(receiver));
        await fixture.WaitForDeadLetterCountAsync(receiver, 1);

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, rejected)).Status);
        var recovered = await fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal("accepted-after-capacity", Assert.Single(recovered.Effects).Value);
        Assert.Equal(2, Assert.Single(recovered.InboxDeadLetters).AttemptCount);
    }

    private static async Task<DeliveryResult> DeliverAsync(
        IDurableMessagingTestGrain receiver,
        DurableEnvelope envelope) =>
        await receiver.AsReference<IDurableInboxExtension>().DeliverAsync(envelope);
}
