#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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
    private readonly ConcurrentDictionary<string, List<TaskCompletionSource<DiagnosticEvent>>> _waiters = new();
    private readonly List<IDisposable> _subscriptions = new();
    private readonly HashSet<string> _listenerPrefixes;
    private readonly TimeProvider _timeProvider;
    private IDisposable? _allListenersSubscription;
    private bool _disposed;

    /// <summary>
    /// Creates a new diagnostic event collector that subscribes to listeners with names
    /// starting with any of the specified prefixes.
    /// </summary>
    /// <param name="listenerPrefixes">
    /// Prefixes of listener names to subscribe to (e.g., "Orleans." to capture all Orleans events).
    /// If empty, subscribes to all listeners.
    /// </param>
    /// <param name="timeProvider">Optional time provider for timestamps.</param>
    public DiagnosticEventCollector(IEnumerable<string>? listenerPrefixes = null, TimeProvider? timeProvider = null)
    {
        _listenerPrefixes = listenerPrefixes?.ToHashSet() ?? [];
        _timeProvider = timeProvider ?? TimeProvider.System;
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
        // Check if the event already occurred
        var existing = _events.FirstOrDefault(e => e.Name == eventName);
        if (existing.Name != null)
        {
            return existing;
        }

        // Create a waiter
        var tcs = new TaskCompletionSource<DiagnosticEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiters = _waiters.GetOrAdd(eventName, _ => new List<TaskCompletionSource<DiagnosticEvent>>());
        lock (waiters)
        {
            waiters.Add(tcs);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        using var registration = cts.Token.Register(
            () => tcs.TrySetException(new TimeoutException($"Timed out waiting for event '{eventName}' after {timeout}")));

        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (waiters)
            {
                waiters.Remove(tcs);
            }
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

        // Check if a matching event already occurred
        var existing = _events.FirstOrDefault(e => e.Name == eventName && predicate(e));
        if (existing.Name != null)
        {
            return existing;
        }

        // Create a waiter with a predicate filter
        var tcs = new TaskCompletionSource<DiagnosticEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiters = _waiters.GetOrAdd(eventName, _ => new List<TaskCompletionSource<DiagnosticEvent>>());

        // Use a wrapper TCS that applies the predicate
        var filterTcs = new TaskCompletionSource<DiagnosticEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (waiters)
        {
            waiters.Add(tcs);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        using var registration = cts.Token.Register(
            () => filterTcs.TrySetException(new TimeoutException($"Timed out waiting for event '{eventName}' matching predicate after {timeout}")));

        // Keep waiting for events until we find one matching the predicate
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var evt = await tcs.Task.ConfigureAwait(false);
                if (predicate(evt))
                {
                    return evt;
                }

                // Reset the TCS for the next event
                tcs = new TaskCompletionSource<DiagnosticEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (waiters)
                {
                    waiters.Add(tcs);
                }
            }

            throw new OperationCanceledException(cancellationToken);
        }
        finally
        {
            lock (waiters)
            {
                waiters.Remove(tcs);
            }
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
        var tcs = new TaskCompletionSource<DiagnosticEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiters = _waiters.GetOrAdd(eventName, _ => new List<TaskCompletionSource<DiagnosticEvent>>());
        lock (waiters)
        {
            waiters.Add(tcs);
        }
        return tcs;
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
        var evt = new DiagnosticEvent(kvp.Key, kvp.Value, _timeProvider.GetUtcNow());
        _events.Enqueue(evt);

        // Signal any waiters
        if (_waiters.TryGetValue(kvp.Key, out var waiters))
        {
            List<TaskCompletionSource<DiagnosticEvent>> waitersSnapshot;
            lock (waiters)
            {
                waitersSnapshot = [.. waiters];
            }

            foreach (var tcs in waitersSnapshot)
            {
                tcs.TrySetResult(evt);
            }
        }
    }

    void IObserver<DiagnosticListener>.OnError(Exception error) { }
    void IObserver<DiagnosticListener>.OnCompleted() { }
    void IObserver<KeyValuePair<string, object?>>.OnError(Exception error) { }
    void IObserver<KeyValuePair<string, object?>>.OnCompleted() { }

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
