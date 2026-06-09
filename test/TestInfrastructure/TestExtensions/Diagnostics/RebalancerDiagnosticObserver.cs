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
    private readonly object _waitersLock = new();
    private readonly List<IWaiter> _waiters = [];
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
    public Task WaitForCycleCountAsync(int expectedCount, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(60);
        return WaitUntilAsync(
            () => GetCycleCount() >= expectedCount,
            effectiveTimeout,
            () => $"Timed out waiting for {expectedCount} rebalancing cycles. Current count: {GetCycleCount()} after {effectiveTimeout}");
    }

    /// <summary>
    /// Waits for a specific number of rebalancing cycles to complete on a specific silo.
    /// </summary>
    /// <param name="siloAddress">The silo address to filter by.</param>
    /// <param name="expectedCount">The minimum number of cycles to wait for.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 60 seconds.</param>
    /// <returns>A task that completes when the expected count is reached.</returns>
    /// <exception cref="TimeoutException">Thrown if the expected count is not reached within the timeout.</exception>
    public Task WaitForCycleCountAsync(SiloAddress siloAddress, int expectedCount, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(60);
        return WaitUntilAsync(
            () => GetCycleCount(siloAddress) >= expectedCount,
            effectiveTimeout,
            () => $"Timed out waiting for {expectedCount} rebalancing cycles on silo {siloAddress}. Current count: {GetCycleCount(siloAddress)} after {effectiveTimeout}");
    }

    /// <summary>
    /// Waits for a rebalancing cycle to complete.
    /// </summary>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <returns>The cycle stop event payload.</returns>
    public Task<ActivationRebalancerEvents.CycleStop> WaitForCycleAsync(TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        return WaitForEventAsync<ActivationRebalancerEvents.CycleStop>(
            static _ => true,
            effectiveTimeout,
            () => $"Timed out waiting for rebalancing cycle after {effectiveTimeout}");
    }

    /// <summary>
    /// Waits for a rebalancing session to start.
    /// </summary>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <returns>The session start event payload.</returns>
    public Task<ActivationRebalancerEvents.SessionStart> WaitForSessionStartAsync(TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        return WaitForSessionStartAsync(static _ => true, effectiveTimeout);
    }

    /// <summary>
    /// Waits for a rebalancing session matching the specified predicate to start.
    /// </summary>
    /// <param name="predicate">The predicate used to match session start events.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <returns>The session start event payload.</returns>
    public Task<ActivationRebalancerEvents.SessionStart> WaitForSessionStartAsync(
        Func<ActivationRebalancerEvents.SessionStart, bool> predicate,
        TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        return WaitForEventAsync(
            predicate,
            effectiveTimeout,
            () => $"Timed out waiting for rebalancing session start after {effectiveTimeout}");
    }

    /// <summary>
    /// Waits for a rebalancing session to complete.
    /// </summary>
    /// <param name="timeout">Maximum time to wait. Defaults to 60 seconds.</param>
    /// <returns>The session stop event payload.</returns>
    public Task<ActivationRebalancerEvents.SessionStop> WaitForSessionStopAsync(TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(60);
        return WaitForSessionStopAsync(static _ => true, effectiveTimeout);
    }

    /// <summary>
    /// Waits for a rebalancing session matching the specified predicate to complete.
    /// </summary>
    /// <param name="predicate">The predicate used to match session stop events.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 60 seconds.</param>
    /// <returns>The session stop event payload.</returns>
    public Task<ActivationRebalancerEvents.SessionStop> WaitForSessionStopAsync(
        Func<ActivationRebalancerEvents.SessionStop, bool> predicate,
        TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(60);
        return WaitForEventAsync(
            predicate,
            effectiveTimeout,
            () => $"Timed out waiting for rebalancing session stop after {effectiveTimeout}");
    }

    /// <summary>
    /// Waits for a specific number of session stop events.
    /// </summary>
    /// <param name="expectedCount">The minimum number of session stops to wait for.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 60 seconds.</param>
    /// <returns>A task that completes when the expected count is reached.</returns>
    /// <exception cref="TimeoutException">Thrown if the expected count is not reached within the timeout.</exception>
    public Task WaitForSessionStopCountAsync(int expectedCount, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(60);
        return WaitUntilAsync(
            () => _sessionStopEvents.Count >= expectedCount,
            effectiveTimeout,
            () => $"Timed out waiting for {expectedCount} session stops. Current count: {_sessionStopEvents.Count} after {effectiveTimeout}");
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

    private Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout, Func<string> timeoutMessage)
    {
        lock (_waitersLock)
        {
            if (predicate())
            {
                return Task.CompletedTask;
            }

            var waiter = new ConditionWaiter(predicate);
            _waiters.Add(waiter);
            return WaitWithTimeoutAsync(waiter, timeout, timeoutMessage);
        }
    }

    private Task<TEvent> WaitForEventAsync<TEvent>(
        Func<TEvent, bool> predicate,
        TimeSpan timeout,
        Func<string> timeoutMessage)
        where TEvent : ActivationRebalancerEvents.RebalancerEvent
    {
        lock (_waitersLock)
        {
            var waiter = new EventWaiter<TEvent>(predicate);
            _waiters.Add(waiter);
            return WaitWithTimeoutAsync(waiter, timeout, timeoutMessage);
        }
    }

    private async Task WaitWithTimeoutAsync(ConditionWaiter waiter, TimeSpan timeout, Func<string> timeoutMessage)
    {
        try
        {
            await waiter.Task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            RemoveWaiter(waiter);
            throw new TimeoutException(timeoutMessage());
        }
    }

    private async Task<TEvent> WaitWithTimeoutAsync<TEvent>(
        EventWaiter<TEvent> waiter,
        TimeSpan timeout,
        Func<string> timeoutMessage)
        where TEvent : ActivationRebalancerEvents.RebalancerEvent
    {
        try
        {
            return await waiter.Task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            RemoveWaiter(waiter);
            throw new TimeoutException(timeoutMessage());
        }
    }

    private void RemoveWaiter(IWaiter waiter)
    {
        lock (_waitersLock)
        {
            _waiters.Remove(waiter);
        }
    }

    private void SignalWaiters(ActivationRebalancerEvents.RebalancerEvent value)
    {
        for (var i = _waiters.Count - 1; i >= 0; i--)
        {
            if (_waiters[i].TryComplete(value))
            {
                _waiters.RemoveAt(i);
            }
        }
    }

    void IObserver<ActivationRebalancerEvents.RebalancerEvent>.OnNext(ActivationRebalancerEvents.RebalancerEvent value)
    {
        lock (_waitersLock)
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

            SignalWaiters(value);
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

    private interface IWaiter
    {
        bool TryComplete(ActivationRebalancerEvents.RebalancerEvent value);
    }

    private sealed class ConditionWaiter(Func<bool> predicate) : IWaiter
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Task => _completion.Task;

        public bool TryComplete(ActivationRebalancerEvents.RebalancerEvent value)
        {
            if (!predicate())
            {
                return false;
            }

            return _completion.TrySetResult();
        }
    }

    private sealed class EventWaiter<TEvent>(Func<TEvent, bool> predicate) : IWaiter
        where TEvent : ActivationRebalancerEvents.RebalancerEvent
    {
        private readonly TaskCompletionSource<TEvent> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<TEvent> Task => _completion.Task;

        public bool TryComplete(ActivationRebalancerEvents.RebalancerEvent value)
        {
            if (value is not TEvent evt || !predicate(evt))
            {
                return false;
            }

            return _completion.TrySetResult(evt);
        }
    }
}
