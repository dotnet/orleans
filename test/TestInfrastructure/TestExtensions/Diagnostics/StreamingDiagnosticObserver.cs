using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Streams;
using StreamingEvents = Orleans.Streaming.Diagnostics.StreamingEvents;

namespace TestExtensions;

/// <summary>
/// A test helper that subscribes to Orleans streaming diagnostic events and provides
/// methods to wait for streaming events deterministically.
/// </summary>
/// <remarks>
/// Uses <c>System.Reactive</c> operators with <c>Replay()</c> so that <c>WaitFor*</c> methods
/// match against both past and future events with zero-latency, event-driven waiting.
/// </remarks>
public sealed class StreamingDiagnosticObserver : IDisposable
{
    private readonly IConnectableObservable<StreamingEvents.StreamingEvent> _events;
    private readonly IDisposable _connection;

    /// <summary>
    /// Creates a new instance of the observer and starts listening for streaming diagnostic events.
    /// </summary>
    public static StreamingDiagnosticObserver Create() => new();

    private StreamingDiagnosticObserver()
    {
        _events = StreamingEvents.AllEvents.Replay();
        _connection = _events.Connect();
    }

    /// <summary>
    /// Waits for queue ownership to change for a provider.
    /// </summary>
    public async Task<StreamingEvents.BalancerChanged> WaitForBalancerChangedAsync(string? streamProvider, SiloAddress? siloAddress, CancellationToken cancellationToken)
    {
        return await _events
            .OfType<StreamingEvents.BalancerChanged>()
            .FirstAsync(e => MatchesProviderAndSilo(e.StreamProvider, e.SiloAddress, streamProvider, siloAddress))
            .ToTask(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for a pulling agent to start for a queue.
    /// </summary>
    public async Task<StreamingEvents.PullingAgentStarted> WaitForPullingAgentStartedAsync(string? streamProvider, SiloAddress? siloAddress, QueueId? queueId, CancellationToken cancellationToken)
    {
        return await _events
            .OfType<StreamingEvents.PullingAgentStarted>()
            .FirstAsync(e => MatchesProviderSiloAndQueue(e.StreamProvider, e.SiloAddress, e.QueueId, streamProvider, siloAddress, queueId))
            .ToTask(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for a specific number of pulling agent starts for a provider.
    /// </summary>
    public async Task WaitForPullingAgentStartedCountAsync(string? streamProvider, int expectedCount, CancellationToken cancellationToken)
    {
        await _events
            .OfType<StreamingEvents.PullingAgentStarted>()
            .Where(e => MatchesProvider(e.StreamProvider, streamProvider))
            .Take(expectedCount)
            .LastOrDefaultAsync()
            .ToTask(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for a pulling agent to stop for a queue.
    /// </summary>
    public async Task<StreamingEvents.PullingAgentStopped> WaitForPullingAgentStoppedAsync(string? streamProvider, SiloAddress? siloAddress, QueueId? queueId, CancellationToken cancellationToken)
    {
        return await _events
            .OfType<StreamingEvents.PullingAgentStopped>()
            .FirstAsync(e => MatchesProviderSiloAndQueue(e.StreamProvider, e.SiloAddress, e.QueueId, streamProvider, siloAddress, queueId))
            .ToTask(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for a queue receiver to initialize.
    /// </summary>
    public async Task<StreamingEvents.QueueReceiverInitialized> WaitForQueueReceiverInitializedAsync(string? streamProvider, SiloAddress? siloAddress, QueueId? queueId, CancellationToken cancellationToken)
    {
        return await _events
            .OfType<StreamingEvents.QueueReceiverInitialized>()
            .FirstAsync(e => MatchesProviderSiloAndQueue(e.StreamProvider, e.SiloAddress, e.QueueId, streamProvider, siloAddress, queueId))
            .ToTask(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for a specific number of queue receivers to initialize for a provider.
    /// </summary>
    public async Task WaitForQueueReceiverInitializedCountAsync(string? streamProvider, int expectedCount, CancellationToken cancellationToken)
    {
        await _events
            .OfType<StreamingEvents.QueueReceiverInitialized>()
            .Where(e => MatchesProvider(e.StreamProvider, streamProvider))
            .Take(expectedCount)
            .LastOrDefaultAsync()
            .ToTask(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for a queue receiver initialization failure.
    /// </summary>
    public async Task<StreamingEvents.QueueReceiverInitializationFailed> WaitForQueueReceiverInitializationFailedAsync(string? streamProvider, SiloAddress? siloAddress, QueueId? queueId, CancellationToken cancellationToken)
    {
        return await _events
            .OfType<StreamingEvents.QueueReceiverInitializationFailed>()
            .FirstAsync(e => MatchesProviderSiloAndQueue(e.StreamProvider, e.SiloAddress, e.QueueId, streamProvider, siloAddress, queueId))
            .ToTask(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for a pulling agent to register a local stream entry.
    /// </summary>
    public async Task<StreamingEvents.PullingAgentStreamRegistered> WaitForPullingAgentStreamRegisteredAsync(StreamId streamId, string? streamProvider, CancellationToken cancellationToken)
    {
        return await _events
            .OfType<StreamingEvents.PullingAgentStreamRegistered>()
            .FirstAsync(e => MatchesStream(e.StreamId, e.StreamProvider, streamId, streamProvider))
            .ToTask(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for a pulling agent stream registration failure.
    /// </summary>
    public async Task<StreamingEvents.PullingAgentStreamRegistrationFailed> WaitForPullingAgentStreamRegistrationFailedAsync(StreamId streamId, string? streamProvider, CancellationToken cancellationToken)
    {
        return await _events
            .OfType<StreamingEvents.PullingAgentStreamRegistrationFailed>()
            .FirstAsync(e => MatchesStream(e.StreamId, e.StreamProvider, streamId, streamProvider))
            .ToTask(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for a message to be delivered on a specific stream.
    /// </summary>
    public async Task<StreamingEvents.MessageDelivered> WaitForMessageDeliveredAsync(StreamId streamId, string? streamProvider, CancellationToken cancellationToken)
    {
        return await _events
            .OfType<StreamingEvents.MessageDelivered>()
            .FirstAsync(e => MatchesStream(e.StreamId, e.StreamProvider, streamId, streamProvider))
            .ToTask(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for a specific number of messages to be delivered on a stream.
    /// </summary>
    public async Task WaitForDeliveryCountAsync(StreamId streamId, int expectedCount, string? streamProvider, CancellationToken cancellationToken)
    {
        await _events
            .OfType<StreamingEvents.MessageDelivered>()
            .Where(e => MatchesStream(e.StreamId, e.StreamProvider, streamId, streamProvider))
            .Take(expectedCount)
            .LastOrDefaultAsync()
            .ToTask(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for a specific number of individual items to be delivered on a stream.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="WaitForDeliveryCountAsync"/> which counts batch deliveries,
    /// this method counts individual items within batches.
    /// </remarks>
    public async Task WaitForItemDeliveryCountAsync(StreamId streamId, int expectedCount, string? streamProvider, CancellationToken cancellationToken)
    {
        await _events
            .OfType<StreamingEvents.ItemDelivered>()
            .Where(e => MatchesStream(e.StreamId, e.StreamProvider, streamId, streamProvider))
            .Take(expectedCount)
            .LastOrDefaultAsync()
            .ToTask(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for a specific number of individual items to be delivered to a particular subscription.
    /// </summary>
    public async Task WaitForItemDeliveryCountAsync(StreamId streamId, Guid subscriptionId, int expectedCount, string? streamProvider, CancellationToken cancellationToken)
    {
        await _events
            .OfType<StreamingEvents.ItemDelivered>()
            .Where(e => MatchesSubscription(e.StreamId, e.SubscriptionId, e.StreamProvider, streamId, subscriptionId, streamProvider))
            .Take(expectedCount)
            .LastOrDefaultAsync()
            .ToTask(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for a specific number of individual items to be delivered on a stream and then
    /// for that stream to report a cursor-drained transition afterward.
    /// </summary>
    public async Task WaitForItemDeliveryAndCursorDrainAsync(StreamId streamId, int expectedCount, string? streamProvider, CancellationToken cancellationToken)
    {
        await _events
            .Where(e => e switch
            {
                StreamingEvents.ItemDelivered item => MatchesStream(item.StreamId, item.StreamProvider, streamId, streamProvider),
                StreamingEvents.ConsumerCursorDrained drained => MatchesStream(drained.StreamId, drained.StreamProvider, streamId, streamProvider),
                _ => false,
            })
            .Scan((DeliveredCount: 0, Drained: false), (state, evt) => evt switch
            {
                StreamingEvents.ItemDelivered => (state.DeliveredCount + 1, state.Drained),
                StreamingEvents.ConsumerCursorDrained when state.DeliveredCount >= expectedCount => (state.DeliveredCount, true),
                _ => state,
            })
            .FirstAsync(state => state.Drained)
            .ToTask(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for a specific number of individual items to be delivered to a particular subscription and then
    /// for that subscription's cursor-drained transition to be reported.
    /// </summary>
    public async Task WaitForItemDeliveryAndCursorDrainAsync(StreamId streamId, Guid subscriptionId, int expectedCount, string? streamProvider, CancellationToken cancellationToken)
    {
        await _events
            .Where(e => e switch
            {
                StreamingEvents.ItemDelivered item => MatchesSubscription(item.StreamId, item.SubscriptionId, item.StreamProvider, streamId, subscriptionId, streamProvider),
                StreamingEvents.ConsumerCursorDrained drained => MatchesSubscription(drained.StreamId, drained.SubscriptionId, drained.StreamProvider, streamId, subscriptionId, streamProvider),
                _ => false,
            })
            .Scan((DeliveredCount: 0, Drained: false), (state, evt) => evt switch
            {
                StreamingEvents.ItemDelivered => (state.DeliveredCount + 1, state.Drained),
                StreamingEvents.ConsumerCursorDrained when state.DeliveredCount >= expectedCount => (state.DeliveredCount, true),
                _ => state,
            })
            .FirstAsync(state => state.Drained)
            .ToTask(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for a stream to become inactive.
    /// </summary>
    public async Task<StreamingEvents.StreamInactive> WaitForStreamInactiveAsync(StreamId streamId, string? streamProvider, CancellationToken cancellationToken)
    {
        return await _events
            .OfType<StreamingEvents.StreamInactive>()
            .FirstAsync(e => MatchesStream(e.StreamId, e.StreamProvider, streamId, streamProvider))
            .ToTask(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for a subscription to be added on a specific stream.
    /// </summary>
    public async Task<StreamingEvents.SubscriptionAdded> WaitForSubscriptionAddedAsync(StreamId streamId, string? streamProvider, CancellationToken cancellationToken)
    {
        return await _events
            .OfType<StreamingEvents.SubscriptionAdded>()
            .FirstAsync(e => MatchesStream(e.StreamId, e.StreamProvider, streamId, streamProvider))
            .ToTask(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for a specific number of subscriptions to be added on a stream.
    /// </summary>
    public async Task WaitForSubscriptionAddedCountAsync(StreamId streamId, int expectedCount, string? streamProvider, CancellationToken cancellationToken)
    {
        await _events
            .OfType<StreamingEvents.SubscriptionAdded>()
            .Where(e => MatchesStream(e.StreamId, e.StreamProvider, streamId, streamProvider))
            .Take(expectedCount)
            .LastOrDefaultAsync()
            .ToTask(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for a subscription to be durably registered on a specific stream.
    /// </summary>
    public async Task<StreamingEvents.SubscriptionRegistered> WaitForSubscriptionRegisteredAsync(StreamId streamId, string? streamProvider, CancellationToken cancellationToken)
    {
        return await _events
            .OfType<StreamingEvents.SubscriptionRegistered>()
            .FirstAsync(e => MatchesStream(e.StreamId, e.StreamProvider, streamId, streamProvider))
            .ToTask(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for a specific number of subscriptions to be durably registered on a stream.
    /// </summary>
    public async Task WaitForSubscriptionRegisteredCountAsync(StreamId streamId, int expectedCount, string? streamProvider, CancellationToken cancellationToken)
    {
        await _events
            .OfType<StreamingEvents.SubscriptionRegistered>()
            .Where(e => MatchesStream(e.StreamId, e.StreamProvider, streamId, streamProvider))
            .Take(expectedCount)
            .LastOrDefaultAsync()
            .ToTask(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for a subscription to be attached on a specific stream.
    /// </summary>
    public async Task<StreamingEvents.SubscriptionAttached> WaitForSubscriptionAttachedAsync(StreamId streamId, string? streamProvider, CancellationToken cancellationToken)
    {
        return await _events
            .OfType<StreamingEvents.SubscriptionAttached>()
            .FirstAsync(e => MatchesStream(e.StreamId, e.StreamProvider, streamId, streamProvider))
            .ToTask(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for a specific number of subscriptions to be attached on a stream.
    /// </summary>
    public async Task WaitForSubscriptionAttachedCountAsync(StreamId streamId, int expectedCount, string? streamProvider, CancellationToken cancellationToken)
    {
        await _events
            .OfType<StreamingEvents.SubscriptionAttached>()
            .Where(e => MatchesStream(e.StreamId, e.StreamProvider, streamId, streamProvider))
            .Take(expectedCount)
            .LastOrDefaultAsync()
            .ToTask(cancellationToken)
            .ConfigureAwait(false);
    }

     /// <summary>
     /// Waits for a subscription to be removed on a specific stream.
     /// </summary>
    public async Task<StreamingEvents.SubscriptionRemoved> WaitForSubscriptionRemovedAsync(StreamId streamId, string? streamProvider, CancellationToken cancellationToken)
    {
        return await _events
            .OfType<StreamingEvents.SubscriptionRemoved>()
            .FirstAsync(e => MatchesStream(e.StreamId, e.StreamProvider, streamId, streamProvider))
            .ToTask(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for a subscription to be durably removed from a specific stream.
    /// </summary>
    public async Task<StreamingEvents.SubscriptionUnregistered> WaitForSubscriptionUnregisteredAsync(StreamId streamId, string? streamProvider, CancellationToken cancellationToken)
    {
        return await _events
            .OfType<StreamingEvents.SubscriptionUnregistered>()
            .FirstAsync(e => MatchesStream(e.StreamId, e.StreamProvider, streamId, streamProvider))
            .ToTask(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for a specific subscription to be durably removed from a specific stream.
    /// </summary>
    public async Task<StreamingEvents.SubscriptionUnregistered> WaitForSubscriptionUnregisteredAsync(StreamId streamId, Guid subscriptionId, string? streamProvider, CancellationToken cancellationToken)
    {
        return await _events
            .OfType<StreamingEvents.SubscriptionUnregistered>()
            .FirstAsync(e => MatchesSubscription(e.StreamId, e.SubscriptionId, e.StreamProvider, streamId, subscriptionId, streamProvider))
            .ToTask(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for a specific number of subscriptions to be durably removed from a stream.
    /// </summary>
    public async Task WaitForSubscriptionUnregisteredCountAsync(StreamId streamId, int expectedCount, string? streamProvider, CancellationToken cancellationToken)
    {
        await _events
            .OfType<StreamingEvents.SubscriptionUnregistered>()
            .Where(e => MatchesStream(e.StreamId, e.StreamProvider, streamId, streamProvider))
            .Take(expectedCount)
            .LastOrDefaultAsync()
            .ToTask(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for a subscription to be detached from a specific stream.
    /// </summary>
    public async Task<StreamingEvents.SubscriptionDetached> WaitForSubscriptionDetachedAsync(StreamId streamId, string? streamProvider, CancellationToken cancellationToken)
    {
        return await _events
            .OfType<StreamingEvents.SubscriptionDetached>()
            .FirstAsync(e => MatchesStream(e.StreamId, e.StreamProvider, streamId, streamProvider))
            .ToTask(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for a specific number of subscriptions to be detached from a stream.
    /// </summary>
    public async Task WaitForSubscriptionDetachedCountAsync(StreamId streamId, int expectedCount, string? streamProvider, CancellationToken cancellationToken)
    {
        await _events
            .OfType<StreamingEvents.SubscriptionDetached>()
            .Where(e => MatchesStream(e.StreamId, e.StreamProvider, streamId, streamProvider))
            .Take(expectedCount)
            .LastOrDefaultAsync()
            .ToTask(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for a consumer cursor to drain on a specific stream.
    /// </summary>
    public async Task<StreamingEvents.ConsumerCursorDrained> WaitForConsumerCursorDrainedAsync(StreamId streamId, string? streamProvider, CancellationToken cancellationToken)
    {
        return await _events
            .OfType<StreamingEvents.ConsumerCursorDrained>()
            .FirstAsync(e => MatchesStream(e.StreamId, e.StreamProvider, streamId, streamProvider))
            .ToTask(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for a consumer cursor to drain for a specific subscription.
    /// </summary>
    public async Task<StreamingEvents.ConsumerCursorDrained> WaitForConsumerCursorDrainedAsync(StreamId streamId, Guid subscriptionId, string? streamProvider, CancellationToken cancellationToken)
    {
        return await _events
            .OfType<StreamingEvents.ConsumerCursorDrained>()
            .FirstAsync(e => MatchesSubscription(e.StreamId, e.SubscriptionId, e.StreamProvider, streamId, subscriptionId, streamProvider))
            .ToTask(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for a specific number of consumer cursor drained events on a stream.
    /// </summary>
    public async Task WaitForConsumerCursorDrainedCountAsync(StreamId streamId, int expectedCount, string? streamProvider, CancellationToken cancellationToken)
    {
        await _events
            .OfType<StreamingEvents.ConsumerCursorDrained>()
            .Where(e => MatchesStream(e.StreamId, e.StreamProvider, streamId, streamProvider))
            .Take(expectedCount)
            .LastOrDefaultAsync()
            .ToTask(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for a specific number of consumer cursor drained events for a specific subscription.
    /// </summary>
    public async Task WaitForConsumerCursorDrainedCountAsync(StreamId streamId, Guid subscriptionId, int expectedCount, string? streamProvider, CancellationToken cancellationToken)
    {
        await _events
            .OfType<StreamingEvents.ConsumerCursorDrained>()
            .Where(e => MatchesSubscription(e.StreamId, e.SubscriptionId, e.StreamProvider, streamId, subscriptionId, streamProvider))
            .Take(expectedCount)
            .LastOrDefaultAsync()
            .ToTask(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for a specific number of subscriptions to be removed on a stream.
    /// </summary>
    public async Task WaitForSubscriptionRemovedCountAsync(StreamId streamId, int expectedCount, string? streamProvider, CancellationToken cancellationToken)
    {
        await _events
            .OfType<StreamingEvents.SubscriptionRemoved>()
            .Where(e => MatchesStream(e.StreamId, e.StreamProvider, streamId, streamProvider))
            .Take(expectedCount)
            .LastOrDefaultAsync()
            .ToTask(cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool MatchesStream(StreamId eventStreamId, string eventStreamProvider, StreamId streamId, string? streamProvider)
    {
        return eventStreamId == streamId
            && MatchesProvider(eventStreamProvider, streamProvider);
    }

    private static bool MatchesSubscription(StreamId eventStreamId, Guid eventSubscriptionId, string eventStreamProvider, StreamId streamId, Guid subscriptionId, string? streamProvider)
    {
        return eventStreamId == streamId
            && eventSubscriptionId == subscriptionId
            && MatchesProvider(eventStreamProvider, streamProvider);
    }

    private static bool MatchesProviderSiloAndQueue(string eventStreamProvider, SiloAddress? eventSiloAddress, QueueId eventQueueId, string? streamProvider, SiloAddress? siloAddress, QueueId? queueId)
    {
        return MatchesProviderAndSilo(eventStreamProvider, eventSiloAddress, streamProvider, siloAddress)
            && (queueId is null || eventQueueId.Equals(queueId.Value));
    }

    private static bool MatchesProviderAndSilo(string eventStreamProvider, SiloAddress? eventSiloAddress, string? streamProvider, SiloAddress? siloAddress)
    {
        return MatchesProvider(eventStreamProvider, streamProvider)
            && (siloAddress is null || eventSiloAddress?.Equals(siloAddress) == true);
    }

    private static bool MatchesProvider(string eventStreamProvider, string? streamProvider)
    {
        return streamProvider is null || eventStreamProvider == streamProvider;
    }

    /// <inheritdoc/>
    public void Dispose() => _connection.Dispose();
}

/// <summary>
/// Extension methods for working with streaming diagnostic observers.
/// </summary>
public static class StreamingDiagnosticExtensions
{
}
