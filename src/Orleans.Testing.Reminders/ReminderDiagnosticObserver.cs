#nullable enable

using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using Orleans;
using Orleans.Internal;
using Orleans.Runtime;
using ReminderEvents = Orleans.Reminders.Diagnostics.ReminderEvents;

namespace Orleans.Testing.Reminders;

/// <summary>
/// A reminder-test helper which subscribes to Orleans reminder diagnostic events and provides
/// deterministic wait helpers for reminder activity.
/// </summary>
/// <remarks>
/// Uses <c>System.Reactive</c> operators with <c>Replay()</c> so that <c>WaitFor*</c> methods
/// match against both past and future events with zero-latency, event-driven waiting.
/// </remarks>
public sealed class ReminderDiagnosticObserver : IDisposable
{
    private readonly object _lock = new();
    private readonly IConnectableObservable<ReminderEvents.ReminderEvent> _events;
    private readonly IDisposable _connection;
    private readonly IDisposable _storageSubscription;
    private readonly Dictionary<GrainId, int> _tickCountsByGrain = [];
    private readonly Dictionary<ReminderTickKey, int> _tickCountsByReminder = [];
    private readonly Dictionary<ReminderTickKey, HashSet<LocalReminderInstanceKey>> _activeLocalReminders = [];
    private readonly List<TickCountWaiter> _tickCountWaiters = [];
    private readonly List<ActiveReminderCountWaiter> _activeReminderCountWaiters = [];

    /// <summary>
    /// Creates a new instance of the observer and starts listening for reminder diagnostic events.
    /// </summary>
    public static ReminderDiagnosticObserver Create()
    {
        return new ReminderDiagnosticObserver();
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ReminderDiagnosticObserver"/> class.
    /// </summary>
    public ReminderDiagnosticObserver()
    {
        _events = ReminderEvents.AllEvents.Replay();
        _storageSubscription = _events.Subscribe(StoreEvent);
        _connection = _events.Connect();
    }

    private void StoreEvent(ReminderEvents.ReminderEvent value)
    {
        List<TaskCompletionSource<bool>> ready = [];
        lock (_lock)
        {
            switch (value)
            {
                case ReminderEvents.TickCompleted tickCompleted:
                    _tickCountsByGrain[tickCompleted.GrainId] = _tickCountsByGrain.GetValueOrDefault(tickCompleted.GrainId) + 1;
                    var reminderKey = new ReminderTickKey(tickCompleted.GrainId, tickCompleted.ReminderName);
                    _tickCountsByReminder[reminderKey] = _tickCountsByReminder.GetValueOrDefault(reminderKey) + 1;
                    ReleaseReadyTickWaiters(ready);
                    break;
                case ReminderEvents.LocalReminderStarted localReminderStarted:
                    var startedKey = new ReminderTickKey(localReminderStarted.GrainId, localReminderStarted.ReminderName);
                    if (!_activeLocalReminders.TryGetValue(startedKey, out var startedInstances))
                    {
                        startedInstances = [];
                        _activeLocalReminders[startedKey] = startedInstances;
                    }

                    startedInstances.Add(new LocalReminderInstanceKey(
                        localReminderStarted.Identity));
                    ReleaseReadyActiveReminderWaiters(ready);
                    break;
                case ReminderEvents.LocalReminderStopped localReminderStopped:
                    var stoppedKey = new ReminderTickKey(localReminderStopped.GrainId, localReminderStopped.ReminderName);
                    if (_activeLocalReminders.TryGetValue(stoppedKey, out var stoppedInstances))
                    {
                        stoppedInstances.Remove(new LocalReminderInstanceKey(
                            localReminderStopped.Identity));

                        if (stoppedInstances.Count == 0)
                        {
                            _activeLocalReminders.Remove(stoppedKey);
                        }
                    }

                    ReleaseReadyActiveReminderWaiters(ready);
                    break;
            }
        }

        foreach (var taskSource in ready)
        {
            taskSource.TrySetResult(true);
        }
    }

    /// <summary>
    /// Waits for a reminder tick to complete on a specific grain.
    /// </summary>
    public Task<ReminderEvents.TickCompleted> WaitForReminderTickAsync(GrainId grainId, CancellationToken cancellationToken, string? reminderName = null)
    {
        return _events
            .OfType<ReminderEvents.TickCompleted>()
            .FirstAsync(e => MatchesReminder(e, grainId, reminderName))
            .ToTask(cancellationToken);
    }

    /// <summary>
    /// Waits for a specific number of reminder ticks to complete on a grain.
    /// </summary>
    public Task WaitForTickCountAsync(GrainId grainId, int expectedCount, CancellationToken cancellationToken, string? reminderName = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedCount);
        return WaitForTickCountCoreAsync(grainId, expectedCount, reminderName, cancellationToken);
    }

