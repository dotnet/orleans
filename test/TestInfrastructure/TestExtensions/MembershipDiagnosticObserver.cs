#nullable enable
using System.Collections.Concurrent;
using System.Diagnostics;
using Orleans.Diagnostics;
using Orleans.Runtime;

namespace TestExtensions;

/// <summary>
/// A test helper that subscribes to Orleans membership diagnostic events and provides
/// methods to wait for specific membership events deterministically.
/// </summary>
/// <remarks>
/// This replaces Task.Delay/Thread.Sleep patterns in tests with event-driven waiting,
/// making tests faster and more reliable.
/// </remarks>
public sealed class MembershipDiagnosticObserver : IDisposable, IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
{
    private readonly ConcurrentDictionary<(SiloAddress SiloAddress, string EventName), TaskCompletionSource<object?>> _waiters = new();
    private readonly ConcurrentBag<SiloStatusChangedEvent> _statusChangedEvents = new();
    private readonly ConcurrentBag<MembershipViewChangedEvent> _viewChangedEvents = new();
    private readonly ConcurrentBag<SiloSuspectedEvent> _suspectedEvents = new();
    private readonly ConcurrentBag<SiloDeclaredDeadEvent> _declaredDeadEvents = new();
    private readonly ConcurrentBag<SiloBecameActiveEvent> _becameActiveEvents = new();
    private readonly ConcurrentBag<SiloJoiningEvent> _joiningEvents = new();
    private readonly List<IDisposable> _subscriptions = new();
    private IDisposable? _listenerSubscription;

    /// <summary>
    /// Gets all captured silo status changed events.
    /// </summary>
    public IReadOnlyCollection<SiloStatusChangedEvent> StatusChangedEvents => _statusChangedEvents.ToArray();

    /// <summary>
    /// Gets all captured membership view changed events.
    /// </summary>
    public IReadOnlyCollection<MembershipViewChangedEvent> ViewChangedEvents => _viewChangedEvents.ToArray();

    /// <summary>
    /// Gets all captured silo suspected events.
    /// </summary>
    public IReadOnlyCollection<SiloSuspectedEvent> SuspectedEvents => _suspectedEvents.ToArray();

    /// <summary>
    /// Gets all captured silo declared dead events.
    /// </summary>
    public IReadOnlyCollection<SiloDeclaredDeadEvent> DeclaredDeadEvents => _declaredDeadEvents.ToArray();

    /// <summary>
    /// Gets all captured silo became active events.
    /// </summary>
    public IReadOnlyCollection<SiloBecameActiveEvent> BecameActiveEvents => _becameActiveEvents.ToArray();

    /// <summary>
    /// Gets all captured silo joining events.
    /// </summary>
    public IReadOnlyCollection<SiloJoiningEvent> JoiningEvents => _joiningEvents.ToArray();

    /// <summary>
    /// Creates a new instance of the observer and starts listening for membership diagnostic events.
    /// </summary>
    public static MembershipDiagnosticObserver Create()
    {
        var observer = new MembershipDiagnosticObserver();
        observer._listenerSubscription = DiagnosticListener.AllListeners.Subscribe(observer);
        return observer;
    }

    private MembershipDiagnosticObserver() { }

    /// <summary>
    /// Waits for a specific silo to be declared dead.
    /// </summary>
    /// <param name="siloAddress">The silo address to wait for.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <returns>The declared dead event payload.</returns>
    public async Task<SiloDeclaredDeadEvent> WaitForSiloDeclaredDeadAsync(SiloAddress siloAddress, TimeSpan? timeout = null)
    {
        var result = await WaitForEventAsync(siloAddress, OrleansMembershipDiagnostics.EventNames.SiloDeclaredDead, timeout ?? TimeSpan.FromSeconds(30));
        return (SiloDeclaredDeadEvent)result!;
    }

    /// <summary>
    /// Waits for any silo to be declared dead.
    /// </summary>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <returns>The declared dead event payload.</returns>
    public async Task<SiloDeclaredDeadEvent> WaitForAnySiloDeclaredDeadAsync(TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource(effectiveTimeout);

        // Check if we already have an event
        if (_declaredDeadEvents.TryPeek(out var existingEvent))
        {
            return existingEvent;
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

            if (_declaredDeadEvents.TryPeek(out var newEvent))
            {
                return newEvent;
            }
        }

        throw new TimeoutException($"Timed out waiting for any silo declared dead event after {effectiveTimeout}");
    }

