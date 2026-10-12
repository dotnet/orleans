using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableJobs;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.Messaging.Tests.Support;

public abstract class MessagingBehaviorTestBase : IAsyncLifetime
{
    protected MessagingBehaviorTestBase(bool receiverOnly = true)
        : this(new MessagingClusterFixture(receiverOnly))
    {
    }

    protected MessagingBehaviorTestBase(MessagingClusterFixture fixture) => Fixture = fixture;

    protected MessagingClusterFixture Fixture { get; }
    protected SerializerSessionPool Sessions => Fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>();

    public ValueTask InitializeAsync() => Fixture.InitializeAsync();
    public ValueTask DisposeAsync() => Fixture.DisposeAsync();

    protected IMessagingTestGrain NewGrain() =>
        Fixture.Client.GetGrain<IMessagingTestGrain>(Guid.NewGuid());

    protected static TestMessage NewMessage(int sequence, string value) =>
        new(TestApplicationProtocol.NewMessageId(), sequence, value);

    protected async Task ReactivateAsync(IMessagingTestGrain receiver)
    {
        var previous = Fixture.GetGrainContext(receiver);
        await receiver.RequestDeactivationAsync();
        await previous.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        _ = await receiver.GetSnapshotAsync();
        Assert.NotSame(previous, Fixture.GetGrainContext(receiver));
    }

    protected static Task<DeliveryResult> DeliverAsync(
        IMessagingTestGrain receiver,
        InboxMessage envelope) =>
        DeliverWithCancellationAsync(receiver, envelope, TestContext.Current.CancellationToken);

    protected static async Task<DeliveryResult> DeliverWithCancellationAsync(
        IMessagingTestGrain receiver,
        InboxMessage envelope,
        CancellationToken cancellationToken) =>
        await receiver.AsReference<IInboxExtension>().DeliverAsync(envelope, cancellationToken);

    protected static async Task WaitForBarrierAsync(
        IMessagingTestGrain receiver,
        HandlerProbe.Barrier barrier)
    {
        try
        {
            await barrier.WaitUntilEnteredAsync();
        }
        catch (TimeoutException exception)
        {
            var snapshot = await receiver.GetSnapshotAsync();
            throw new TimeoutException(
                $"Handler did not start. Inbox={snapshot.InboxCount}, effects={snapshot.Effects.Count}, maxHandlers={snapshot.MaxConcurrentHandlers}, deadLetters={string.Join(" | ", snapshot.InboxDeadLetters.Select(static item => item.Reason))}.",
                exception);
        }
    }

    protected InboxMessage CreateEnvelope(
        IMessagingTestGrain receiver,
        TestMessage message,
        string route = "messages/record") =>
        CreateEnvelope(receiver, (object)message, route);

    protected InboxMessage CreateEnvelope(
        IMessagingTestGrain receiver,
        object body,
        string route)
    {
        var sessions = Fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var sender = GrainId.Create("external-test-sender", Guid.NewGuid().ToString("N"));
        return TestApplicationProtocol.CreateInbox(sessions, sender, route, body);
    }
}
