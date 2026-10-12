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
        var poison = TestApplicationProtocol.CreateInbox(sessions, sender, "messages/capacity", new TestMessage(TestApplicationProtocol.NewMessageId(), 31, "poison", ThrowDuringPreparation: true));
        var rejected = TestApplicationProtocol.CreateInbox(sessions, sender, "messages/capacity", new TestMessage(TestApplicationProtocol.NewMessageId(), 32, "accepted-after-capacity"));
        const string processedInstrument = "orleans-messaging-inbox-messages-processed";
        var firstRetryAcknowledged = fixture.Metrics.WaitForCountAsync(processedInstrument, 1, "retry");

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, poison)).Status);
        var full = await fixture.WaitForInboxCountAsync(receiver, 1);
        Assert.Empty(full.Effects);
        Assert.Equal(DeliveryStatus.Backpressured, (await DeliverAsync(receiver, rejected)).Status);
        Assert.Equal(1, (await receiver.GetSnapshotAsync()).InboxCount);

        await firstRetryAcknowledged;
        Assert.Equal(1, fixture.Metrics.GetCount(processedInstrument, "retry"));
        var inbox = fixture.GetInbox(receiver);
        var state = await fixture.OnGrainAsync(receiver, () => InboxTestAccess.GetMessageState(inbox, poison.Envelope.MessageId));
        Assert.Equal(1, state.AttemptCount);
        var nextAttempt = Assert.IsType<DateTimeOffset>(state.NextAttemptAt);
        Assert.Equal(fixture.Clock.GetUtcNow() + TimeSpan.FromHours(1), nextAttempt);
        fixture.Clock.Advance(TimeSpan.FromHours(2));
        Assert.True(nextAttempt <= fixture.Clock.GetUtcNow());
        var previous = fixture.GetGrainContext(receiver);
        await receiver.RequestDeactivationAsync();
        await previous.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        _ = await receiver.GetSnapshotAsync();
        Assert.NotSame(previous, fixture.GetGrainContext(receiver));
        var current = fixture.GetGrainContext(receiver);
        var currentGrain = Assert.IsType<MessagingTestGrain>(current.GrainInstance);
        try
        {
            await fixture.SnapshotProbe.WaitAsync(receiver.GetGrainId(),
                snapshot => snapshot.ActivationId == currentGrain.GetSnapshotForTest().ActivationId
                    && snapshot.InboxDeadLetters.Count == 1);
        }
        catch (TimeoutException exception)
        {
            var snapshot = await receiver.GetSnapshotAsync();
            throw new TimeoutException(
                $"Recovered retry did not complete. Activation={snapshot.ActivationId}, inbox={snapshot.InboxCount}, " +
                $"processed={snapshot.ProcessedMessageCount}, deadLetters={snapshot.InboxDeadLetters.Count}, " +
                $"nextAttempt={nextAttempt:O}, now={fixture.Clock.GetUtcNow():O}, " +
                $"failureCompleted={currentGrain.DeactivationFailure.Task.IsCompleted}.", exception);
        }

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, rejected)).Status);
        var recovered = await fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal("accepted-after-capacity", Assert.Single(recovered.Effects).Value);
        Assert.Equal(2, Assert.Single(recovered.InboxDeadLetters).AttemptCount);
    }

    private static async Task<DeliveryResult> DeliverAsync(
        IMessagingTestGrain receiver,
        InboxMessage envelope) =>
        await receiver.AsReference<IInboxExtension>().DeliverAsync(envelope);
}
