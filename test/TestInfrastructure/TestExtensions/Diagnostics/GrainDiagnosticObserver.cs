#nullable enable
using System.Collections.Concurrent;
using System.Collections.Generic;
using Orleans.Runtime.Diagnostics;

namespace TestExtensions;

/// <summary>
/// A test helper that subscribes to Orleans grain diagnostic events and provides
/// methods to wait for specific grain lifecycle events deterministically.
/// </summary>
/// <remarks>
/// This replaces Task.Delay/Thread.Sleep patterns in tests with event-driven waiting,
/// making tests faster and more reliable.
/// </remarks>
public sealed class GrainDiagnosticObserver : IDisposable, IObserver<GrainLifecycleEvents.LifecycleEvent>
{
    private readonly ConcurrentDictionary<(GrainId GrainId, string EventName), List<TaskCompletionSource<object?>>> _waiters = new();
    private readonly ConcurrentBag<GrainLifecycleEvents.Created> _createdEvents = new();
    private readonly ConcurrentBag<GrainLifecycleEvents.Activated> _activatedEvents = new();
    private readonly ConcurrentBag<GrainLifecycleEvents.Deactivating> _deactivatingEvents = new();
    private readonly ConcurrentBag<GrainLifecycleEvents.Deactivated> _deactivatedEvents = new();
    private IDisposable? _subscription;
    private int _listenerSubscriptionCount;

    /// <summary>
    /// Gets the number of Orleans.Grains listeners that have been subscribed to.
    /// </summary>
    public int ListenerSubscriptionCount => _listenerSubscriptionCount;

    /// <summary>
    /// Gets all captured grain created events.
    /// </summary>
    public IReadOnlyCollection<GrainLifecycleEvents.Created> CreatedEvents => _createdEvents.ToArray();

    /// <summary>
    /// Gets all captured grain activated events.
    /// </summary>
    public IReadOnlyCollection<GrainLifecycleEvents.Activated> ActivatedEvents => _activatedEvents.ToArray();

    /// <summary>
    /// Gets all captured grain deactivating events.
    /// </summary>
    public IReadOnlyCollection<GrainLifecycleEvents.Deactivating> DeactivatingEvents => _deactivatingEvents.ToArray();

    /// <summary>
    /// Gets all captured grain deactivated events.
    /// </summary>
    public IReadOnlyCollection<GrainLifecycleEvents.Deactivated> DeactivatedEvents => _deactivatedEvents.ToArray();

    /// <summary>
    /// Creates a new instance of the observer and starts listening for grain diagnostic events.
    /// </summary>
    public static GrainDiagnosticObserver Create()
    {
        var observer = new GrainDiagnosticObserver();
        observer._subscription = GrainLifecycleEvents.AllEvents.Subscribe(observer);
        Interlocked.Increment(ref observer._listenerSubscriptionCount);
        return observer;
    }

    private GrainDiagnosticObserver() { }

    /// <summary>
    /// Waits for a specific grain to be deactivated.
    /// </summary>
    /// <param name="grainId">The grain ID to wait for.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <returns>The deactivated event payload.</returns>
    public async Task<GrainLifecycleEvents.Deactivated> WaitForGrainDeactivatedAsync(GrainId grainId, TimeSpan? timeout = null)
    {
        var result = await WaitForEventAsync(grainId, nameof(GrainLifecycleEvents.Deactivated), timeout ?? TimeSpan.FromSeconds(30));
        return (GrainLifecycleEvents.Deactivated)result!;
    }

    /// <summary>
    /// Waits for a specific grain to start deactivating.
    /// </summary>
    /// <param name="grainId">The grain ID to wait for.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <returns>The deactivating event payload.</returns>
    public async Task<GrainLifecycleEvents.Deactivating> WaitForGrainDeactivatingAsync(GrainId grainId, TimeSpan? timeout = null)
    {
        var result = await WaitForEventAsync(grainId, nameof(GrainLifecycleEvents.Deactivating), timeout ?? TimeSpan.FromSeconds(30));
        return (GrainLifecycleEvents.Deactivating)result!;
    }

