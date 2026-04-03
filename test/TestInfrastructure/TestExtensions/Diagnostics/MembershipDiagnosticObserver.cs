#nullable enable
using System.Collections.Concurrent;
using Orleans.Core.Diagnostics;

namespace TestExtensions;

/// <summary>
/// A test helper that subscribes to Orleans membership diagnostic events and provides
/// methods to wait for specific membership events deterministically.
/// </summary>
/// <remarks>
/// This replaces Task.Delay/Thread.Sleep patterns in tests with event-driven waiting,
/// making tests faster and more reliable.
/// </remarks>
internal sealed class MembershipDiagnosticObserver : IDisposable, IObserver<MembershipEvents.MembershipEvent>
{
    private readonly ConcurrentBag<StatusTransition> _statusTransitions = new();
    private readonly ConcurrentBag<MembershipEvents.ViewChanged> _viewChangedEvents = new();
    private readonly Dictionary<SiloAddress, MembershipTableSnapshot> _lastSnapshotsByObserver = new();
    private readonly object _lockObject = new();
    private MembershipTableSnapshot? _lastSnapshotWithoutObserver;
    private IDisposable? _subscription;

    /// <summary>
    /// Represents a status transition derived from a membership view change.
    /// </summary>
    /// <param name="oldEntry">The previous membership entry for the silo, if one existed.</param>
    /// <param name="newEntry">The new membership entry for the silo.</param>
    /// <param name="observerSiloAddress">The address of the silo that observed this change.</param>
    public sealed class StatusTransition(
        MembershipEntry? oldEntry,
        MembershipEntry newEntry,
        SiloAddress? observerSiloAddress)
    {
        /// <summary>
        /// The previous membership entry for the silo, if one existed.
        /// </summary>
        public readonly MembershipEntry? OldEntry = oldEntry;

        /// <summary>
        /// The new membership entry for the silo.
        /// </summary>
        public readonly MembershipEntry NewEntry = newEntry;

        /// <summary>
        /// The address of the silo that observed this change.
        /// </summary>
        public readonly SiloAddress? ObserverSiloAddress = observerSiloAddress;
    }

    /// <summary>
    /// Gets all derived silo status transitions computed from captured membership view changes.
    /// </summary>
    public IReadOnlyCollection<StatusTransition> StatusTransitions => _statusTransitions.ToArray();

    /// <summary>
    /// Gets all captured membership view changed events.
    /// </summary>
    public IReadOnlyCollection<MembershipEvents.ViewChanged> ViewChangedEvents => _viewChangedEvents.ToArray();

    /// <summary>
    /// Gets all captured status change events whose new status is <see cref="SiloStatus.Dead"/>.
    /// </summary>
    public IReadOnlyCollection<StatusTransition> DeclaredDeadEvents => GetEventsByStatus(SiloStatus.Dead);

    /// <summary>
    /// Gets all captured status change events whose new status is <see cref="SiloStatus.Active"/>.
    /// </summary>
    public IReadOnlyCollection<StatusTransition> BecameActiveEvents => GetEventsByStatus(SiloStatus.Active);

    /// <summary>
    /// Gets all captured status change events whose new status is <see cref="SiloStatus.Joining"/>.
    /// </summary>
    public IReadOnlyCollection<StatusTransition> JoiningEvents => GetEventsByStatus(SiloStatus.Joining);

    /// <summary>
    /// Creates a new instance of the observer and starts listening for membership diagnostic events.
    /// </summary>
    public static MembershipDiagnosticObserver Create()
    {
        var observer = new MembershipDiagnosticObserver();
        observer._subscription = MembershipEvents.AllEvents.Subscribe(observer);
        return observer;
    }

    private MembershipDiagnosticObserver()
    {
    }

    /// <summary>
    /// Waits for a specific silo to be declared dead.
    /// </summary>
    /// <param name="siloAddress">The silo address to wait for.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <returns>The derived status transition payload.</returns>
    public Task<StatusTransition> WaitForSiloDeclaredDeadAsync(SiloAddress siloAddress, TimeSpan? timeout = null)
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
    /// <returns>The derived status transition payload.</returns>
    public Task<StatusTransition> WaitForAnySiloDeclaredDeadAsync(TimeSpan? timeout = null)
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
    /// <returns>The derived status transition payload.</returns>
    public Task<StatusTransition> WaitForSiloDeclaredDeadAsync(Func<StatusTransition, bool> predicate, TimeSpan? timeout = null)
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
    /// <returns>The derived status transition payload.</returns>
    public Task<StatusTransition> WaitForSiloStatusAsync(SiloAddress siloAddress, string expectedStatus, TimeSpan? timeout = null)
    {
        return WaitForStatusChangeAsync(
            e => e.NewEntry.SiloAddress.Equals(siloAddress)
                && e.NewEntry.Status.ToString().Equals(expectedStatus, StringComparison.OrdinalIgnoreCase),
            timeout ?? TimeSpan.FromSeconds(30),
            $"silo {siloAddress} to change to status '{expectedStatus}'");
    }