    /// <summary>
    /// Waits for additional reminder ticks after the current observed count.
    /// </summary>
    public Task WaitForAdditionalTickCountAsync(GrainId grainId, int additionalCount, CancellationToken cancellationToken, string? reminderName = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(additionalCount);

        int targetCount;
        lock (_lock)
        {
            targetCount = GetTickCountCore(grainId, reminderName) + additionalCount;
        }

        return WaitForTickCountCoreAsync(grainId, targetCount, reminderName, cancellationToken);
    }

    /// <summary>
    /// Waits until a condition associated with reminder ticks becomes true, re-evaluating after each matching tick.
    /// </summary>
    public async Task WaitForTickConditionAsync(GrainId grainId, Func<CancellationToken, Task<bool>> condition, CancellationToken cancellationToken, string? reminderName = null)
    {
        ArgumentNullException.ThrowIfNull(condition);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var nextTickTarget = GetTickCount(grainId, reminderName) + 1;
            if (await condition(cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            await WaitForTickCountCoreAsync(grainId, nextTickTarget, reminderName, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits for a reminder to be registered.
    /// </summary>
    public Task<ReminderEvents.Registered> WaitForReminderRegisteredAsync(GrainId grainId, string reminderName, CancellationToken cancellationToken)
    {
        return _events
            .OfType<ReminderEvents.Registered>()
            .FirstAsync(e => MatchesReminder(e, grainId, reminderName))
            .ToTask(cancellationToken);
    }

    /// <summary>
    /// Waits for a reminder to be unregistered.
    /// </summary>
    public Task<ReminderEvents.Unregistered> WaitForReminderUnregisteredAsync(GrainId grainId, string reminderName, CancellationToken cancellationToken)
    {
        return _events
            .OfType<ReminderEvents.Unregistered>()
            .FirstAsync(e => MatchesReminder(e, grainId, reminderName))
            .ToTask(cancellationToken);
    }

    /// <summary>
    /// Gets the count of completed reminder ticks for a specific grain.
    /// </summary>
    public int GetTickCount(GrainId grainId, string? reminderName = null)
    {
        lock (_lock)
        {
            return GetTickCountCore(grainId, reminderName);
        }
    }

    /// <summary>
    /// Gets the count of active local reminder owners for a specific reminder.
    /// </summary>
    public int GetActiveReminderCount(GrainId grainId, string reminderName)
    {
        ArgumentException.ThrowIfNullOrEmpty(reminderName);

        lock (_lock)
        {
            return GetActiveReminderCountCore(grainId, reminderName);
        }
    }

    /// <summary>
    /// Waits for a specific number of active local reminder owners for a reminder.
    /// </summary>
    public Task WaitForActiveReminderCountAsync(GrainId grainId, int expectedCount, CancellationToken cancellationToken, string reminderName)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedCount);
        ArgumentException.ThrowIfNullOrEmpty(reminderName);
        return WaitForActiveReminderCountCoreAsync(grainId, expectedCount, reminderName, cancellationToken);
    }

    /// <summary>
    /// Waits until there are no active local reminder owners for a reminder.
    /// </summary>
    public Task WaitForReminderQuiescenceAsync(GrainId grainId, string reminderName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(reminderName);
        return WaitForActiveReminderCountCoreAsync(grainId, 0, reminderName, cancellationToken);
    }

    private static bool MatchesReminder(ReminderEvents.ReminderEvent evt, GrainId grainId, string? reminderName)
    {
        return evt.GrainId == grainId
            && (reminderName is null || evt.ReminderName == reminderName);
    }

    private Task WaitForTickCountCoreAsync(GrainId grainId, int targetCount, string? reminderName, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        TickCountWaiter? waiter;
        lock (_lock)
        {
            if (GetTickCountCore(grainId, reminderName) >= targetCount)
            {
                return Task.CompletedTask;
            }

            waiter = new TickCountWaiter(grainId, reminderName, targetCount);
            _tickCountWaiters.Add(waiter);
        }

        return WaitAsync(waiter, cancellationToken);
    }

    private Task WaitForActiveReminderCountCoreAsync(GrainId grainId, int targetCount, string reminderName, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        ActiveReminderCountWaiter? waiter;
        lock (_lock)
        {
            if (GetActiveReminderCountCore(grainId, reminderName) == targetCount)
            {
                return Task.CompletedTask;
            }

            waiter = new ActiveReminderCountWaiter(grainId, reminderName, targetCount);
            _activeReminderCountWaiters.Add(waiter);
        }

        return WaitAsync(waiter, cancellationToken);
    }

    private async Task WaitAsync(TickCountWaiter waiter, CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(static state =>
        {
            var (observer, pendingWaiter, token) = ((ReminderDiagnosticObserver Observer, TickCountWaiter Waiter, CancellationToken Token))state!;
            observer.CancelWaiter(pendingWaiter, token);
        }, (this, waiter, cancellationToken));

        await waiter.TaskSource.Task.ConfigureAwait(false);
    }

    private async Task WaitAsync(ActiveReminderCountWaiter waiter, CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(static state =>
        {
            var (observer, pendingWaiter, token) = ((ReminderDiagnosticObserver Observer, ActiveReminderCountWaiter Waiter, CancellationToken Token))state!;
            observer.CancelWaiter(pendingWaiter, token);
        }, (this, waiter, cancellationToken));

        await waiter.TaskSource.Task.ConfigureAwait(false);
    }

    private void CancelWaiter(TickCountWaiter waiter, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _tickCountWaiters.Remove(waiter);
        }

        waiter.TaskSource.TrySetCanceled(cancellationToken);
    }

    private void CancelWaiter(ActiveReminderCountWaiter waiter, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _activeReminderCountWaiters.Remove(waiter);
        }

        waiter.TaskSource.TrySetCanceled(cancellationToken);
    }

