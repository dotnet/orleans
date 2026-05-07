#nullable enable
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Orleans.Runtime.Diagnostics;

namespace TestExtensions;

/// <summary>
/// A test helper that subscribes to Orleans placement diagnostic events and provides
/// methods to wait for statistics propagation deterministically.
/// </summary>
/// <remarks>
/// This enables deterministic testing of placement strategies by allowing tests to wait
/// for statistics propagation to complete across all silos before making assertions.
/// </remarks>
public sealed class PlacementDiagnosticObserver : IDisposable, IObserver<DeploymentLoadPublisherEvents.DeploymentLoadPublisherEvent>
{
    private readonly ConcurrentBag<DeploymentLoadPublisherEvents.Published> _publishedEvents = new();
    private readonly ConcurrentBag<DeploymentLoadPublisherEvents.Received> _receivedEvents = new();
    private readonly ConcurrentBag<DeploymentLoadPublisherEvents.ClusterRefreshed> _clusterRefreshedEvents = new();
    private readonly ConcurrentBag<DeploymentLoadPublisherEvents.Removed> _removedEvents = new();
    private readonly ILogger? _logger;
    private IDisposable? _subscription;
    private int _placementListenerSubscriptionCount;

    /// <summary>
    /// Gets the count of placement listener subscriptions. Useful for debugging.
    /// </summary>
    public int PlacementListenerSubscriptionCount => _placementListenerSubscriptionCount;

    /// <summary>
    /// Gets all captured statistics published events.
    /// </summary>
    public IReadOnlyCollection<DeploymentLoadPublisherEvents.Published> PublishedEvents => _publishedEvents.ToArray();

    /// <summary>
    /// Gets all captured statistics received events.
    /// </summary>
    public IReadOnlyCollection<DeploymentLoadPublisherEvents.Received> ReceivedEvents => _receivedEvents.ToArray();

    /// <summary>
    /// Gets all captured cluster statistics refreshed events.
    /// </summary>
    public IReadOnlyCollection<DeploymentLoadPublisherEvents.ClusterRefreshed> ClusterRefreshedEvents => _clusterRefreshedEvents.ToArray();

    /// <summary>
    /// Gets all captured statistics removed events.
    /// </summary>
    public IReadOnlyCollection<DeploymentLoadPublisherEvents.Removed> RemovedEvents => _removedEvents.ToArray();

    /// <summary>
    /// Creates a new instance of the observer and starts listening for placement diagnostic events.
    /// </summary>
    /// <param name="logger">Optional logger for debug output.</param>
    public static PlacementDiagnosticObserver Create(ILogger? logger = null)
    {
        var observer = new PlacementDiagnosticObserver(logger);
        observer._subscription = DeploymentLoadPublisherEvents.AllEvents.Subscribe(observer);
        observer._placementListenerSubscriptionCount = 1;
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
    public async Task<DeploymentLoadPublisherEvents.Published> WaitForStatisticsPublishedAsync(SiloAddress siloAddress, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource(effectiveTimeout);

        var existingMatch = _publishedEvents.FirstOrDefault(e => e.SiloAddress == siloAddress);
        if (existingMatch is not null)
        {
            return existingMatch;
        }

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
            if (match is not null)
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
    public async Task<DeploymentLoadPublisherEvents.ClusterRefreshed> WaitForClusterStatisticsRefreshedAsync(SiloAddress siloAddress, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource(effectiveTimeout);

        var existingMatch = _clusterRefreshedEvents.FirstOrDefault(e => e.SiloAddress == siloAddress);
        if (existingMatch is not null)
        {
            return existingMatch;
        }

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
            if (match is not null)
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
    public async Task<DeploymentLoadPublisherEvents.Received> WaitForStatisticsReceivedAsync(SiloAddress fromSilo, SiloAddress receiverSilo, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource(effectiveTimeout);

        var existingMatch = _receivedEvents.FirstOrDefault(e => e.FromSilo == fromSilo && e.ReceiverSilo == receiverSilo);
        if (existingMatch is not null)
        {
            return existingMatch;
        }

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
            if (match is not null)
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

    void IObserver<DeploymentLoadPublisherEvents.DeploymentLoadPublisherEvent>.OnNext(DeploymentLoadPublisherEvents.DeploymentLoadPublisherEvent value)
    {
        switch (value)
        {
            case DeploymentLoadPublisherEvents.Published published:
                _logger?.LogDebug("PlacementDiagnosticObserver: StatisticsPublished from {SiloAddress}", published.SiloAddress);
                _publishedEvents.Add(published);
                break;
            case DeploymentLoadPublisherEvents.Received received:
                _logger?.LogDebug("PlacementDiagnosticObserver: StatisticsReceived from {FromSilo} by {ReceiverSilo}", received.FromSilo, received.ReceiverSilo);
                _receivedEvents.Add(received);
                break;
            case DeploymentLoadPublisherEvents.ClusterRefreshed clusterRefreshed:
                _logger?.LogDebug("PlacementDiagnosticObserver: ClusterStatisticsRefreshed on {SiloAddress}, SiloCount={SiloCount}", clusterRefreshed.SiloAddress, clusterRefreshed.Statistics.Count);
                _clusterRefreshedEvents.Add(clusterRefreshed);
                break;
            case DeploymentLoadPublisherEvents.Removed removed:
                _logger?.LogDebug("PlacementDiagnosticObserver: StatisticsRemoved for {RemovedSilo} on {ObserverSilo}", removed.RemovedSilo, removed.ObserverSilo);
                _removedEvents.Add(removed);
                break;
        }
    }

    void IObserver<DeploymentLoadPublisherEvents.DeploymentLoadPublisherEvent>.OnError(Exception error)
    {
    }

    void IObserver<DeploymentLoadPublisherEvents.DeploymentLoadPublisherEvent>.OnCompleted()
    {
    }

    public void Dispose()
    {
        _subscription?.Dispose();
    }
}