    /// <summary>
    /// Waits for a specific grain to be activated.
    /// </summary>
    /// <param name="grainId">The grain ID to wait for.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <returns>The activated event payload.</returns>
    public async Task<GrainLifecycleEvents.Activated> WaitForGrainActivatedAsync(GrainId grainId, TimeSpan? timeout = null)
    {
        var result = await WaitForEventAsync(grainId, nameof(GrainLifecycleEvents.Activated), timeout ?? TimeSpan.FromSeconds(30));
        return (GrainLifecycleEvents.Activated)result!;
    }

    /// <summary>
    /// Waits for a specific grain to be created.
    /// </summary>
    /// <param name="grainId">The grain ID to wait for.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <returns>The created event payload.</returns>
    public async Task<GrainLifecycleEvents.Created> WaitForGrainCreatedAsync(GrainId grainId, TimeSpan? timeout = null)
    {
        var result = await WaitForEventAsync(grainId, nameof(GrainLifecycleEvents.Created), timeout ?? TimeSpan.FromSeconds(30));
        return (GrainLifecycleEvents.Created)result!;
    }

    /// <summary>
    /// Waits for any grain matching the predicate to be deactivated.
    /// </summary>
    /// <param name="predicate">A predicate to match the grain.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <returns>The deactivated event payload.</returns>
    public async Task<GrainLifecycleEvents.Deactivated> WaitForAnyGrainDeactivatedAsync(Func<GrainLifecycleEvents.Deactivated, bool> predicate, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource(effectiveTimeout);

        // Check if we already have a matching event
        var existingMatch = _deactivatedEvents.FirstOrDefault(predicate);
        if (existingMatch != null)
        {
            return existingMatch;
        }

        // Poll for new events (this is a fallback; the event-driven approach is preferred)
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

            var match = _deactivatedEvents.FirstOrDefault(predicate);
            if (match != null)
            {
                return match;
            }
        }