    private int GetTickCountCore(GrainId grainId, string? reminderName)
    {
        if (reminderName is null)
        {
            return _tickCountsByGrain.GetValueOrDefault(grainId);
        }

        return _tickCountsByReminder.GetValueOrDefault(new ReminderTickKey(grainId, reminderName));
    }

    private int GetActiveReminderCountCore(GrainId grainId, string reminderName)
    {
        return _activeLocalReminders.TryGetValue(new ReminderTickKey(grainId, reminderName), out var instances)
            ? instances.Count
            : 0;
    }

    private void ReleaseReadyTickWaiters(List<TaskCompletionSource<bool>> ready)
    {
        for (var i = _tickCountWaiters.Count - 1; i >= 0; i--)
        {
            var waiter = _tickCountWaiters[i];
            if (GetTickCountCore(waiter.GrainId, waiter.ReminderName) < waiter.TargetCount)
            {
                continue;
            }

            _tickCountWaiters.RemoveAt(i);
            ready.Add(waiter.TaskSource);
        }
    }

    private void ReleaseReadyActiveReminderWaiters(List<TaskCompletionSource<bool>> ready)
    {
        for (var i = _activeReminderCountWaiters.Count - 1; i >= 0; i--)
        {
            var waiter = _activeReminderCountWaiters[i];
            if (GetActiveReminderCountCore(waiter.GrainId, waiter.ReminderName) != waiter.TargetCount)
            {
                continue;
            }

            _activeReminderCountWaiters.RemoveAt(i);
            ready.Add(waiter.TaskSource);
        }
    }

