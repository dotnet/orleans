#nullable enable
using System.Collections.Concurrent;
using Orleans.Runtime;
using Orleans.Runtime.Diagnostics;

namespace TestExtensions;

/// <summary>
/// A test helper that subscribes to Orleans activation rebalancer diagnostic events and provides
/// methods to wait for rebalancing cycles deterministically.
/// </summary>
/// <remarks>
/// This replaces Task.Delay patterns in rebalancing tests with event-driven waiting,
/// making tests faster and more reliable when waiting for rebalancing cycles.
/// </remarks>
public sealed class RebalancerDiagnosticObserver : IDisposable, IObserver<ActivationRebalancerEvents.RebalancerEvent>
{
    private readonly ConcurrentQueue<ActivationRebalancerEvents.CycleStart> _cycleStartEvents = new();
    private readonly ConcurrentQueue<ActivationRebalancerEvents.CycleStop> _cycleStopEvents = new();
    private readonly ConcurrentQueue<ActivationRebalancerEvents.SessionStart> _sessionStartEvents = new();
    private readonly ConcurrentQueue<ActivationRebalancerEvents.SessionStop> _sessionStopEvents = new();
    private IDisposable? _subscription;

    /// <summary>
    /// Gets all captured cycle start events.
    /// </summary>
    public IReadOnlyCollection<ActivationRebalancerEvents.CycleStart> CycleStartEvents => _cycleStartEvents.ToArray();

    /// <summary>
    /// Gets all captured cycle stop events.
    /// </summary>
    public IReadOnlyCollection<ActivationRebalancerEvents.CycleStop> CycleStopEvents => _cycleStopEvents.ToArray();

    /// <summary>
    /// Gets all captured session start events.
    /// </summary>
    public IReadOnlyCollection<ActivationRebalancerEvents.SessionStart> SessionStartEvents => _sessionStartEvents.ToArray();

    /// <summary>
    /// Gets all captured session stop events.
    /// </summary>
    public IReadOnlyCollection<ActivationRebalancerEvents.SessionStop> SessionStopEvents => _sessionStopEvents.ToArray();

    /// <summary>
    /// Creates a new instance of the observer and starts listening for rebalancer diagnostic events.
    /// </summary>
    public static RebalancerDiagnosticObserver Create()
    {
        var observer = new RebalancerDiagnosticObserver();
        observer._subscription = ActivationRebalancerEvents.AllEvents.Subscribe(observer);
        return observer;
    }

    private RebalancerDiagnosticObserver()
    {
    }

