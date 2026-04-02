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
    private readonly ConcurrentBag<SiloStatusChangedEvent> _statusChangedEvents = new();
    private readonly ConcurrentBag<MembershipViewChangedEvent> _viewChangedEvents = new();
    private readonly ConcurrentBag<SiloSuspectedEvent> _suspectedEvents = new();
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
    /// Gets all captured status change events whose new status is <see cref="SiloStatus.Dead"/>.
    /// </summary>
    public IReadOnlyCollection<SiloStatusChangedEvent> DeclaredDeadEvents => GetEventsByStatus(SiloStatus.Dead);

    /// <summary>
    /// Gets all captured status change events whose new status is <see cref="SiloStatus.Active"/>.
    /// </summary>
    public IReadOnlyCollection<SiloStatusChangedEvent> BecameActiveEvents => GetEventsByStatus(SiloStatus.Active);

    /// <summary>
    /// Gets all captured status change events whose new status is <see cref="SiloStatus.Joining"/>.
    /// </summary>
    public IReadOnlyCollection<SiloStatusChangedEvent> JoiningEvents => GetEventsByStatus(SiloStatus.Joining);

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
    /// <returns>The status changed event payload.</returns>
    public Task<SiloStatusChangedEvent> WaitForSiloDeclaredDeadAsync(SiloAddress siloAddress, TimeSpan? timeout = null)
    {
        return WaitForStatusChangeAsync(
            e => e.NewEntry.SiloAddress.Equals(siloAddress) && e.NewEntry.Status == SiloStatus.Dead,
            timeout ?? TimeSpan.FromSeconds(30),
            $"silo {siloAddress} to be declared dead");
    }

    /// <summary>
    /// Waits for any silo to be declared dead.
    /// </summary>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <returns>The status changed event payload.</returns>
    public Task<SiloStatusChangedEvent> WaitForAnySiloDeclaredDeadAsync(TimeSpan? timeout = null)
    {
        return WaitForStatusChangeAsync(
            e => e.NewEntry.Status == SiloStatus.Dead,
            timeout ?? TimeSpan.FromSeconds(30),
            "any silo to be declared dead");
    }

    /// <summary>
    /// Waits for any status change event matching the predicate to be declared dead.
    /// </summary>
    /// <param name="predicate">A predicate to match the event.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <returns>The status changed event payload.</returns>
    public Task<SiloStatusChangedEvent> WaitForSiloDeclaredDeadAsync(Func<SiloStatusChangedEvent, bool> predicate, TimeSpan? timeout = null)
    {
        return WaitForStatusChangeAsync(
            e => e.NewEntry.Status == SiloStatus.Dead && predicate(e),
            timeout ?? TimeSpan.FromSeconds(30),
            "a dead-silo status change matching the predicate");
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

        var existingMatch = _statusChangedEvents.FirstOrDefault(e =>
            e.NewEntry.SiloAddress.Equals(siloAddress) &&
            e.NewEntry.Status.ToString().Equals(expectedStatus, StringComparison.OrdinalIgnoreCase));
        if (existingMatch != null)
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

            var match = _statusChangedEvents.FirstOrDefault(e =>
                e.NewEntry.SiloAddress.Equals(siloAddress) &&
                e.NewEntry.Status.ToString().Equals(expectedStatus, StringComparison.OrdinalIgnoreCase));
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
    /// <returns>The status changed event payload.</returns>
    public Task<SiloStatusChangedEvent> WaitForSiloBecameActiveAsync(SiloAddress siloAddress, TimeSpan? timeout = null)
    {
        return WaitForStatusChangeAsync(
            e => e.NewEntry.SiloAddress.Equals(siloAddress) && e.NewEntry.Status == SiloStatus.Active,
            timeout ?? TimeSpan.FromSeconds(30),
            $"silo {siloAddress} to become active");
    }

    /// <summary>
    /// Waits for any silo to become active.
    /// </summary>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <returns>The status changed event payload.</returns>
    public Task<SiloStatusChangedEvent> WaitForAnySiloBecameActiveAsync(TimeSpan? timeout = null)
    {
        return WaitForStatusChangeAsync(
            e => e.NewEntry.Status == SiloStatus.Active,
            timeout ?? TimeSpan.FromSeconds(30),
            "any silo to become active");
    }

    /// <summary>
    /// Waits for a specific silo to start joining the cluster.
    /// </summary>
    /// <param name="siloAddress">The silo address to wait for.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <returns>The status changed event payload.</returns>
    public Task<SiloStatusChangedEvent> WaitForSiloJoiningAsync(SiloAddress siloAddress, TimeSpan? timeout = null)
    {
        return WaitForStatusChangeAsync(
            e => e.NewEntry.SiloAddress.Equals(siloAddress) && e.NewEntry.Status == SiloStatus.Joining,
            timeout ?? TimeSpan.FromSeconds(30),
            $"silo {siloAddress} to start joining");
    }

    /// <summary>
    /// Checks if a specific silo has been declared dead.
    /// </summary>
    /// <param name="siloAddress">The silo address to check.</param>
    /// <returns>True if the silo has been declared dead.</returns>
    public bool HasSiloDeclaredDead(SiloAddress siloAddress)
    {
        return _statusChangedEvents.Any(e => e.NewEntry.SiloAddress.Equals(siloAddress) && e.NewEntry.Status == SiloStatus.Dead);
    }

    /// <summary>
    /// Checks if a specific silo has become active.
    /// </summary>
    /// <param name="siloAddress">The silo address to check.</param>
    /// <returns>True if the silo has become active.</returns>
    public bool HasSiloBecameActive(SiloAddress siloAddress)
    {
        return _statusChangedEvents.Any(e => e.NewEntry.SiloAddress.Equals(siloAddress) && e.NewEntry.Status == SiloStatus.Active);
    }

    /// <summary>
    /// Gets the count of silos declared dead.
    /// </summary>
    /// <returns>The count of dead-status events.</returns>
    public int GetDeclaredDeadCount()
    {
        return _statusChangedEvents.Count(e => e.NewEntry.Status == SiloStatus.Dead);
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
    }

    private async Task<SiloStatusChangedEvent> WaitForStatusChangeAsync(Func<SiloStatusChangedEvent, bool> predicate, TimeSpan timeout, string description)
    {
        using var cts = new CancellationTokenSource(timeout);

        var existingMatch = _statusChangedEvents.FirstOrDefault(predicate);
        if (existingMatch != null)
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

            var match = _statusChangedEvents.FirstOrDefault(predicate);
            if (match != null)
            {
                return match;
            }
        }

        throw new TimeoutException($"Timed out waiting for {description} after {timeout}");
    }

    private IReadOnlyCollection<SiloStatusChangedEvent> GetEventsByStatus(SiloStatus status)
    {
        return _statusChangedEvents.Where(e => e.NewEntry.Status == status).ToArray();
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
