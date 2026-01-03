#nullable enable
using System.Collections.Concurrent;
using System.Diagnostics;
using Orleans;
using Orleans.Diagnostics;
using Orleans.Runtime;

namespace TestExtensions;

/// <summary>
/// A test helper that subscribes to Orleans grain diagnostic events and provides
/// methods to wait for specific grain lifecycle events deterministically.
/// </summary>
/// <remarks>
/// This replaces Task.Delay/Thread.Sleep patterns in tests with event-driven waiting,
/// making tests faster and more reliable.
/// </remarks>
public sealed class GrainDiagnosticObserver : IDisposable, IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
{
    private readonly ConcurrentDictionary<(GrainId GrainId, string EventName), TaskCompletionSource<object?>> _waiters = new();
    private readonly ConcurrentBag<GrainCreatedEvent> _createdEvents = new();
    private readonly ConcurrentBag<GrainActivatedEvent> _activatedEvents = new();
    private readonly ConcurrentBag<GrainDeactivatingEvent> _deactivatingEvents = new();
    private readonly ConcurrentBag<GrainDeactivatedEvent> _deactivatedEvents = new();
    private readonly List<IDisposable> _subscriptions = new();
    private IDisposable? _listenerSubscription;
    private int _listenerSubscriptionCount;

    /// <summary>
    /// Gets the number of Orleans.Grains listeners that have been subscribed to.
    /// </summary>
    public int ListenerSubscriptionCount => _listenerSubscriptionCount;

    /// <summary>
    /// Gets all captured grain created events.
    /// </summary>
    public IReadOnlyCollection<GrainCreatedEvent> CreatedEvents => _createdEvents.ToArray();

    /// <summary>
    /// Gets all captured grain activated events.
    /// </summary>
    public IReadOnlyCollection<GrainActivatedEvent> ActivatedEvents => _activatedEvents.ToArray();

    /// <summary>
    /// Gets all captured grain deactivating events.
    /// </summary>
    public IReadOnlyCollection<GrainDeactivatingEvent> DeactivatingEvents => _deactivatingEvents.ToArray();

    /// <summary>
    /// Gets all captured grain deactivated events.
    /// </summary>
    public IReadOnlyCollection<GrainDeactivatedEvent> DeactivatedEvents => _deactivatedEvents.ToArray();

    /// <summary>
    /// Creates a new instance of the observer and starts listening for grain diagnostic events.
    /// </summary>
    /// <remarks>
    /// <see cref="DiagnosticListener.AllListeners"/> will call <see cref="IObserver{T}.OnNext"/> for all
    /// existing listeners synchronously during the <see cref="IObservable{T}.Subscribe"/> call, 
    /// then continue to call <see cref="IObserver{T}.OnNext"/> for new listeners as they're created.
    /// </remarks>
    public static GrainDiagnosticObserver Create()
    {
        var observer = new GrainDiagnosticObserver();
        observer._listenerSubscription = DiagnosticListener.AllListeners.Subscribe(observer);
        return observer;
    }

    private GrainDiagnosticObserver() { }

    /// <summary>
    /// Waits for a specific grain to be deactivated.
    /// </summary>
    /// <param name="grainId">The grain ID to wait for.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <returns>The deactivated event payload.</returns>
    public async Task<GrainDeactivatedEvent> WaitForGrainDeactivatedAsync(GrainId grainId, TimeSpan? timeout = null)
    {
        var result = await WaitForEventAsync(grainId, OrleansGrainDiagnostics.EventNames.Deactivated, timeout ?? TimeSpan.FromSeconds(30));
        return (GrainDeactivatedEvent)result!;
    }

    /// <summary>
    /// Waits for a specific grain to start deactivating.
    /// </summary>
    /// <param name="grainId">The grain ID to wait for.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <returns>The deactivating event payload.</returns>
    public async Task<GrainDeactivatingEvent> WaitForGrainDeactivatingAsync(GrainId grainId, TimeSpan? timeout = null)
    {
        var result = await WaitForEventAsync(grainId, OrleansGrainDiagnostics.EventNames.Deactivating, timeout ?? TimeSpan.FromSeconds(30));
        return (GrainDeactivatingEvent)result!;
    }

    /// <summary>
    /// Waits for a specific grain to be activated.
    /// </summary>
    /// <param name="grainId">The grain ID to wait for.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <returns>The activated event payload.</returns>
    public async Task<GrainActivatedEvent> WaitForGrainActivatedAsync(GrainId grainId, TimeSpan? timeout = null)
    {
        var result = await WaitForEventAsync(grainId, OrleansGrainDiagnostics.EventNames.Activated, timeout ?? TimeSpan.FromSeconds(30));
        return (GrainActivatedEvent)result!;
    }

    /// <summary>
    /// Waits for a specific grain to be created.
    /// </summary>
    /// <param name="grainId">The grain ID to wait for.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <returns>The created event payload.</returns>
    public async Task<GrainCreatedEvent> WaitForGrainCreatedAsync(GrainId grainId, TimeSpan? timeout = null)
    {
        var result = await WaitForEventAsync(grainId, OrleansGrainDiagnostics.EventNames.Created, timeout ?? TimeSpan.FromSeconds(30));
        return (GrainCreatedEvent)result!;
    }

