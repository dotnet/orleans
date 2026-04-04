#nullable enable
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
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
    /// Waits for a message to be delivered on a specific stream.
    /// </summary>
    public async Task<StreamingEvents.MessageDelivered> WaitForMessageDeliveredAsync(StreamId streamId, string? streamProvider = null, CancellationToken cancellationToken = default)
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
    public async Task WaitForDeliveryCountAsync(StreamId streamId, int expectedCount, string? streamProvider = null, CancellationToken cancellationToken = default)
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
    public async Task WaitForItemDeliveryCountAsync(StreamId streamId, int expectedCount, string? streamProvider = null, CancellationToken cancellationToken = default)
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
    /// Waits for a stream to become inactive.
    /// </summary>
    public async Task<StreamingEvents.StreamInactive> WaitForStreamInactiveAsync(StreamId streamId, string? streamProvider = null, CancellationToken cancellationToken = default)
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
    public async Task<StreamingEvents.SubscriptionAdded> WaitForSubscriptionAddedAsync(StreamId streamId, string? streamProvider = null, CancellationToken cancellationToken = default)
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
    public async Task WaitForSubscriptionAddedCountAsync(StreamId streamId, int expectedCount, string? streamProvider = null, CancellationToken cancellationToken = default)
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
    /// Waits for a subscription to be removed on a specific stream.
    /// </summary>
    public async Task<StreamingEvents.SubscriptionRemoved> WaitForSubscriptionRemovedAsync(StreamId streamId, string? streamProvider = null, CancellationToken cancellationToken = default)
    {
        return await _events
            .OfType<StreamingEvents.SubscriptionRemoved>()
            .FirstAsync(e => MatchesStream(e.StreamId, e.StreamProvider, streamId, streamProvider))
            .ToTask(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for a specific number of subscriptions to be removed on a stream.
    /// </summary>
    public async Task WaitForSubscriptionRemovedCountAsync(StreamId streamId, int expectedCount, string? streamProvider = null, CancellationToken cancellationToken = default)
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
            && (streamProvider is null || eventStreamProvider == streamProvider);
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
