using Microsoft.Extensions.DependencyInjection;
using Orleans.Messaging.Tests.Support;
using Orleans.Runtime;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.Messaging.Tests.Functional;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class InboxCapacityCollection : ICollectionFixture<InboxCapacityClusterFixture>
{
    public const string Name = "Messaging inbox capacity";
}

[Collection(InboxCapacityCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class InboxCapacityBehaviorTests(InboxCapacityClusterFixture fixture)
{
    [Fact]
    public async Task InboxAtCapacity_BackpressuresWithoutPersistenceAndRecoversWhenCapacityFrees()
    {
        var receiver = fixture.Client.GetGrain<IMessagingTestGrain>(Guid.NewGuid());
        var sessions = fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var sender = GrainId.Create("capacity-test-sender", Guid.NewGuid().ToString("N"));
        var poison = TestApplicationProtocol.CreateInbox(sessions, sender, "messages/capacity", new TestMessage(TestApplicationProtocol.NewMessageId(), 31, "poison", ThrowDuringPreparation: true), fixture.JobNow);
        var rejected = TestApplicationProtocol.CreateInbox(sessions, sender, "messages/capacity", new TestMessage(TestApplicationProtocol.NewMessageId(), 32, "accepted-after-capacity"), fixture.JobNow);
        const string processedInstrument = "orleans-messaging-inbox-messages-processed";
        var firstRetryAcknowledged = fixture.Metrics.WaitForCountAsync(processedInstrument, 1, "retry");
        _ = await receiver.GetSnapshotAsync();
        var failed = fixture.GetGrainContext(receiver);

        // The poison's attempt fails its activation, and recovery charges it; the message keeps the only slot.
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, poison)).Status);
        await failed.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(DeliveryStatus.Backpressured, (await DeliverAsync(receiver, rejected)).Status);
        var full = await receiver.GetSnapshotAsync();
        Assert.Equal(1, full.InboxCount);
        Assert.Empty(full.Effects);

        await firstRetryAcknowledged;
        Assert.Equal(1, fixture.Metrics.GetCount(processedInstrument, "retry"));
        var inbox = fixture.GetInbox(receiver);
        var state = await fixture.OnGrainAsync(receiver, () => InboxTestAccess.GetMessageState(inbox, poison.Envelope.MessageId));
        Assert.Equal(1, state.AttemptCount);
        var nextAttempt = Assert.IsType<DateTimeOffset>(state.NextAttemptAt);
        Assert.Equal(fixture.Clock.GetUtcNow() + TimeSpan.FromHours(1), nextAttempt);

        // The retry runs after its window closes and fails again; the recovery which charges it dead-letters the message.
        fixture.Clock.Advance(TimeSpan.FromHours(2));
        failed = fixture.GetGrainContext(receiver);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, poison)).Status);
        await failed.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(DeliveryStatus.Expired, (await DeliverAsync(receiver, rejected)).Status);
        var deadLettered = await receiver.GetSnapshotAsync();
        Assert.Equal(0, deadLettered.InboxCount);
        Assert.Equal(2, Assert.Single(deadLettered.InboxDeadLetters).AttemptCount);
        rejected = rejected with { SentAt = fixture.JobNow };
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, rejected)).Status);
        var recovered = await fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal("accepted-after-capacity", Assert.Single(recovered.Effects).Value);
        Assert.Equal(2, Assert.Single(recovered.InboxDeadLetters).AttemptCount);
    }

    private static async Task<DeliveryResult> DeliverAsync(
        IMessagingTestGrain receiver,
        InboxMessage envelope) =>
        await receiver.AsReference<IMessagingExtension>().DeliverAsync(envelope);
}