    /// <summary>
    /// Waits for any grain matching the predicate to be deactivated.
    /// </summary>
    /// <param name="predicate">A predicate to match the grain.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <returns>The deactivated event payload.</returns>
    public async Task<GrainDeactivatedEvent> WaitForAnyGrainDeactivatedAsync(Func<GrainDeactivatedEvent, bool> predicate, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        var cts = new CancellationTokenSource(effectiveTimeout);

        // Check if we already have a matching event
        var existingMatch = _deactivatedEvents.FirstOrDefault(predicate);
        if (existingMatch != null)
        {
            return existingMatch;
        }

        // Poll for new events (this is a fallback; the event-driven approach is preferred)
        while (!cts.Token.IsCancellationRequested)
        {
            await Task.Delay(10, cts.Token);
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
        return _deactivatedEvents.Any(e => e.GrainId == grainId);
    }

    /// <summary>
    /// Checks if a specific grain has been activated.
    /// </summary>
    /// <param name="grainId">The grain ID to check.</param>
    /// <returns>True if the grain has been activated.</returns>
    public bool HasGrainActivated(GrainId grainId)
    {
        return _activatedEvents.Any(e => e.GrainId == grainId);
    }

    /// <summary>
    /// Gets the count of deactivated events for a specific grain type.
    /// </summary>
    /// <param name="grainTypeName">The grain type name to filter by.</param>
    /// <returns>The count of deactivation events.</returns>
    public int GetDeactivationCount(string grainTypeName)
    {
        return _deactivatedEvents.Count(e => e.GrainType.Contains(grainTypeName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets the count of activated events for a specific grain type.
    /// </summary>
    /// <param name="grainTypeName">The grain type name to filter by.</param>
    /// <returns>The count of activation events.</returns>
    public int GetActivationCount(string grainTypeName)
    {
        return _activatedEvents.Count(e => e.GrainType.Contains(grainTypeName, StringComparison.OrdinalIgnoreCase));
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
        var tcs = _waiters.GetOrAdd(key, _ => new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously));

        // Check if the event already occurred
        object? existingEvent = eventName switch
        {
            OrleansGrainDiagnostics.EventNames.Created => _createdEvents.FirstOrDefault(e => e.GrainId == grainId),
            OrleansGrainDiagnostics.EventNames.Activated => _activatedEvents.FirstOrDefault(e => e.GrainId == grainId),
            OrleansGrainDiagnostics.EventNames.Deactivating => _deactivatingEvents.FirstOrDefault(e => e.GrainId == grainId),
            OrleansGrainDiagnostics.EventNames.Deactivated => _deactivatedEvents.FirstOrDefault(e => e.GrainId == grainId),
            _ => null
        };

        if (existingEvent != null)
        {
            return existingEvent;
        }

        using var cts = new CancellationTokenSource(timeout);
        using var registration = cts.Token.Register(() => tcs.TrySetException(new TimeoutException($"Timed out waiting for {eventName} event for grain {grainId} after {timeout}")));

        return await tcs.Task;
    }

    private void SignalWaiters(GrainId grainId, string eventName, object payload)
    {
        var key = (grainId, eventName);
        if (_waiters.TryRemove(key, out var tcs))
        {
            tcs.TrySetResult(payload);
        }
    }

    void IObserver<DiagnosticListener>.OnNext(DiagnosticListener listener)
    {
        if (listener.Name == OrleansGrainDiagnostics.ListenerName)
        {
            var subscription = listener.Subscribe(this);
            _subscriptions.Add(subscription);
            Interlocked.Increment(ref _listenerSubscriptionCount);
        }
    }

    void IObserver<KeyValuePair<string, object?>>.OnNext(KeyValuePair<string, object?> kvp)
    {
        switch (kvp.Key)
        {
            case OrleansGrainDiagnostics.EventNames.Created when kvp.Value is GrainCreatedEvent created:
                _createdEvents.Add(created);
                SignalWaiters(created.GrainId, kvp.Key, created);
                break;

            case OrleansGrainDiagnostics.EventNames.Activated when kvp.Value is GrainActivatedEvent activated:
                _activatedEvents.Add(activated);
                SignalWaiters(activated.GrainId, kvp.Key, activated);
                break;

            case OrleansGrainDiagnostics.EventNames.Deactivating when kvp.Value is GrainDeactivatingEvent deactivating:
                _deactivatingEvents.Add(deactivating);
                SignalWaiters(deactivating.GrainId, kvp.Key, deactivating);
                break;

            case OrleansGrainDiagnostics.EventNames.Deactivated when kvp.Value is GrainDeactivatedEvent deactivated:
                _deactivatedEvents.Add(deactivated);
                SignalWaiters(deactivated.GrainId, kvp.Key, deactivated);
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
/// Extension methods for working with grain references and diagnostic observers.
/// </summary>
public static class GrainDiagnosticExtensions
{
    /// <summary>
    /// Waits for a grain to be deactivated.
    /// </summary>
    public static Task<GrainDeactivatedEvent> WaitForDeactivatedAsync(this GrainDiagnosticObserver observer, IAddressable grain, TimeSpan? timeout = null)
    {
        return observer.WaitForGrainDeactivatedAsync(grain.GetGrainId(), timeout);
    }

    /// <summary>
    /// Waits for a grain to be activated.
    /// </summary>
    public static Task<GrainActivatedEvent> WaitForActivatedAsync(this GrainDiagnosticObserver observer, IAddressable grain, TimeSpan? timeout = null)
    {
        return observer.WaitForGrainActivatedAsync(grain.GetGrainId(), timeout);
    }
}