        throw new TimeoutException($"Timed out waiting for grain deactivation matching predicate after {effectiveTimeout}");
    }

    /// <summary>
    /// Checks if a specific grain has been deactivated.
    /// </summary>
    /// <param name="grainId">The grain ID to check.</param>
    /// <returns>True if the grain has been deactivated.</returns>
    public bool HasGrainDeactivated(GrainId grainId)
    {
        return _deactivatedEvents.Any(e => e.GrainContext.GrainId == grainId);
    }

    /// <summary>
    /// Checks if a specific grain has been activated.
    /// </summary>
    /// <param name="grainId">The grain ID to check.</param>
    /// <returns>True if the grain has been activated.</returns>
    public bool HasGrainActivated(GrainId grainId)
    {
        return _activatedEvents.Any(e => e.GrainContext.GrainId == grainId);
    }

    /// <summary>
    /// Gets the count of deactivated events for a specific grain type.
    /// </summary>
    /// <param name="grainTypeName">The grain type name to filter by.</param>
    /// <returns>The count of deactivation events.</returns>
    public int GetDeactivationCount(string grainTypeName)
    {
        return _deactivatedEvents.Count(e => GetGrainTypeName(e.GrainContext).Contains(grainTypeName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets the count of activated events for a specific grain type.
    /// </summary>
    /// <param name="grainTypeName">The grain type name to filter by.</param>
    /// <returns>The count of activation events.</returns>
    public int GetActivationCount(string grainTypeName)
    {
        return _activatedEvents.Count(e => GetGrainTypeName(e.GrainContext).Contains(grainTypeName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Waits until the deactivation count for a specific grain type reaches or exceeds the expected count.
    /// </summary>
    /// <param name="grainTypeName">The grain type name to filter by.</param>
    /// <param name="expectedCount">The minimum number of deactivations to wait for.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 60 seconds.</param>
    /// <returns>A task that completes when the expected count is reached.</returns>
    /// <exception cref="TimeoutException">Thrown if the expected count is not reached within the timeout.</exception>
    public async Task WaitForDeactivationCountAsync(string grainTypeName, int expectedCount, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(60);
        using var cts = new CancellationTokenSource(effectiveTimeout);

        while (!cts.Token.IsCancellationRequested)
        {
            var currentCount = GetDeactivationCount(grainTypeName);
            if (currentCount >= expectedCount)
            {
                return;
            }

            try
            {
                await Task.Delay(50, cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        var finalCount = GetDeactivationCount(grainTypeName);
        if (finalCount >= expectedCount)
        {
            return;
        }

        throw new TimeoutException($"Timed out waiting for {expectedCount} deactivations of grain type '{grainTypeName}'. Current count: {finalCount} after {effectiveTimeout}");
    }

    /// <summary>
    /// Waits until the activation count for a specific grain type reaches or exceeds the expected count.
    /// </summary>
    /// <param name="grainTypeName">The grain type name to filter by.</param>
    /// <param name="expectedCount">The minimum number of activations to wait for.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 60 seconds.</param>
    /// <returns>A task that completes when the expected count is reached.</returns>
    /// <exception cref="TimeoutException">Thrown if the expected count is not reached within the timeout.</exception>
    public async Task WaitForActivationCountAsync(string grainTypeName, int expectedCount, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(60);
        using var cts = new CancellationTokenSource(effectiveTimeout);

        while (!cts.Token.IsCancellationRequested)
        {
            var currentCount = GetActivationCount(grainTypeName);
            if (currentCount >= expectedCount)
            {
                return;
            }

            try
            {
                await Task.Delay(50, cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        var finalCount = GetActivationCount(grainTypeName);
        if (finalCount >= expectedCount)
        {
            return;
        }

        throw new TimeoutException($"Timed out waiting for {expectedCount} activations of grain type '{grainTypeName}'. Current count: {finalCount} after {effectiveTimeout}");
    }

    /// <summary>
    /// Clears all captured events.
    /// </summary>
    public void Clear()
    {
        _createdEvents.Clear();
        _activatedEvents.Clear();
        _deactivatingEvents.Clear();
        _deactivatedEvents.Clear();
    }

    private async Task<object?> WaitForEventAsync(GrainId grainId, string eventName, TimeSpan timeout)
    {
        var key = (grainId, eventName);
        var existingEvent = GetExistingEvent(grainId, eventName);
        if (existingEvent != null)
        {
            return existingEvent;
        }

        var waiter = CreateWaiter(key);
        try
        {
            existingEvent = GetExistingEvent(grainId, eventName);
            if (existingEvent != null)
            {
                return existingEvent;
            }

            return await waiter.Task.WaitAsync(timeout).ConfigureAwait(false);
        }
        finally
        {
            RemoveWaiter(key, waiter);
        }
    }

    private object? GetExistingEvent(GrainId grainId, string eventName)
    {
        return eventName switch
        {
            nameof(GrainLifecycleEvents.Created) => _createdEvents.FirstOrDefault(e => e.GrainContext.GrainId == grainId),
            nameof(GrainLifecycleEvents.Activated) => _activatedEvents.FirstOrDefault(e => e.GrainContext.GrainId == grainId),
            nameof(GrainLifecycleEvents.Deactivating) => _deactivatingEvents.FirstOrDefault(e => e.GrainContext.GrainId == grainId),
            nameof(GrainLifecycleEvents.Deactivated) => _deactivatedEvents.FirstOrDefault(e => e.GrainContext.GrainId == grainId),
            _ => null
        };
    }

    private TaskCompletionSource<object?> CreateWaiter((GrainId GrainId, string EventName) key)
    {
        var waiter = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiters = _waiters.GetOrAdd(key, static _ => []);
        lock (waiters)
        {
            waiters.Add(waiter);
        }

        return waiter;
    }

    private void RemoveWaiter((GrainId GrainId, string EventName) key, TaskCompletionSource<object?> waiter)
    {
        if (!_waiters.TryGetValue(key, out var waiters))
        {
            return;
        }

        lock (waiters)
        {
            waiters.Remove(waiter);
        }
    }

    private void SignalWaiters(GrainId grainId, string eventName, object payload)
    {
        var key = (grainId, eventName);
        if (_waiters.TryGetValue(key, out var waiters))
        {
            List<TaskCompletionSource<object?>> waitersSnapshot;
            lock (waiters)
            {
                waitersSnapshot = [.. waiters];
            }

            foreach (var waiter in waitersSnapshot)
            {
                if (waiter.TrySetResult(payload))
                {
                    RemoveWaiter(key, waiter);
                }
            }
        }
    }

    void IObserver<GrainLifecycleEvents.LifecycleEvent>.OnNext(GrainLifecycleEvents.LifecycleEvent value)
    {
        switch (value)
        {
            case GrainLifecycleEvents.Created created:
                _createdEvents.Add(created);
                SignalWaiters(created.GrainContext.GrainId, nameof(GrainLifecycleEvents.Created), created);
                break;

            case GrainLifecycleEvents.Activated activated:
                _activatedEvents.Add(activated);
                SignalWaiters(activated.GrainContext.GrainId, nameof(GrainLifecycleEvents.Activated), activated);
                break;

            case GrainLifecycleEvents.Deactivating deactivating:
                _deactivatingEvents.Add(deactivating);
                SignalWaiters(deactivating.GrainContext.GrainId, nameof(GrainLifecycleEvents.Deactivating), deactivating);
                break;

            case GrainLifecycleEvents.Deactivated deactivated:
                _deactivatedEvents.Add(deactivated);
                SignalWaiters(deactivated.GrainContext.GrainId, nameof(GrainLifecycleEvents.Deactivated), deactivated);
                break;
        }
    }

    void IObserver<GrainLifecycleEvents.LifecycleEvent>.OnError(Exception error) { }
    void IObserver<GrainLifecycleEvents.LifecycleEvent>.OnCompleted() { }

    /// <summary>
    /// Stops listening for grain diagnostic events.
    /// </summary>
    public void Dispose()
    {
        _subscription?.Dispose();
    }

    private static string GetGrainTypeName(IGrainContext grainContext) => grainContext.GrainId.Type.ToString()!;
}

/// <summary>
/// Extension methods for working with grain references and diagnostic observers.
/// </summary>
public static class GrainDiagnosticExtensions
{
    /// <summary>
    /// Waits for a grain to be created.
    /// </summary>
    public static Task<GrainLifecycleEvents.Created> WaitForCreatedAsync(this GrainDiagnosticObserver observer, IAddressable grain, TimeSpan? timeout = null)
    {
        return observer.WaitForGrainCreatedAsync(grain.GetGrainId(), timeout);
    }

    /// <summary>
    /// Waits for a grain to be deactivated.
    /// </summary>
    public static Task<GrainLifecycleEvents.Deactivated> WaitForDeactivatedAsync(this GrainDiagnosticObserver observer, IAddressable grain, TimeSpan? timeout = null)
    {
        return observer.WaitForGrainDeactivatedAsync(grain.GetGrainId(), timeout);
    }

    /// <summary>
    /// Waits for a grain to be activated.
    /// </summary>
    public static Task<GrainLifecycleEvents.Activated> WaitForActivatedAsync(this GrainDiagnosticObserver observer, IAddressable grain, TimeSpan? timeout = null)
    {
        return observer.WaitForGrainActivatedAsync(grain.GetGrainId(), timeout);
    }
}