    /// <summary>
    /// Waits for a specific number of rebalancing cycles to complete across the cluster.
    /// </summary>
    /// <param name="expectedCount">The minimum number of cycles to wait for.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 60 seconds.</param>
    /// <returns>A task that completes when the expected count is reached.</returns>
    /// <exception cref="TimeoutException">Thrown if the expected count is not reached within the timeout.</exception>
    public async Task WaitForCycleCountAsync(int expectedCount, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(60);
        using var cts = new CancellationTokenSource(effectiveTimeout);

        while (!cts.Token.IsCancellationRequested)
        {
            var currentCount = GetCycleCount();
            if (currentCount >= expectedCount)
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

        var finalCount = GetCycleCount();
        if (finalCount >= expectedCount)
        {
            return;
        }

        throw new TimeoutException($"Timed out waiting for {expectedCount} rebalancing cycles. Current count: {finalCount} after {effectiveTimeout}");
    }

    /// <summary>
    /// Waits for a specific number of rebalancing cycles to complete on a specific silo.
    /// </summary>
    /// <param name="siloAddress">The silo address to filter by.</param>
    /// <param name="expectedCount">The minimum number of cycles to wait for.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 60 seconds.</param>
    /// <returns>A task that completes when the expected count is reached.</returns>
    /// <exception cref="TimeoutException">Thrown if the expected count is not reached within the timeout.</exception>
    public async Task WaitForCycleCountAsync(SiloAddress siloAddress, int expectedCount, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(60);
        using var cts = new CancellationTokenSource(effectiveTimeout);

        while (!cts.Token.IsCancellationRequested)
        {
            var currentCount = GetCycleCount(siloAddress);
            if (currentCount >= expectedCount)
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

        var finalCount = GetCycleCount(siloAddress);
        if (finalCount >= expectedCount)
        {
            return;
        }

        throw new TimeoutException($"Timed out waiting for {expectedCount} rebalancing cycles on silo {siloAddress}. Current count: {finalCount} after {effectiveTimeout}");
    }

    /// <summary>
    /// Waits for a rebalancing cycle to complete.
    /// </summary>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <returns>The cycle stop event payload.</returns>
    public async Task<ActivationRebalancerEvents.CycleStop> WaitForCycleAsync(TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource(effectiveTimeout);
        var initialCount = _cycleStopEvents.Count;

        while (!cts.Token.IsCancellationRequested)
        {
            var events = _cycleStopEvents.ToArray();
            if (events.Length > initialCount)
            {
                return events[initialCount];
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

        throw new TimeoutException($"Timed out waiting for rebalancing cycle after {effectiveTimeout}");
    }

    /// <summary>
    /// Waits for a rebalancing session to complete.
    /// </summary>
    /// <param name="timeout">Maximum time to wait. Defaults to 60 seconds.</param>
    /// <returns>The session stop event payload.</returns>
    public async Task<ActivationRebalancerEvents.SessionStop> WaitForSessionStopAsync(TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(60);
        using var cts = new CancellationTokenSource(effectiveTimeout);
        var initialCount = _sessionStopEvents.Count;

        while (!cts.Token.IsCancellationRequested)
        {
            var events = _sessionStopEvents.ToArray();
            if (events.Length > initialCount)
            {
                return events[initialCount];
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

        throw new TimeoutException($"Timed out waiting for rebalancing session stop after {effectiveTimeout}");
    }

    /// <summary>
    /// Waits for a specific number of session stop events.
    /// </summary>
    /// <param name="expectedCount">The minimum number of session stops to wait for.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 60 seconds.</param>
    /// <returns>A task that completes when the expected count is reached.</returns>
    /// <exception cref="TimeoutException">Thrown if the expected count is not reached within the timeout.</exception>
    public async Task WaitForSessionStopCountAsync(int expectedCount, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(60);
        using var cts = new CancellationTokenSource(effectiveTimeout);

        while (!cts.Token.IsCancellationRequested)
        {
            var currentCount = _sessionStopEvents.Count;
            if (currentCount >= expectedCount)
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

        var finalCount = _sessionStopEvents.Count;
        if (finalCount >= expectedCount)
        {
            return;
        }

        throw new TimeoutException($"Timed out waiting for {expectedCount} session stops. Current count: {finalCount} after {effectiveTimeout}");
    }

    /// <summary>
    /// Gets the total count of completed rebalancing cycles across all silos.
    /// </summary>
    /// <returns>The count of cycle stop events.</returns>
    public int GetCycleCount()
    {
        return _cycleStopEvents.Count;
    }

    /// <summary>
    /// Gets the count of completed rebalancing cycles for a specific silo.
    /// </summary>
    /// <param name="siloAddress">The silo address to filter by.</param>
    /// <returns>The count of cycle stop events for the silo.</returns>
    public int GetCycleCount(SiloAddress siloAddress)
    {
        return _cycleStopEvents.Count(e => e.SiloAddress.Equals(siloAddress));
    }

    /// <summary>
    /// Gets the total number of activations migrated across all cycles.
    /// </summary>
    /// <returns>The total number of activations migrated.</returns>
    public int GetTotalActivationsMigrated()
    {
        return _cycleStopEvents.Sum(e => e.ActivationsMigrated);
    }

    /// <summary>
    /// Gets the most recent entropy deviation from cycle stop events.
    /// </summary>
    /// <returns>The entropy deviation, or null if no cycles have completed.</returns>
    public double? GetLatestEntropyDeviation()
    {
        var latest = _cycleStopEvents.OrderByDescending(e => e.CycleNumber).FirstOrDefault();
        return latest?.EntropyDeviation;
    }

    /// <summary>
    /// Clears all captured events.
    /// </summary>
    public void Clear()
    {
        _cycleStartEvents.Clear();
        _cycleStopEvents.Clear();
        _sessionStartEvents.Clear();
        _sessionStopEvents.Clear();
    }

    void IObserver<ActivationRebalancerEvents.RebalancerEvent>.OnNext(ActivationRebalancerEvents.RebalancerEvent value)
    {
        switch (value)
        {
            case ActivationRebalancerEvents.CycleStart cycleStart:
                _cycleStartEvents.Enqueue(cycleStart);
                break;
            case ActivationRebalancerEvents.CycleStop cycleStop:
                _cycleStopEvents.Enqueue(cycleStop);
                break;
            case ActivationRebalancerEvents.SessionStart sessionStart:
                _sessionStartEvents.Enqueue(sessionStart);
                break;
            case ActivationRebalancerEvents.SessionStop sessionStop:
                _sessionStopEvents.Enqueue(sessionStop);
                break;
        }
    }

    void IObserver<ActivationRebalancerEvents.RebalancerEvent>.OnError(Exception error)
    {
    }

    void IObserver<ActivationRebalancerEvents.RebalancerEvent>.OnCompleted()
    {
    }

    public void Dispose()
    {
        _subscription?.Dispose();
    }
}