    /// <summary>
    /// Waits for any silo matching the predicate to be declared dead.
    /// </summary>
    /// <param name="predicate">A predicate to match the silo.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <returns>The declared dead event payload.</returns>
    public async Task<SiloDeclaredDeadEvent> WaitForSiloDeclaredDeadAsync(Func<SiloDeclaredDeadEvent, bool> predicate, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource(effectiveTimeout);

        // Check if we already have a matching event
        var existingMatch = _declaredDeadEvents.FirstOrDefault(predicate);
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

            var match = _declaredDeadEvents.FirstOrDefault(predicate);
            if (match != null)
            {
                return match;
            }
        }

        throw new TimeoutException($"Timed out waiting for silo declared dead event matching predicate after {effectiveTimeout}");
    }

    /// <summary>
    /// Waits for a specific silo's status to change to the specified status.
    /// </summary>
    /// <param name="siloAddress">The silo address to monitor.</param>
    /// <param name="expectedStatus">The expected new status (e.g., "Dead", "Active").</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <returns>The status changed event payload.</returns>
    public async Task<SiloStatusChangedEvent> WaitForSiloStatusAsync(SiloAddress siloAddress, string expectedStatus, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource(effectiveTimeout);

        // Check if we already have a matching event
        var existingMatch = _statusChangedEvents.FirstOrDefault(e =>
            e.SiloAddress.Equals(siloAddress) &&
            e.NewStatus.Equals(expectedStatus, StringComparison.OrdinalIgnoreCase));
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

            var match = _statusChangedEvents.FirstOrDefault(e =>
                e.SiloAddress.Equals(siloAddress) &&
                e.NewStatus.Equals(expectedStatus, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                return match;
            }
        }

        throw new TimeoutException($"Timed out waiting for silo {siloAddress} to change to status '{expectedStatus}' after {effectiveTimeout}");
    }

    /// <summary>
    /// Waits for a specific silo to become active.
    /// </summary>
    /// <param name="siloAddress">The silo address to wait for.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <returns>The became active event payload.</returns>
    public async Task<SiloBecameActiveEvent> WaitForSiloBecameActiveAsync(SiloAddress siloAddress, TimeSpan? timeout = null)
    {
        var result = await WaitForEventAsync(siloAddress, OrleansMembershipDiagnostics.EventNames.SiloBecameActive, timeout ?? TimeSpan.FromSeconds(30));
        return (SiloBecameActiveEvent)result!;
    }

    /// <summary>
    /// Waits for any silo to become active.
    /// </summary>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <returns>The became active event payload.</returns>
    public async Task<SiloBecameActiveEvent> WaitForAnySiloBecameActiveAsync(TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource(effectiveTimeout);

        // Check if we already have an event
        if (_becameActiveEvents.TryPeek(out var existingEvent))
        {
            return existingEvent;
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

            if (_becameActiveEvents.TryPeek(out var newEvent))
            {
                return newEvent;
            }
        }

        throw new TimeoutException($"Timed out waiting for any silo became active event after {effectiveTimeout}");
    }

    /// <summary>
    /// Waits for a specific silo to start joining the cluster.
    /// </summary>
    /// <param name="siloAddress">The silo address to wait for.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <returns>The joining event payload.</returns>
    public async Task<SiloJoiningEvent> WaitForSiloJoiningAsync(SiloAddress siloAddress, TimeSpan? timeout = null)
    {
        var result = await WaitForEventAsync(siloAddress, OrleansMembershipDiagnostics.EventNames.SiloJoining, timeout ?? TimeSpan.FromSeconds(30));
        return (SiloJoiningEvent)result!;
    }

    /// <summary>
    /// Checks if a specific silo has been declared dead.
    /// </summary>
    /// <param name="siloAddress">The silo address to check.</param>
    /// <returns>True if the silo has been declared dead.</returns>
    public bool HasSiloDeclaredDead(SiloAddress siloAddress)
    {
        return _declaredDeadEvents.Any(e => e.DeadSilo.Equals(siloAddress));
    }

    /// <summary>
    /// Checks if a specific silo has become active.
    /// </summary>
    /// <param name="siloAddress">The silo address to check.</param>
    /// <returns>True if the silo has become active.</returns>
    public bool HasSiloBecameActive(SiloAddress siloAddress)
    {
        return _becameActiveEvents.Any(e => e.SiloAddress.Equals(siloAddress));
    }

    /// <summary>
    /// Gets the count of silos declared dead.
    /// </summary>
    /// <returns>The count of declared dead events.</returns>
    public int GetDeclaredDeadCount()
    {
        return _declaredDeadEvents.Count;
    }

    /// <summary>
    /// Waits until the number of declared dead silos reaches or exceeds the expected count.
    /// </summary>
    /// <param name="expectedCount">The minimum number of silos declared dead to wait for.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 60 seconds.</param>
    /// <returns>A task that completes when the expected count is reached.</returns>
    /// <exception cref="TimeoutException">Thrown if the expected count is not reached within the timeout.</exception>
    public async Task WaitForDeclaredDeadCountAsync(int expectedCount, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(60);
        using var cts = new CancellationTokenSource(effectiveTimeout);

        while (!cts.Token.IsCancellationRequested)
        {
            var currentCount = GetDeclaredDeadCount();
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

        var finalCount = GetDeclaredDeadCount();
        if (finalCount >= expectedCount)
        {
            return;
        }

        throw new TimeoutException($"Timed out waiting for {expectedCount} silos to be declared dead. Current count: {finalCount} after {effectiveTimeout}");
    }

    /// <summary>
    /// Clears all captured events.
    /// </summary>
    public void Clear()
    {
        _statusChangedEvents.Clear();
        _viewChangedEvents.Clear();
        _suspectedEvents.Clear();
        _declaredDeadEvents.Clear();
        _becameActiveEvents.Clear();
        _joiningEvents.Clear();
    }

    private async Task<object?> WaitForEventAsync(SiloAddress siloAddress, string eventName, TimeSpan timeout)
    {
        var key = (siloAddress, eventName);
        var tcs = _waiters.GetOrAdd(key, _ => new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously));

        // Check if the event already occurred
        object? existingEvent = eventName switch
        {
            OrleansMembershipDiagnostics.EventNames.SiloDeclaredDead => _declaredDeadEvents.FirstOrDefault(e => e.DeadSilo.Equals(siloAddress)),
            OrleansMembershipDiagnostics.EventNames.SiloBecameActive => _becameActiveEvents.FirstOrDefault(e => e.SiloAddress.Equals(siloAddress)),
            OrleansMembershipDiagnostics.EventNames.SiloJoining => _joiningEvents.FirstOrDefault(e => e.SiloAddress.Equals(siloAddress)),
            _ => null
        };

        if (existingEvent != null)
        {
            return existingEvent;
        }

        using var cts = new CancellationTokenSource(timeout);
        using var registration = cts.Token.Register(() => tcs.TrySetException(new TimeoutException($"Timed out waiting for {eventName} event for silo {siloAddress} after {timeout}")));

        return await tcs.Task;
    }

    private void SignalWaiters(SiloAddress siloAddress, string eventName, object payload)
    {
        var key = (siloAddress, eventName);
        if (_waiters.TryRemove(key, out var tcs))
        {
            tcs.TrySetResult(payload);
        }
    }

    void IObserver<DiagnosticListener>.OnNext(DiagnosticListener listener)
    {
        if (listener.Name == OrleansMembershipDiagnostics.ListenerName)
        {
            var subscription = listener.Subscribe(this);
            _subscriptions.Add(subscription);
        }
    }

    void IObserver<KeyValuePair<string, object?>>.OnNext(KeyValuePair<string, object?> kvp)
    {
        switch (kvp.Key)
        {
            case OrleansMembershipDiagnostics.EventNames.SiloStatusChanged when kvp.Value is SiloStatusChangedEvent statusChanged:
                _statusChangedEvents.Add(statusChanged);
                break;

            case OrleansMembershipDiagnostics.EventNames.ViewChanged when kvp.Value is MembershipViewChangedEvent viewChanged:
                _viewChangedEvents.Add(viewChanged);
                break;

            case OrleansMembershipDiagnostics.EventNames.SiloSuspected when kvp.Value is SiloSuspectedEvent suspected:
                _suspectedEvents.Add(suspected);
                break;

            case OrleansMembershipDiagnostics.EventNames.SiloDeclaredDead when kvp.Value is SiloDeclaredDeadEvent declaredDead:
                _declaredDeadEvents.Add(declaredDead);
                SignalWaiters(declaredDead.DeadSilo, kvp.Key, declaredDead);
                break;

            case OrleansMembershipDiagnostics.EventNames.SiloBecameActive when kvp.Value is SiloBecameActiveEvent becameActive:
                _becameActiveEvents.Add(becameActive);
                SignalWaiters(becameActive.SiloAddress, kvp.Key, becameActive);
                break;

            case OrleansMembershipDiagnostics.EventNames.SiloJoining when kvp.Value is SiloJoiningEvent joining:
                _joiningEvents.Add(joining);
                SignalWaiters(joining.SiloAddress, kvp.Key, joining);
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
