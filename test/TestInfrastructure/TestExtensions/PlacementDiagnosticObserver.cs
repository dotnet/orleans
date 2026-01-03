#nullable enable
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Orleans.Diagnostics;
using Orleans.Runtime;

namespace TestExtensions;

/// <summary>
/// A test helper that subscribes to Orleans placement diagnostic events and provides
/// methods to wait for statistics propagation deterministically.
/// </summary>
/// <remarks>
/// This enables deterministic testing of placement strategies by allowing tests to wait
/// for statistics propagation to complete across all silos before making assertions.
/// </remarks>
public sealed class PlacementDiagnosticObserver : IDisposable, IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
{
    private readonly ConcurrentBag<SiloStatisticsPublishedEvent> _publishedEvents = new();
    private readonly ConcurrentBag<SiloStatisticsReceivedEvent> _receivedEvents = new();
    private readonly ConcurrentBag<ClusterStatisticsRefreshedEvent> _clusterRefreshedEvents = new();
    private readonly ConcurrentBag<SiloStatisticsRemovedEvent> _removedEvents = new();
    private readonly List<IDisposable> _subscriptions = new();
    private IDisposable? _listenerSubscription;
    private int _placementListenerSubscriptionCount;
    private readonly ILogger? _logger;

    /// <summary>
    /// Gets the count of placement listener subscriptions. Useful for debugging.
    /// </summary>
    public int PlacementListenerSubscriptionCount => _placementListenerSubscriptionCount;

    /// <summary>
    /// Gets all captured statistics published events.
    /// </summary>
    public IReadOnlyCollection<SiloStatisticsPublishedEvent> PublishedEvents => _publishedEvents.ToArray();

    /// <summary>
    /// Gets all captured statistics received events.
    /// </summary>
    public IReadOnlyCollection<SiloStatisticsReceivedEvent> ReceivedEvents => _receivedEvents.ToArray();

    /// <summary>
    /// Gets all captured cluster statistics refreshed events.
    /// </summary>
    public IReadOnlyCollection<ClusterStatisticsRefreshedEvent> ClusterRefreshedEvents => _clusterRefreshedEvents.ToArray();

    /// <summary>
    /// Gets all captured statistics removed events.
    /// </summary>
    public IReadOnlyCollection<SiloStatisticsRemovedEvent> RemovedEvents => _removedEvents.ToArray();

    /// <summary>
    /// Creates a new instance of the observer and starts listening for placement diagnostic events.
    /// </summary>
    /// <param name="logger">Optional logger for debug output.</param>
    public static PlacementDiagnosticObserver Create(ILogger? logger = null)
    {
        var observer = new PlacementDiagnosticObserver(logger);
        observer._listenerSubscription = DiagnosticListener.AllListeners.Subscribe(observer);
        return observer;
    }