    private readonly record struct ReminderTickKey(GrainId GrainId, string ReminderName);
    private readonly record struct LocalReminderInstanceKey(object Identity)
    {
        public bool Equals(LocalReminderInstanceKey other) => ReferenceEquals(Identity, other.Identity);

        public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Identity);
    }

    private sealed class TickCountWaiter(GrainId grainId, string? reminderName, int targetCount)
    {
        public GrainId GrainId { get; } = grainId;
        public string? ReminderName { get; } = reminderName;
        public int TargetCount { get; } = targetCount;
        public TaskCompletionSource<bool> TaskSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ActiveReminderCountWaiter(GrainId grainId, string reminderName, int targetCount)
    {
        public GrainId GrainId { get; } = grainId;
        public string ReminderName { get; } = reminderName;
        public int TargetCount { get; } = targetCount;
        public TaskCompletionSource<bool> TaskSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _storageSubscription.Dispose();
        _connection.Dispose();
    }
}

/// <summary>
/// Extension methods for working with grain references and reminder diagnostic observers.
/// </summary>
public static class ReminderDiagnosticExtensions
{
    /// <summary>
    /// Waits for a reminder tick on a grain.
    /// </summary>
    public static Task<ReminderEvents.TickCompleted> WaitForReminderTickAsync(this ReminderDiagnosticObserver observer, IAddressable grain, CancellationToken cancellationToken, string? reminderName = null)
    {
        return observer.WaitForReminderTickAsync(grain.GetGrainId(), cancellationToken, reminderName);
    }

    /// <summary>
    /// Waits for a specific number of reminder ticks on a grain.
    /// </summary>
    public static Task WaitForTickCountAsync(this ReminderDiagnosticObserver observer, IAddressable grain, int expectedCount, CancellationToken cancellationToken, string? reminderName = null)
    {
        return observer.WaitForTickCountAsync(grain.GetGrainId(), expectedCount, cancellationToken, reminderName);
    }

    /// <summary>
    /// Waits for additional reminder ticks on a grain after the current observed count.
    /// </summary>
    public static Task WaitForAdditionalTickCountAsync(this ReminderDiagnosticObserver observer, IAddressable grain, int additionalCount, CancellationToken cancellationToken, string? reminderName = null)
    {
        return observer.WaitForAdditionalTickCountAsync(grain.GetGrainId(), additionalCount, cancellationToken, reminderName);
    }

    /// <summary>
    /// Waits for a specific number of active local reminder owners for a grain reminder.
    /// </summary>
    public static Task WaitForActiveReminderCountAsync(this ReminderDiagnosticObserver observer, IAddressable grain, int expectedCount, CancellationToken cancellationToken, string reminderName)
    {
        return observer.WaitForActiveReminderCountAsync(grain.GetGrainId(), expectedCount, cancellationToken, reminderName);
    }

    /// <summary>
    /// Waits until a grain reminder has no active local reminder owners.
    /// </summary>
    public static Task WaitForReminderQuiescenceAsync(this ReminderDiagnosticObserver observer, IAddressable grain, string reminderName, CancellationToken cancellationToken)
    {
        return observer.WaitForReminderQuiescenceAsync(grain.GetGrainId(), reminderName, cancellationToken);
    }

    /// <summary>
    /// Waits until a condition associated with reminder ticks on a grain becomes true.
    /// </summary>
    public static Task WaitForTickConditionAsync(this ReminderDiagnosticObserver observer, IAddressable grain, Func<CancellationToken, Task<bool>> condition, CancellationToken cancellationToken, string? reminderName = null)
    {
        return observer.WaitForTickConditionAsync(grain.GetGrainId(), condition, cancellationToken, reminderName);
    }

    /// <summary>
    /// Waits for a reminder to be registered on a grain.
    /// </summary>
    public static Task<ReminderEvents.Registered> WaitForReminderRegisteredAsync(this ReminderDiagnosticObserver observer, IAddressable grain, string reminderName, CancellationToken cancellationToken)
    {
        return observer.WaitForReminderRegisteredAsync(grain.GetGrainId(), reminderName, cancellationToken);
    }

    /// <summary>
    /// Waits for a reminder to be unregistered on a grain.
    /// </summary>
    public static Task<ReminderEvents.Unregistered> WaitForReminderUnregisteredAsync(this ReminderDiagnosticObserver observer, IAddressable grain, string reminderName, CancellationToken cancellationToken)
    {
        return observer.WaitForReminderUnregisteredAsync(grain.GetGrainId(), reminderName, cancellationToken);
    }
}
