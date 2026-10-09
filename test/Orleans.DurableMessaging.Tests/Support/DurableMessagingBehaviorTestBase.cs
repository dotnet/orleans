using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableJobs;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Support;

public abstract class DurableMessagingBehaviorTestBase : IAsyncLifetime
{
    protected DurableMessagingBehaviorTestBase()
        : this(new DurableMessagingClusterFixture())
    {
    }

    protected DurableMessagingBehaviorTestBase(DurableMessagingClusterFixture fixture) => Fixture = fixture;

    protected DurableMessagingClusterFixture Fixture { get; }

    public ValueTask InitializeAsync() => Fixture.InitializeAsync();
    public ValueTask DisposeAsync() => Fixture.DisposeAsync();

    protected IDurableMessagingTestGrain NewGrain() =>
        Fixture.Client.GetGrain<IDurableMessagingTestGrain>(Guid.NewGuid());

    protected static DurableTestMessage NewMessage(int sequence, string value) =>
        new(Guid.NewGuid(), sequence, value);

    protected async Task RefreshSeededOwnerAsync(IDurableMessagingTestGrain receiver)
    {
        var previous = Fixture.GetGrainContext(receiver);
        await receiver.RequestDeactivationAsync();
        await previous.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        _ = await receiver.GetSnapshotAsync();
        Assert.NotSame(previous, Fixture.GetGrainContext(receiver));
    }

    protected static Task<DeliveryResult> DeliverAsync(
        IDurableMessagingTestGrain receiver,
        DurableEnvelope envelope) =>
        DeliverWithCancellationAsync(receiver, envelope, TestContext.Current.CancellationToken);

    protected static async Task<DeliveryResult> DeliverWithCancellationAsync(
        IDurableMessagingTestGrain receiver,
        DurableEnvelope envelope,
        CancellationToken cancellationToken) =>
        await receiver.AsReference<IDurableInboxExtension>().DeliverAsync(envelope, cancellationToken);

    protected static async Task WaitForBarrierAsync(
        IDurableMessagingTestGrain receiver,
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

    protected EnvelopeLease CreateEnvelope(
        IDurableMessagingTestGrain receiver,
        DurableTestMessage message,
        string route = "messages/record") =>
        CreateEnvelope(receiver, (object)message, route);

    protected EnvelopeLease CreateEnvelope(
        IDurableMessagingTestGrain receiver,
        object body,
        string route)
    {
        var sessions = Fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var sender = GrainId.Create("external-test-sender", Guid.NewGuid().ToString("N"));
        return new EnvelopeLease(TestApplicationProtocol.Create(sessions, sender, receiver.GetGrainId(), route, body));
    }

    protected sealed class EnvelopeLease(DurableEnvelope value) : IDisposable
    {
        public DurableEnvelope Value { get; } = value;
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Value.Dispose();
        }
    }
}
