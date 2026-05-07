#nullable enable
using System.Collections.Concurrent;
using Orleans.Runtime.Diagnostics;

namespace TestExtensions;

/// <summary>
/// A test helper that subscribes to Orleans timer diagnostic events and provides
/// methods to wait for timer ticks deterministically.
/// </summary>
/// <remarks>
/// This replaces Task.Delay/Thread.Sleep patterns in tests with event-driven waiting,
/// making tests faster and more reliable when waiting for grain timer callbacks.
/// </remarks>
public sealed class TimerDiagnosticObserver : IDisposable, IObserver<GrainTimerEvents.TimerEvent>
{
    private readonly ConcurrentBag<GrainTimerEvents.Created> _createdEvents = new();
    private readonly ConcurrentBag<GrainTimerEvents.TickStart> _tickStartEvents = new();
    private readonly ConcurrentBag<GrainTimerEvents.TickStop> _tickStopEvents = new();
    private readonly ConcurrentBag<GrainTimerEvents.Disposed> _disposedEvents = new();
    private IDisposable? _subscription;

    /// <summary>
    /// Gets all captured timer created events.
    /// </summary>
    public IReadOnlyCollection<GrainTimerEvents.Created> CreatedEvents => _createdEvents.ToArray();

    /// <summary>
    /// Gets all captured timer tick start events.
    /// </summary>
    public IReadOnlyCollection<GrainTimerEvents.TickStart> TickStartEvents => _tickStartEvents.ToArray();

    /// <summary>
    /// Gets all captured timer tick stop events.
    /// </summary>
    public IReadOnlyCollection<GrainTimerEvents.TickStop> TickStopEvents => _tickStopEvents.ToArray();

    /// <summary>
    /// Gets all captured timer disposed events.
    /// </summary>
    public IReadOnlyCollection<GrainTimerEvents.Disposed> DisposedEvents => _disposedEvents.ToArray();

    /// <summary>
    /// Creates a new instance of the observer and starts listening for timer diagnostic events.
    /// </summary>
    public static TimerDiagnosticObserver Create()
    {
        var observer = new TimerDiagnosticObserver();
        observer._subscription = GrainTimerEvents.AllEvents.Subscribe(observer);
        return observer;
    }

    private TimerDiagnosticObserver()
    {
    }

    /// <summary>
    /// Waits for a timer to be created on a specific grain.
    /// </summary>
    /// <param name="grainId">The grain ID to wait for.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <returns>The timer created event payload.</returns>
    public async Task<GrainTimerEvents.Created> WaitForTimerCreatedAsync(GrainId grainId, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource(effectiveTimeout);

        var existingMatch = _createdEvents.FirstOrDefault(e => e.GrainContext.GrainId == grainId);
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

            var match = _createdEvents.FirstOrDefault(e => e.GrainContext.GrainId == grainId);
            if (match is not null)
            {
                return match;
            }
        }

