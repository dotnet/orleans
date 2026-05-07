using System.Collections.Concurrent;
using System.Diagnostics;

namespace Orleans.TestingHost.Diagnostics;

/// <summary>
/// Represents a captured diagnostic event with its name and payload.
/// </summary>
/// <param name="Name">The name of the event.</param>
/// <param name="Payload">The event payload object.</param>
/// <param name="Timestamp">The time the event was captured.</param>
public readonly record struct DiagnosticEvent(
    string Name,
    object? Payload,
    DateTimeOffset Timestamp);

/// <summary>
/// A test utility for collecting and waiting on diagnostic events.
/// Subscribes to <see cref="DiagnosticListener.AllListeners"/> and captures events
/// from listeners matching specified patterns.
/// </summary>
/// <remarks>
/// Use this class to wait for specific Orleans events in tests without
/// relying on Task.Delay or Thread.Sleep.
/// </remarks>
public sealed class DiagnosticEventCollector : IDisposable, IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
{
    private readonly ConcurrentQueue<DiagnosticEvent> _events = new();
    private readonly ConcurrentDictionary<string, List<EventWaiter>> _waiters = new();
    private readonly List<IDisposable> _subscriptions = [];
    private readonly HashSet<string> _listenerPrefixes;
    private readonly IDisposable? _allListenersSubscription;
    private bool _disposed;

    /// <summary>
    /// Creates a new diagnostic event collector that subscribes to listeners with names
    /// starting with any of the specified prefixes.
    /// </summary>
    /// <param name="listenerPrefixes">
    /// Prefixes of listener names to subscribe to (e.g., "Orleans." to capture all Orleans events).
    /// If empty, subscribes to all listeners.
    /// </param>
    public DiagnosticEventCollector(IEnumerable<string>? listenerPrefixes = null)
    {
        _listenerPrefixes = listenerPrefixes?.ToHashSet() ?? [];
        _allListenersSubscription = DiagnosticListener.AllListeners.Subscribe(this);
    }

    /// <summary>
    /// Creates a new diagnostic event collector that subscribes to listeners with names
    /// starting with any of the specified prefixes.
    /// </summary>
    /// <param name="listenerPrefixes">
    /// Prefixes of listener names to subscribe to (e.g., "Orleans." to capture all Orleans events).
    /// If empty, subscribes to all listeners.
    /// </param>
    public DiagnosticEventCollector(params string[] listenerPrefixes)
        : this((IEnumerable<string>)listenerPrefixes)
    {
    }

    /// <summary>
    /// Gets all captured diagnostic events.
    /// </summary>
    public IReadOnlyList<DiagnosticEvent> Events => [.. _events];

    /// <summary>
    /// Gets events with the specified name.
    /// </summary>
    public IEnumerable<DiagnosticEvent> GetEvents(string eventName)
        => _events.Where(e => e.Name == eventName);

    /// <summary>
    /// Gets the count of events with the specified name.
    /// </summary>
    public int GetEventCount(string eventName)
        => _events.Count(e => e.Name == eventName);

    /// <summary>
    /// Clears all captured events.
    /// </summary>
    public void Clear()
    {
        _events.Clear();
    }

    /// <summary>
    /// Waits for an event with the specified name to be captured.
    /// If an event with that name has already been captured, returns immediately.
    /// </summary>
    /// <param name="eventName">The name of the event to wait for.</param>
    /// <param name="timeout">The maximum time to wait.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The captured event.</returns>
    /// <exception cref="TimeoutException">Thrown if the event is not captured within the timeout.</exception>
    public async Task<DiagnosticEvent> WaitForEventAsync(
        string eventName,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (TryGetEvent(eventName, static _ => true, out var existing))
        {
            return existing;
        }

        var waiter = CreateWaiter(eventName);

        try
        {
            if (TryGetEvent(eventName, static _ => true, out existing))
            {
                return existing;
            }

            return await waiter.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            RemoveWaiter(eventName, waiter);
        }
    }

    /// <summary>
    /// Waits for an event with the specified name that matches a predicate.
    /// </summary>
    /// <param name="eventName">The name of the event to wait for.</param>
    /// <param name="predicate">A predicate to match the event payload.</param>
    /// <param name="timeout">The maximum time to wait.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The captured event.</returns>
    public async Task<DiagnosticEvent> WaitForEventAsync(
        string eventName,
        Func<DiagnosticEvent, bool> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        if (TryGetEvent(eventName, predicate, out var existing))
        {
            return existing;
        }

        var waiter = CreateWaiter(eventName, predicate);
        try
        {
            if (TryGetEvent(eventName, predicate, out existing))
            {
                return existing;
            }

            return await waiter.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            RemoveWaiter(eventName, waiter);
        }
    }

