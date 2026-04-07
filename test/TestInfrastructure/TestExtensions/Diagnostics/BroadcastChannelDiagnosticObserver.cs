#nullable enable
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using Orleans.BroadcastChannel;
using Orleans.BroadcastChannel.Diagnostics;
using Orleans.Runtime;

namespace TestExtensions;

/// <summary>
/// A test helper that subscribes to Orleans broadcast channel diagnostic events and provides
/// methods to wait for broadcast channel events deterministically.
/// </summary>
/// <remarks>
/// Uses <c>System.Reactive</c> operators with <c>Replay()</c> so that <c>WaitFor*</c> methods
/// match against both past and future events with zero-latency, event-driven waiting.
/// </remarks>
public sealed class BroadcastChannelDiagnosticObserver : IDisposable
{
    private readonly IConnectableObservable<BroadcastChannelEvents.BroadcastChannelEvent> _events;
    private readonly IDisposable _connection;

    /// <summary>
    /// Creates a new instance of the observer and starts listening for broadcast channel diagnostic events.
    /// </summary>
    public static BroadcastChannelDiagnosticObserver Create() => new();

    private BroadcastChannelDiagnosticObserver()
    {
        _events = BroadcastChannelEvents.AllEvents.Replay();
        _connection = _events.Connect();
    }

    /// <summary>
    /// Waits for an item to be published to a specific channel.
    /// </summary>
    public async Task<BroadcastChannelEvents.ItemPublished> WaitForItemPublishedAsync(ChannelId channelId, string? providerName = null, CancellationToken cancellationToken = default)
    {
        return await _events
            .OfType<BroadcastChannelEvents.ItemPublished>()
            .FirstAsync(e => MatchesChannel(e.ChannelId, e.ProviderName, channelId, providerName))
            .ToTask(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for an item to be delivered to a specific channel subscriber.
    /// </summary>
    public async Task<BroadcastChannelEvents.ItemDelivered> WaitForItemDeliveredAsync(ChannelId channelId, string? providerName = null, CancellationToken cancellationToken = default)
    {
        return await _events
            .OfType<BroadcastChannelEvents.ItemDelivered>()
            .FirstAsync(e => MatchesChannel(e.ChannelId, e.ProviderName, channelId, providerName))
            .ToTask(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for a specific number of items to be delivered on a channel.
    /// </summary>
    public async Task WaitForDeliveryCountAsync(ChannelId channelId, int expectedCount, string? providerName = null, CancellationToken cancellationToken = default)
    {
        await _events
            .OfType<BroadcastChannelEvents.ItemDelivered>()
            .Where(e => MatchesChannel(e.ChannelId, e.ProviderName, channelId, providerName))
            .Take(expectedCount)
            .LastOrDefaultAsync()
            .ToTask(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for a specific number of items to be published to a channel.
    /// </summary>
    public async Task WaitForPublishCountAsync(ChannelId channelId, int expectedCount, string? providerName = null, CancellationToken cancellationToken = default)
    {
        await _events
            .OfType<BroadcastChannelEvents.ItemPublished>()
            .Where(e => MatchesChannel(e.ChannelId, e.ProviderName, channelId, providerName))
            .Take(expectedCount)
            .LastOrDefaultAsync()
            .ToTask(cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool MatchesChannel(ChannelId eventChannelId, string eventProviderName, ChannelId channelId, string? providerName)
    {
        return eventChannelId == channelId
            && (providerName is null || eventProviderName == providerName);
    }

    /// <inheritdoc/>
    public void Dispose() => _connection.Dispose();
}