        throw new TimeoutException($"Timed out waiting for timer creation on grain {grainId} after {effectiveTimeout}");
    }

    /// <summary>
    /// Waits for a timer tick to complete on a specific grain.
    /// </summary>
    /// <param name="grainId">The grain ID to wait for.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <returns>The tick stop event payload.</returns>
    public async Task<GrainTimerEvents.TickStop> WaitForTimerTickAsync(GrainId grainId, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource(effectiveTimeout);

        var existingMatch = _tickStopEvents.FirstOrDefault(e => e.GrainContext.GrainId == grainId);
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

            var match = _tickStopEvents.FirstOrDefault(e => e.GrainContext.GrainId == grainId);
            if (match is not null)
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
    public async Task<GrainTimerEvents.TickStop> WaitForTimerTickByGrainTypeAsync(string grainTypeName, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource(effectiveTimeout);

        var existingMatch = _tickStopEvents.FirstOrDefault(e => GetGrainTypeName(e.GrainContext).Contains(grainTypeName, StringComparison.OrdinalIgnoreCase));
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

            var match = _tickStopEvents.FirstOrDefault(e => GetGrainTypeName(e.GrainContext).Contains(grainTypeName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        throw new TimeoutException($"Timed out waiting for timer tick on grain type '{grainTypeName}' after {effectiveTimeout}");
    }

    /// <summary>
    /// Waits for a timer to be disposed on a specific grain.
    /// </summary>
    /// <param name="grainId">The grain ID to wait for.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <returns>The timer disposed event payload.</returns>
    public async Task<GrainTimerEvents.Disposed> WaitForTimerDisposedAsync(GrainId grainId, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource(effectiveTimeout);

        var existingMatch = _disposedEvents.FirstOrDefault(e => e.GrainContext.GrainId == grainId);
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

            var match = _disposedEvents.FirstOrDefault(e => e.GrainContext.GrainId == grainId);
            if (match is not null)
            {
                return match;
            }
        }

        throw new TimeoutException($"Timed out waiting for timer disposal on grain {grainId} after {effectiveTimeout}");
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
        return _tickStopEvents.Count(e => e.GrainContext.GrainId == grainId);
    }

    /// <summary>
    /// Gets the count of timer ticks for a specific grain type.
    /// </summary>
    /// <param name="grainTypeName">The grain type name to filter by (partial match supported).</param>
    /// <returns>The count of timer tick events.</returns>
    public int GetTickCountByGrainType(string grainTypeName)
    {
        return _tickStopEvents.Count(e => GetGrainTypeName(e.GrainContext).Contains(grainTypeName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Checks if a timer has ticked on a specific grain.
    /// </summary>
    /// <param name="grainId">The grain ID to check.</param>
    /// <returns>True if at least one timer tick has occurred.</returns>
    public bool HasTimerTicked(GrainId grainId)
    {
        return _tickStopEvents.Any(e => e.GrainContext.GrainId == grainId);
    }

    /// <summary>
    /// Clears all captured events.
    /// </summary>
    public void Clear()
    {
        _createdEvents.Clear();
        _tickStartEvents.Clear();
        _tickStopEvents.Clear();
        _disposedEvents.Clear();
    }

    void IObserver<GrainTimerEvents.TimerEvent>.OnNext(GrainTimerEvents.TimerEvent value)
    {
        switch (value)
        {
            case GrainTimerEvents.Created created:
                _createdEvents.Add(created);
                break;
            case GrainTimerEvents.TickStart tickStart:
                _tickStartEvents.Add(tickStart);
                break;
            case GrainTimerEvents.TickStop tickStop:
                _tickStopEvents.Add(tickStop);
                break;
            case GrainTimerEvents.Disposed disposed:
                _disposedEvents.Add(disposed);
                break;
        }
    }

    void IObserver<GrainTimerEvents.TimerEvent>.OnError(Exception error)
    {
    }

    void IObserver<GrainTimerEvents.TimerEvent>.OnCompleted()
    {
    }

    public void Dispose()
    {
        _subscription?.Dispose();
    }

    private static string GetGrainTypeName(IGrainContext grainContext) => grainContext.GrainId.Type.ToString()!;
}

/// <summary>
/// Extension methods for working with grain references and timer diagnostic observers.
/// </summary>
public static class TimerDiagnosticExtensions
{
    /// <summary>
    /// Waits for timer creation on a grain.
    /// </summary>
    public static Task<GrainTimerEvents.Created> WaitForTimerCreatedAsync(this TimerDiagnosticObserver observer, IAddressable grain, TimeSpan? timeout = null)
    {
        return observer.WaitForTimerCreatedAsync(grain.GetGrainId(), timeout);
    }

    /// <summary>
    /// Waits for a timer tick on a grain.
    /// </summary>
    public static Task<GrainTimerEvents.TickStop> WaitForTimerTickAsync(this TimerDiagnosticObserver observer, IAddressable grain, TimeSpan? timeout = null)
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

    /// <summary>
    /// Waits for timer disposal on a grain.
    /// </summary>
    public static Task<GrainTimerEvents.Disposed> WaitForTimerDisposedAsync(this TimerDiagnosticObserver observer, IAddressable grain, TimeSpan? timeout = null)
    {
        return observer.WaitForTimerDisposedAsync(grain.GetGrainId(), timeout);
    }
}
