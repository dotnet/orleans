#nullable enable
using System.Collections.Concurrent;
using System.Diagnostics;
using Orleans;
using Orleans.Diagnostics;
using Orleans.Runtime;

namespace TestExtensions;

/// <summary>
/// A test helper that subscribes to Orleans timer diagnostic events and provides
/// methods to wait for timer ticks deterministically.
/// </summary>
/// <remarks>
/// This replaces Task.Delay/Thread.Sleep patterns in tests with event-driven waiting,
/// making tests faster and more reliable when waiting for grain timer callbacks.
/// </remarks>
public sealed class TimerDiagnosticObserver : IDisposable, IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
{
    private readonly ConcurrentBag<GrainTimerTickStartEvent> _tickStartEvents = new();
    private readonly ConcurrentBag<GrainTimerTickStopEvent> _tickStopEvents = new();
    private readonly ConcurrentBag<GrainTimerDisposedEvent> _disposedEvents = new();
    private readonly List<IDisposable> _subscriptions = new();
    private IDisposable? _listenerSubscription;

    /// <summary>
    /// Gets all captured timer tick start events.
    /// </summary>
    public IReadOnlyCollection<GrainTimerTickStartEvent> TickStartEvents => _tickStartEvents.ToArray();

    /// <summary>
    /// Gets all captured timer tick stop events.
    /// </summary>
    public IReadOnlyCollection<GrainTimerTickStopEvent> TickStopEvents => _tickStopEvents.ToArray();

    /// <summary>
    /// Gets all captured timer disposed events.
    /// </summary>
    public IReadOnlyCollection<GrainTimerDisposedEvent> DisposedEvents => _disposedEvents.ToArray();

    /// <summary>
    /// Creates a new instance of the observer and starts listening for timer diagnostic events.
    /// </summary>
    public static TimerDiagnosticObserver Create()
    {
        var observer = new TimerDiagnosticObserver();
        observer._listenerSubscription = DiagnosticListener.AllListeners.Subscribe(observer);
        return observer;
    }

    private TimerDiagnosticObserver() { }

    /// <summary>
    /// Waits for a timer tick to complete on a specific grain.
    /// </summary>
    /// <param name="grainId">The grain ID to wait for.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <returns>The tick stop event payload.</returns>
    public async Task<GrainTimerTickStopEvent> WaitForTimerTickAsync(GrainId grainId, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource(effectiveTimeout);

        // Check if we already have a matching event
        var existingMatch = _tickStopEvents.FirstOrDefault(e => e.GrainId == grainId);
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

            var match = _tickStopEvents.FirstOrDefault(e => e.GrainId == grainId);
            if (match != null)
            {
                return match;
            }
        }