    private PlacementDiagnosticObserver(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Waits for statistics to be published from a specific silo.
    /// </summary>
    /// <param name="siloAddress">The silo address to wait for.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <returns>The published event payload.</returns>
    public async Task<SiloStatisticsPublishedEvent> WaitForStatisticsPublishedAsync(SiloAddress siloAddress, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource(effectiveTimeout);

        // Check if we already have a matching event
        var existingMatch = _publishedEvents.FirstOrDefault(e => e.SiloAddress == siloAddress);
        if (existingMatch != null)
        {
            return existingMatch;
        }

        // Poll for new events
        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(10, cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var match = _publishedEvents.FirstOrDefault(e => e.SiloAddress == siloAddress);
            if (match != null)
            {
                return match;
            }
        }

        throw new TimeoutException($"Timed out waiting for statistics published from silo {siloAddress} after {effectiveTimeout}");
    }

    /// <summary>
    /// Waits for a cluster statistics refresh to complete on a specific silo.
    /// </summary>
    /// <param name="siloAddress">The silo address that should complete the refresh.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <returns>The cluster refreshed event payload.</returns>
    public async Task<ClusterStatisticsRefreshedEvent> WaitForClusterStatisticsRefreshedAsync(SiloAddress siloAddress, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource(effectiveTimeout);

        // Check if we already have a matching event
        var existingMatch = _clusterRefreshedEvents.FirstOrDefault(e => e.SiloAddress == siloAddress);
        if (existingMatch != null)
        {
            return existingMatch;
        }

        // Poll for new events
        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(10, cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var match = _clusterRefreshedEvents.FirstOrDefault(e => e.SiloAddress == siloAddress);
            if (match != null)
            {
                return match;
            }
        }

        throw new TimeoutException($"Timed out waiting for cluster statistics refresh on silo {siloAddress} after {effectiveTimeout}");
    }

    /// <summary>
    /// Waits for cluster statistics to be refreshed on all silos in the cluster.
    /// This is useful for ensuring statistics have propagated before making placement assertions.
    /// </summary>
    /// <param name="siloAddresses">The addresses of all silos that should refresh.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <returns>A task that completes when all silos have refreshed their statistics.</returns>
    public async Task WaitForAllSilosRefreshedAsync(IEnumerable<SiloAddress> siloAddresses, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        var siloSet = siloAddresses.ToHashSet();
        using var cts = new CancellationTokenSource(effectiveTimeout);

        while (!cts.Token.IsCancellationRequested)
        {
            var refreshedSilos = _clusterRefreshedEvents.Select(e => e.SiloAddress).ToHashSet();
            if (siloSet.All(s => refreshedSilos.Contains(s)))
            {
                return;
            }

            try
            {
                await Task.Delay(10, cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        var refreshedSilosFinal = _clusterRefreshedEvents.Select(e => e.SiloAddress).ToHashSet();
        var missingSilos = siloSet.Where(s => !refreshedSilosFinal.Contains(s)).ToList();
        throw new TimeoutException($"Timed out waiting for cluster statistics refresh on silos: {string.Join(", ", missingSilos)} after {effectiveTimeout}");
    }

    /// <summary>
    /// Waits for statistics to be received from a specific silo by another silo.
    /// </summary>
    /// <param name="fromSilo">The silo that sent the statistics.</param>
    /// <param name="receiverSilo">The silo that should receive the statistics.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <returns>The received event payload.</returns>
    public async Task<SiloStatisticsReceivedEvent> WaitForStatisticsReceivedAsync(SiloAddress fromSilo, SiloAddress receiverSilo, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource(effectiveTimeout);

        // Check if we already have a matching event
        var existingMatch = _receivedEvents.FirstOrDefault(e => e.FromSilo == fromSilo && e.ReceiverSilo == receiverSilo);
        if (existingMatch != null)
        {
            return existingMatch;
        }

        // Poll for new events
        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(10, cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var match = _receivedEvents.FirstOrDefault(e => e.FromSilo == fromSilo && e.ReceiverSilo == receiverSilo);
            if (match != null)
            {
                return match;
            }
        }

        throw new TimeoutException($"Timed out waiting for statistics from {fromSilo} to be received by {receiverSilo} after {effectiveTimeout}");
    }

    /// <summary>
    /// Waits for a specific number of statistics published events.
    /// </summary>
    /// <param name="expectedCount">The minimum number of events to wait for.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <returns>A task that completes when the expected count is reached.</returns>
    public async Task WaitForPublishedCountAsync(int expectedCount, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource(effectiveTimeout);

        while (!cts.Token.IsCancellationRequested)
        {
            if (_publishedEvents.Count >= expectedCount)
            {
                return;
            }

            try
            {
                await Task.Delay(10, cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (_publishedEvents.Count >= expectedCount)
        {
            return;
        }

        throw new TimeoutException($"Timed out waiting for {expectedCount} statistics published events. Current count: {_publishedEvents.Count} after {effectiveTimeout}");
    }

    /// <summary>
    /// Waits for a specific number of cluster statistics refreshed events.
    /// </summary>
    /// <param name="expectedCount">The minimum number of events to wait for.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <returns>A task that completes when the expected count is reached.</returns>
    public async Task WaitForClusterRefreshedCountAsync(int expectedCount, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource(effectiveTimeout);

        while (!cts.Token.IsCancellationRequested)
        {
            if (_clusterRefreshedEvents.Count >= expectedCount)
            {
                return;
            }

            try
            {
                await Task.Delay(10, cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (_clusterRefreshedEvents.Count >= expectedCount)
        {
            return;
        }

        throw new TimeoutException($"Timed out waiting for {expectedCount} cluster statistics refreshed events. Current count: {_clusterRefreshedEvents.Count} after {effectiveTimeout}");
    }

    /// <summary>
    /// Gets the count of cluster refreshed events for a specific silo.
    /// </summary>
    /// <param name="siloAddress">The silo address to filter by.</param>
    /// <returns>The count of refresh events.</returns>
    public int GetClusterRefreshCount(SiloAddress siloAddress)
    {
        return _clusterRefreshedEvents.Count(e => e.SiloAddress == siloAddress);
    }

    /// <summary>
    /// Clears all captured events. Useful for resetting state between test phases.
    /// </summary>
    public void Clear()
    {
        _publishedEvents.Clear();
        _receivedEvents.Clear();
        _clusterRefreshedEvents.Clear();
        _removedEvents.Clear();
    }

    void IObserver<DiagnosticListener>.OnNext(DiagnosticListener listener)
    {
        if (listener.Name == OrleansPlacementDiagnostics.ListenerName)
        {
            _logger?.LogDebug("PlacementDiagnosticObserver: Subscribed to listener '{ListenerName}'", listener.Name);
            Interlocked.Increment(ref _placementListenerSubscriptionCount);
            var subscription = listener.Subscribe(this);
            _subscriptions.Add(subscription);
        }
    }

    void IObserver<KeyValuePair<string, object?>>.OnNext(KeyValuePair<string, object?> kvp)
    {
        switch (kvp.Key)
        {
            case OrleansPlacementDiagnostics.EventNames.StatisticsPublished when kvp.Value is SiloStatisticsPublishedEvent published:
                _logger?.LogDebug("PlacementDiagnosticObserver: StatisticsPublished from {SiloAddress}", published.SiloAddress);
                _publishedEvents.Add(published);
                break;

            case OrleansPlacementDiagnostics.EventNames.StatisticsReceived when kvp.Value is SiloStatisticsReceivedEvent received:
                _logger?.LogDebug("PlacementDiagnosticObserver: StatisticsReceived from {FromSilo} by {ReceiverSilo}", received.FromSilo, received.ReceiverSilo);
                _receivedEvents.Add(received);
                break;

            case OrleansPlacementDiagnostics.EventNames.ClusterStatisticsRefreshed when kvp.Value is ClusterStatisticsRefreshedEvent clusterRefreshed:
                _logger?.LogDebug("PlacementDiagnosticObserver: ClusterStatisticsRefreshed on {SiloAddress}, SiloCount={SiloCount}", clusterRefreshed.SiloAddress, clusterRefreshed.SiloCount);
                _clusterRefreshedEvents.Add(clusterRefreshed);
                break;

            case OrleansPlacementDiagnostics.EventNames.StatisticsRemoved when kvp.Value is SiloStatisticsRemovedEvent removed:
                _logger?.LogDebug("PlacementDiagnosticObserver: StatisticsRemoved for {RemovedSilo} on {ObserverSilo}", removed.RemovedSilo, removed.ObserverSilo);
                _removedEvents.Add(removed);
                break;
        }
    }

    void IObserver<DiagnosticListener>.OnError(Exception error) { }
    void IObserver<DiagnosticListener>.OnCompleted() { }
    void IObserver<KeyValuePair<string, object?>>.OnError(Exception error) { }
    void IObserver<KeyValuePair<string, object?>>.OnCompleted() { }

    public void Dispose()
    {
        _listenerSubscription?.Dispose();
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }
        _subscriptions.Clear();
    }
}
