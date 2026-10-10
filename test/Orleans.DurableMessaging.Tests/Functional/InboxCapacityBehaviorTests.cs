using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableMessaging.Tests.Support;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Runtime.Diagnostics;
using Orleans.Serialization.Session;
using Orleans.TestingHost.Diagnostics;
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
        using var timers = new DiagnosticEventCollector(GrainTimerEvents.ListenerName);

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, poison)).Status);
        var full = await fixture.WaitForInboxCountAsync(receiver, 1);
        Assert.Empty(full.Effects);
        Assert.Equal(DeliveryStatus.Backpressured, (await DeliverAsync(receiver, rejected)).Status);
        Assert.Equal(1, (await receiver.GetSnapshotAsync()).InboxCount);

        await firstRetryAcknowledged;
        Assert.Equal(1, fixture.Metrics.GetCount(processedInstrument, "retry"));
        var originalContext = fixture.GetGrainContext(receiver);
        var timer = Assert.Single(timers.Events.Select(static item => item.Payload).OfType<GrainTimerEvents.Created>(),
            item => ReferenceEquals(item.GrainContext, originalContext)
                && item.Timer.GetType().GenericTypeArguments is [var state]
                && state.DeclaringType == ReceiverTestServices.GetImplementationType("DurableInboxExtension")
                && state.Name == "LocalDrainTimerState").Timer;
        var stopped = await timers.WaitForEventAsync(nameof(GrainTimerEvents.TickStop),
            item => item.Payload is GrainTimerEvents.TickStop stop && ReferenceEquals(stop.Timer, timer),
            TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Null(Assert.IsType<GrainTimerEvents.TickStop>(stopped.Payload).Exception);
        DateTimeOffset nextAttempt = default;
        await OnTurnAsync(originalContext, () =>
        {
            var stateType = ReceiverTestServices.GetImplementationType("InboxMessageState");
            var statesType = typeof(IDurableDictionary<,>).MakeGenericType(typeof(HierarchicalKey), stateType);
            var states = originalContext.ActivationServices.GetRequiredKeyedService(
                statesType, "__orleans.durable-messaging.inbox-message-state");
            var dictionaryType = typeof(IDictionary<,>).MakeGenericType(typeof(HierarchicalKey), stateType);
            var state = dictionaryType.GetProperty("Item")!.GetValue(states, [poison.MessageId])!;
            Assert.Equal(1, stateType.GetProperty("AttemptCount")!.GetValue(state));
            nextAttempt = Assert.IsType<DateTimeOffset>(stateType.GetProperty("NextAttemptAt")!.GetValue(state));
            Assert.Equal(fixture.Clock.GetUtcNow() + TimeSpan.FromHours(1), nextAttempt);
        });
        fixture.Clock.Advance(TimeSpan.FromHours(2));
        Assert.True(nextAttempt <= fixture.Clock.GetUtcNow());
        var previous = fixture.GetGrainContext(receiver);
        await receiver.RequestDeactivationAsync();
        await previous.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        _ = await receiver.GetSnapshotAsync();
        Assert.NotSame(previous, fixture.GetGrainContext(receiver));
        var current = fixture.GetGrainContext(receiver);
        var currentGrain = Assert.IsType<DurableMessagingTestGrain>(current.GrainInstance);
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
                $"nextAttempt={nextAttempt:O}, now={fixture.Clock.GetUtcNow():O}, job={snapshot.InboxJobId}, " +
                $"failureCompleted={currentGrain.DeactivationFailure.Task.IsCompleted}.", exception);
        }

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, rejected)).Status);
        var recovered = await fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal("accepted-after-capacity", Assert.Single(recovered.Effects).Value);
        Assert.Equal(2, Assert.Single(recovered.InboxDeadLetters).AttemptCount);
    }

    private static async Task<DeliveryResult> DeliverAsync(
        IDurableMessagingTestGrain receiver,
        DurableEnvelope envelope) =>
        await receiver.AsReference<IDurableInboxExtension>().DeliverAsync(envelope);

    private static Task OnTurnAsync(IGrainContext context, Action action)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Scheduler.QueueAction(() =>
        {
            try { action(); finished.SetResult(); }
            catch (Exception exception) { finished.SetException(exception); }
        });
        return finished.Task;
    }
}