        throw new TimeoutException($"Timed out waiting for timer tick on grain {grainId} after {effectiveTimeout}");
    }

    /// <summary>
    /// Waits for a timer tick to complete on any grain matching the grain type.
    /// </summary>
    /// <param name="grainTypeName">The grain type name to match (partial match supported).</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <returns>The tick stop event payload.</returns>
    public async Task<GrainTimerTickStopEvent> WaitForTimerTickByGrainTypeAsync(string grainTypeName, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource(effectiveTimeout);

        // Check if we already have a matching event
        var existingMatch = _tickStopEvents.FirstOrDefault(e => e.GrainType.Contains(grainTypeName, StringComparison.OrdinalIgnoreCase));
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

            var match = _tickStopEvents.FirstOrDefault(e => e.GrainType.Contains(grainTypeName, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                return match;
            }
        }

        throw new TimeoutException($"Timed out waiting for timer tick on grain type '{grainTypeName}' after {effectiveTimeout}");
    }

    /// <summary>
    /// Waits for a specific number of timer ticks to complete on a grain.
    /// </summary>
    /// <param name="grainId">The grain ID to wait for.</param>
    /// <param name="expectedCount">The minimum number of ticks to wait for.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 60 seconds.</param>
    /// <returns>A task that completes when the expected count is reached.</returns>
    /// <exception cref="TimeoutException">Thrown if the expected count is not reached within the timeout.</exception>
    public async Task WaitForTickCountAsync(GrainId grainId, int expectedCount, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(60);
        using var cts = new CancellationTokenSource(effectiveTimeout);

        while (!cts.Token.IsCancellationRequested)
        {
            var currentCount = GetTickCount(grainId);
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

        var finalCount = GetTickCount(grainId);
        if (finalCount >= expectedCount)
        {
            return;
        }

        throw new TimeoutException($"Timed out waiting for {expectedCount} timer ticks on grain {grainId}. Current count: {finalCount} after {effectiveTimeout}");
    }

    /// <summary>
    /// Waits for a specific number of timer ticks to complete on any grain of the specified type.
    /// </summary>
    /// <param name="grainTypeName">The grain type name to match (partial match supported).</param>
    /// <param name="expectedCount">The minimum number of ticks to wait for.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 60 seconds.</param>
    /// <returns>A task that completes when the expected count is reached.</returns>
    /// <exception cref="TimeoutException">Thrown if the expected count is not reached within the timeout.</exception>
    public async Task WaitForTickCountByGrainTypeAsync(string grainTypeName, int expectedCount, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(60);
        using var cts = new CancellationTokenSource(effectiveTimeout);

        while (!cts.Token.IsCancellationRequested)
        {
            var currentCount = GetTickCountByGrainType(grainTypeName);
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

        var finalCount = GetTickCountByGrainType(grainTypeName);
        if (finalCount >= expectedCount)
        {
            return;
        }

        throw new TimeoutException($"Timed out waiting for {expectedCount} timer ticks on grain type '{grainTypeName}'. Current count: {finalCount} after {effectiveTimeout}");
    }

    /// <summary>
    /// Gets the count of timer ticks for a specific grain.
    /// </summary>
    /// <param name="grainId">The grain ID to filter by.</param>
    /// <returns>The count of timer tick events.</returns>
    public int GetTickCount(GrainId grainId)
    {
        return _tickStopEvents.Count(e => e.GrainId == grainId);
    }

    /// <summary>
    /// Gets the count of timer ticks for a specific grain type.
    /// </summary>
    /// <param name="grainTypeName">The grain type name to filter by (partial match supported).</param>
    /// <returns>The count of timer tick events.</returns>
    public int GetTickCountByGrainType(string grainTypeName)
    {
        return _tickStopEvents.Count(e => e.GrainType.Contains(grainTypeName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Checks if a timer has ticked on a specific grain.
    /// </summary>
    /// <param name="grainId">The grain ID to check.</param>
    /// <returns>True if at least one timer tick has occurred.</returns>
    public bool HasTimerTicked(GrainId grainId)
    {
        return _tickStopEvents.Any(e => e.GrainId == grainId);
    }

    /// <summary>
    /// Clears all captured events.
    /// </summary>
    public void Clear()
    {
        _tickStartEvents.Clear();
        _tickStopEvents.Clear();
        _disposedEvents.Clear();
    }

    void IObserver<DiagnosticListener>.OnNext(DiagnosticListener listener)
    {
        if (listener.Name == OrleansTimerDiagnostics.ListenerName)
        {
            var subscription = listener.Subscribe(this);
            _subscriptions.Add(subscription);
        }
    }

    void IObserver<KeyValuePair<string, object?>>.OnNext(KeyValuePair<string, object?> kvp)
    {
        switch (kvp.Key)
        {
            case OrleansTimerDiagnostics.EventNames.TickStart when kvp.Value is GrainTimerTickStartEvent tickStart:
                _tickStartEvents.Add(tickStart);
                break;

            case OrleansTimerDiagnostics.EventNames.TickStop when kvp.Value is GrainTimerTickStopEvent tickStop:
                _tickStopEvents.Add(tickStop);
                break;

            case OrleansTimerDiagnostics.EventNames.Disposed when kvp.Value is GrainTimerDisposedEvent disposed:
                _disposedEvents.Add(disposed);
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

/// <summary>
/// Extension methods for working with grain references and timer diagnostic observers.
/// </summary>
public static class TimerDiagnosticExtensions
{
    /// <summary>
    /// Waits for a timer tick on a grain.
    /// </summary>
    public static Task<GrainTimerTickStopEvent> WaitForTimerTickAsync(this TimerDiagnosticObserver observer, IAddressable grain, TimeSpan? timeout = null)
    {
        return observer.WaitForTimerTickAsync(grain.GetGrainId(), timeout);
    }

    /// <summary>
    /// Waits for a specific number of timer ticks on a grain.
    /// </summary>
    public static Task WaitForTickCountAsync(this TimerDiagnosticObserver observer, IAddressable grain, int expectedCount, TimeSpan? timeout = null)
    {
        return observer.WaitForTickCountAsync(grain.GetGrainId(), expectedCount, timeout);
    }
}