    /// <summary>
    /// Creates a task that completes when the specified event is captured.
    /// Unlike WaitForEventAsync, this does not check for existing events.
    /// </summary>
    /// <param name="eventName">The name of the event to wait for.</param>
    /// <returns>A task completion source that will be completed when the event is captured.</returns>
    public TaskCompletionSource<DiagnosticEvent> CreateEventAwaiter(string eventName)
    {
        return CreateWaiter(eventName).Completion;
    }

    /// <summary>
    /// Waits until at least the specified number of events with the given name have been captured.
    /// </summary>
    /// <param name="eventName">The name of the event to count.</param>
    /// <param name="expectedCount">The minimum number of events expected.</param>
    /// <param name="timeout">The maximum time to wait.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>All captured events with the specified name.</returns>
    /// <exception cref="TimeoutException">Thrown if the expected count is not reached within the timeout.</exception>
    public async Task<IReadOnlyList<DiagnosticEvent>> WaitForEventCountAsync(
        string eventName,
        int expectedCount,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedCount, 1);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        while (!cts.Token.IsCancellationRequested)
        {
            var currentCount = GetEventCount(eventName);
            if (currentCount >= expectedCount)
            {
                return GetEvents(eventName).ToList();
            }

            var waiter = CreateWaiter(eventName);

            try
            {
                currentCount = GetEventCount(eventName);
                if (currentCount >= expectedCount)
                {
                    return GetEvents(eventName).ToList();
                }

                await waiter.Task.WaitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cts.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting for {expectedCount} '{eventName}' events after {timeout}. Got {GetEventCount(eventName)} events.");
            }
            finally
            {
                RemoveWaiter(eventName, waiter);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new TimeoutException($"Timed out waiting for {expectedCount} '{eventName}' events after {timeout}. Got {GetEventCount(eventName)} events.");
    }

    private EventWaiter CreateWaiter(string eventName, Func<DiagnosticEvent, bool>? predicate = null)
    {
        var waiter = new EventWaiter(predicate);
        var waiters = _waiters.GetOrAdd(eventName, static _ => []);
        lock (waiters)
        {
            waiters.Add(waiter);
        }

        return waiter;
    }

    private void RemoveWaiter(string eventName, EventWaiter waiter)
    {
        if (!_waiters.TryGetValue(eventName, out var waiters))
        {
            return;
        }

        lock (waiters)
        {
            waiters.Remove(waiter);
        }
    }

    private bool TryGetEvent(string eventName, Func<DiagnosticEvent, bool> predicate, out DiagnosticEvent result)
    {
        result = _events.FirstOrDefault(e => e.Name == eventName && predicate(e));
        return result.Name is not null;
    }

    void IObserver<DiagnosticListener>.OnNext(DiagnosticListener listener)
    {
        // Subscribe to all listeners if no prefixes specified, or to matching ones
        if (_listenerPrefixes.Count == 0 || _listenerPrefixes.Any(p => listener.Name.StartsWith(p, StringComparison.Ordinal)))
        {
            var subscription = listener.Subscribe(this);
            _subscriptions.Add(subscription);
        }
    }

    void IObserver<KeyValuePair<string, object?>>.OnNext(KeyValuePair<string, object?> kvp)
    {
        var evt = new DiagnosticEvent(kvp.Key, kvp.Value, DateTimeOffset.UtcNow);
        _events.Enqueue(evt);

        // Signal any waiters
        if (_waiters.TryGetValue(kvp.Key, out var waiters))
        {
            List<EventWaiter> waitersSnapshot;
            lock (waiters)
            {
                waitersSnapshot = [.. waiters];
            }

            foreach (var waiter in waitersSnapshot)
            {
                if (waiter.TrySetResult(evt))
                {
                    RemoveWaiter(kvp.Key, waiter);
                }
            }
        }
    }

    void IObserver<DiagnosticListener>.OnError(Exception error) { }
    void IObserver<DiagnosticListener>.OnCompleted() { }
    void IObserver<KeyValuePair<string, object?>>.OnError(Exception error) { }
    void IObserver<KeyValuePair<string, object?>>.OnCompleted() { }

    private sealed class EventWaiter(Func<DiagnosticEvent, bool>? predicate)
    {
        private readonly Func<DiagnosticEvent, bool>? _predicate = predicate;

        public TaskCompletionSource<DiagnosticEvent> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<DiagnosticEvent> Task => Completion.Task;

        public bool TrySetResult(DiagnosticEvent evt)
        {
            return _predicate is null || _predicate(evt) ? Completion.TrySetResult(evt) : false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _allListenersSubscription?.Dispose();
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }
        _subscriptions.Clear();
    }
}