    /// <summary>
    /// Waits for a specific silo to become active.
    /// </summary>
    /// <param name="siloAddress">The silo address to wait for.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <returns>The derived status transition payload.</returns>
    public Task<StatusTransition> WaitForSiloBecameActiveAsync(SiloAddress siloAddress, TimeSpan? timeout = null)
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
    /// <returns>The derived status transition payload.</returns>
    public Task<StatusTransition> WaitForAnySiloBecameActiveAsync(TimeSpan? timeout = null)
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
    /// <returns>The derived status transition payload.</returns>
    public Task<StatusTransition> WaitForSiloJoiningAsync(SiloAddress siloAddress, TimeSpan? timeout = null)
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
        return _statusTransitions.Any(e => e.NewEntry.SiloAddress.Equals(siloAddress) && e.NewEntry.Status == SiloStatus.Dead);
    }

    /// <summary>
    /// Checks if a specific silo has become active.
    /// </summary>
    /// <param name="siloAddress">The silo address to check.</param>
    /// <returns>True if the silo has become active.</returns>
    public bool HasSiloBecameActive(SiloAddress siloAddress)
    {
        return _statusTransitions.Any(e => e.NewEntry.SiloAddress.Equals(siloAddress) && e.NewEntry.Status == SiloStatus.Active);
    }

    /// <summary>
    /// Gets the count of silos declared dead.
    /// </summary>
    /// <returns>The count of dead-status events.</returns>
    public int GetDeclaredDeadCount()
    {
        return _statusTransitions.Count(e => e.NewEntry.Status == SiloStatus.Dead);
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
        _statusTransitions.Clear();
        _viewChangedEvents.Clear();

        lock (_lockObject)
        {
            _lastSnapshotsByObserver.Clear();
            _lastSnapshotWithoutObserver = null;
        }
    }

    private async Task<StatusTransition> WaitForStatusChangeAsync(Func<StatusTransition, bool> predicate, TimeSpan timeout, string description)
    {
        using var cts = new CancellationTokenSource(timeout);

        var existingMatch = _statusTransitions.FirstOrDefault(predicate);
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

            var match = _statusTransitions.FirstOrDefault(predicate);
            if (match is not null)
            {
                return match;
            }
        }

        throw new TimeoutException($"Timed out waiting for {description} after {timeout}");
    }

    private IReadOnlyCollection<StatusTransition> GetEventsByStatus(SiloStatus status)
    {
        return _statusTransitions.Where(e => e.NewEntry.Status == status).ToArray();
    }

    private void RecordViewChanged(MembershipEvents.ViewChanged viewChanged)
    {
        StatusTransition[] transitions;
        lock (_lockObject)
        {
            var previousSnapshot = GetPreviousSnapshot(viewChanged.ObserverSiloAddress);
            transitions = DeriveStatusTransitions(previousSnapshot, viewChanged).ToArray();
            SetPreviousSnapshot(viewChanged.ObserverSiloAddress, viewChanged.Snapshot);
        }

        foreach (var transition in transitions)
        {
            _statusTransitions.Add(transition);
        }

        _viewChangedEvents.Add(viewChanged);
    }

    private MembershipTableSnapshot? GetPreviousSnapshot(SiloAddress? observerSiloAddress)
    {
        if (observerSiloAddress is null)
        {
            return _lastSnapshotWithoutObserver;
        }

        _lastSnapshotsByObserver.TryGetValue(observerSiloAddress, out var snapshot);
        return snapshot;
    }

    private void SetPreviousSnapshot(SiloAddress? observerSiloAddress, MembershipTableSnapshot snapshot)
    {
        if (observerSiloAddress is null)
        {
            _lastSnapshotWithoutObserver = snapshot;
            return;
        }

        _lastSnapshotsByObserver[observerSiloAddress] = snapshot;
    }

    private static IEnumerable<StatusTransition> DeriveStatusTransitions(
        MembershipTableSnapshot? previousSnapshot,
        MembershipEvents.ViewChanged viewChanged)
    {
        foreach (var (siloAddress, newEntry) in viewChanged.Snapshot.Entries)
        {
            if (previousSnapshot?.Entries.TryGetValue(siloAddress, out var oldEntry) == true)
            {
                if (oldEntry.Status == newEntry.Status)
                {
                    continue;
                }

                yield return new StatusTransition(oldEntry, newEntry, viewChanged.ObserverSiloAddress);
                continue;
            }

            yield return new StatusTransition(
                oldEntry: null,
                newEntry,
                viewChanged.ObserverSiloAddress);
        }
    }

    void IObserver<MembershipEvents.MembershipEvent>.OnNext(MembershipEvents.MembershipEvent value)
    {
        switch (value)
        {
            case MembershipEvents.ViewChanged viewChanged:
                RecordViewChanged(viewChanged);
                break;
        }
    }

    void IObserver<MembershipEvents.MembershipEvent>.OnError(Exception error)
    {
    }

    void IObserver<MembershipEvents.MembershipEvent>.OnCompleted()
    {
    }

    public void Dispose()
    {
        _subscription?.Dispose();
    }
}
