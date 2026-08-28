#nullable enable

using System.Diagnostics.CodeAnalysis;
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
    private readonly IConnectableObservable<ReminderEvents.ReminderServiceEvent> _serviceEvents;
    private readonly IDisposable _connection;
    private readonly IDisposable _serviceConnection;
    private readonly IDisposable _storageSubscription;
    private readonly Dictionary<GrainId, int> _tickCountsByGrain = [];
    private readonly Dictionary<ReminderTickKey, int> _tickCountsByReminder = [];
    private readonly Dictionary<ReminderTickKey, int> _localStartCounts = [];
    private readonly Dictionary<ReminderTickKey, int> _localStopCounts = [];
    private readonly Dictionary<ReminderTickKey, int> _scheduleChangeCounts = [];
    private readonly Dictionary<ReminderTickKey, Dictionary<LocalReminderInstanceKey, LocalReminderInstanceState>> _activeLocalReminders = [];
    private readonly List<TickCountWaiter> _tickCountWaiters = [];
    private readonly List<ActiveReminderCountWaiter> _activeReminderCountWaiters = [];
    private readonly List<LocalReminderScheduleWaiter> _localReminderScheduleWaiters = [];
    private readonly List<GlobalQuiescenceWaiter> _globalQuiescenceWaiters = [];
    private readonly List<ScheduleChangeCountWaiter> _scheduleChangeCountWaiters = [];

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
        _serviceEvents = ReminderEvents.ServiceEvents.Replay();
        _storageSubscription = _events.Subscribe(StoreEvent);
        _connection = _events.Connect();
        _serviceConnection = _serviceEvents.Connect();
    }

    private void StoreEvent(ReminderEvents.ReminderEvent value)
    {
        List<Waiter> ready = [];
        lock (_lock)
        {
            switch (value)
            {
                case ReminderEvents.TickFiring tickFiring:
                    var tickFiringKey = new ReminderTickKey(tickFiring.GrainId, tickFiring.ReminderName);
                    if (_activeLocalReminders.TryGetValue(tickFiringKey, out var firingInstances))
                    {
                        foreach (var instance in firingInstances.Values)
                        {
                            if (Equals(instance.SiloAddress, tickFiring.SiloAddress))
                            {
                                instance.TickAttemptCount++;
                            }
                        }
                    }

                    break;
                case ReminderEvents.TickCompleted tickCompleted:
                    _tickCountsByGrain[tickCompleted.GrainId] = _tickCountsByGrain.GetValueOrDefault(tickCompleted.GrainId) + 1;
                    var reminderKey = new ReminderTickKey(tickCompleted.GrainId, tickCompleted.ReminderName);
                    _tickCountsByReminder[reminderKey] = _tickCountsByReminder.GetValueOrDefault(reminderKey) + 1;
                    ReleaseReadyTickWaiters(ready);
                    break;
                case ReminderEvents.LocalReminderStarted localReminderStarted:
                    var startedKey = new ReminderTickKey(localReminderStarted.GrainId, localReminderStarted.ReminderName);
                    _localStartCounts[startedKey] = _localStartCounts.GetValueOrDefault(startedKey) + 1;
                    if (!_activeLocalReminders.TryGetValue(startedKey, out var startedInstances))
                    {
                        startedInstances = new Dictionary<LocalReminderInstanceKey, LocalReminderInstanceState>();
                        _activeLocalReminders[startedKey] = startedInstances;
                    }

                    startedInstances.TryAdd(
                        new LocalReminderInstanceKey(localReminderStarted.Identity),
                        new LocalReminderInstanceState(localReminderStarted.SiloAddress));
                    ReleaseReadyActiveReminderWaiters(ready);
                    break;
                case ReminderEvents.LocalReminderStopped localReminderStopped:
                    var stoppedKey = new ReminderTickKey(localReminderStopped.GrainId, localReminderStopped.ReminderName);
                    _localStopCounts[stoppedKey] = _localStopCounts.GetValueOrDefault(stoppedKey) + 1;
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
                    ReleaseReadyLocalReminderScheduleWaiters(ready);
                    ReleaseReadyGlobalQuiescenceWaiters(ready);
                    break;
                case ReminderEvents.LocalReminderScheduleChanged localReminderScheduleChanged:
                    var changedKey = new ReminderTickKey(localReminderScheduleChanged.GrainId, localReminderScheduleChanged.ReminderName);
                    _scheduleChangeCounts[changedKey] = _scheduleChangeCounts.GetValueOrDefault(changedKey) + 1;
                    if (TryGetLocalReminderInstance(changedKey, localReminderScheduleChanged.Identity, out var changedInstance))
                    {
                        changedInstance.ScheduleVersion = Math.Max(
                            changedInstance.ScheduleVersion,
                            localReminderScheduleChanged.ScheduleVersion);
                    }

                    ReleaseReadyScheduleChangeWaiters(ready);
                    break;
                case ReminderEvents.LocalReminderTickWaitArmed localReminderTickWaitArmed:
                    var tickWaitArmedKey = new ReminderTickKey(localReminderTickWaitArmed.GrainId, localReminderTickWaitArmed.ReminderName);
                    if (TryGetLocalReminderInstance(tickWaitArmedKey, localReminderTickWaitArmed.Identity, out var armedInstance)
                        && localReminderTickWaitArmed.ScheduleVersion >= armedInstance.ScheduleVersion)
                    {
                        armedInstance.TickWaitArmedVersion = Math.Max(
                            armedInstance.TickWaitArmedVersion,
                            localReminderTickWaitArmed.ScheduleVersion);
                        armedInstance.TickWaitArmedCount++;
                        ReleaseReadyLocalReminderScheduleWaiters(ready);
                    }

                    break;
            }
        }

        foreach (var waiter in ready)
        {
            waiter.Complete();
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
    /// Waits for a reminder service to complete startup.
    /// </summary>
    public Task<ReminderEvents.ReminderServiceStarted> WaitForReminderServiceStartedAsync(CancellationToken cancellationToken, SiloAddress? siloAddress = null)
    {
        return _serviceEvents
            .OfType<ReminderEvents.ReminderServiceStarted>()
            .FirstAsync(e => siloAddress is null || Equals(e.SiloAddress, siloAddress))
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

    /// <summary>Gets the number of local reminder instances started for an identity.</summary>
    public int GetLocalStartCount(GrainId grainId, string reminderName)
    {
        lock (_lock)
        {
            return _localStartCounts.GetValueOrDefault(new ReminderTickKey(grainId, reminderName));
        }
    }

    /// <summary>Gets the number of local reminder instances stopped for an identity.</summary>
    public int GetLocalStopCount(GrainId grainId, string reminderName)
    {
        lock (_lock)
        {
            return _localStopCounts.GetValueOrDefault(new ReminderTickKey(grainId, reminderName));
        }
    }

    /// <summary>Gets the number of local schedule changes for an identity.</summary>
    public int GetScheduleChangeCount(GrainId grainId, string reminderName)
    {
        lock (_lock)
        {
            return _scheduleChangeCounts.GetValueOrDefault(new ReminderTickKey(grainId, reminderName));
        }
    }

    /// <summary>Waits for the requested number of local schedule changes for an identity.</summary>
    public Task WaitForScheduleChangeCountAsync(
        GrainId grainId,
        string reminderName,
        int expectedCount,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedCount);
        ArgumentException.ThrowIfNullOrEmpty(reminderName);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        ScheduleChangeCountWaiter waiter;
        lock (_lock)
        {
            if (_scheduleChangeCounts.GetValueOrDefault(new ReminderTickKey(grainId, reminderName)) >= expectedCount)
            {
                return Task.CompletedTask;
            }

            waiter = new ScheduleChangeCountWaiter(grainId, reminderName, expectedCount);
            _scheduleChangeCountWaiters.Add(waiter);
            RegisterCancellation(waiter, _scheduleChangeCountWaiters, cancellationToken);
        }

        return waiter.TaskSource.Task;
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
    /// Gets the silos which currently host local owners for a reminder.
    /// </summary>
    public SiloAddress[] GetActiveReminderSilos(GrainId grainId, string reminderName)
    {
        ArgumentException.ThrowIfNullOrEmpty(reminderName);

        lock (_lock)
        {
            return _activeLocalReminders.TryGetValue(new ReminderTickKey(grainId, reminderName), out var instances)
                ? instances.Values.Select(instance => instance.SiloAddress).OfType<SiloAddress>().Distinct().ToArray()
                : [];
        }
    }

    /// <summary>
    /// Gets one silo address per active local reminder instance, preserving duplicate instances on the same silo.
    /// </summary>
    public SiloAddress[] GetActiveReminderOwnerSilos(
        GrainId grainId,
        string reminderName,
        Predicate<SiloAddress?>? ownerFilter = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(reminderName);

        lock (_lock)
        {
            return _activeLocalReminders.TryGetValue(new ReminderTickKey(grainId, reminderName), out var instances)
                ? instances.Values
                    .Where(instance => ownerFilter is null || ownerFilter(instance.SiloAddress))
                    .Select(instance => instance.SiloAddress)
                    .OfType<SiloAddress>()
                    .ToArray()
                : [];
        }
    }

    /// <summary>
    /// Waits for a specific number of active local reminder owners for a reminder.
    /// </summary>
    public Task WaitForActiveReminderCountAsync(GrainId grainId, int expectedCount, CancellationToken cancellationToken, string reminderName)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedCount);
        ArgumentException.ThrowIfNullOrEmpty(reminderName);
        return WaitForActiveReminderCountCoreAsync(grainId, expectedCount, reminderName, ownerFilter: null, cancellationToken);
    }

    /// <summary>
    /// Waits for a specific number of active local reminder instances which match <paramref name="ownerFilter"/>.
    /// </summary>
    public Task WaitForActiveReminderCountAsync(
        GrainId grainId,
        int expectedCount,
        CancellationToken cancellationToken,
        string reminderName,
        Predicate<SiloAddress?> ownerFilter)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedCount);
        ArgumentException.ThrowIfNullOrEmpty(reminderName);
        ArgumentNullException.ThrowIfNull(ownerFilter);
        return WaitForActiveReminderCountCoreAsync(
            grainId,
            expectedCount,
            reminderName,
            ownerFilter,
            cancellationToken);
    }

    /// <summary>
    /// Waits until all active local owners have armed the next tick wait for a reminder.
    /// </summary>
    public Task WaitForLocalReminderScheduleAsync(GrainId grainId, string reminderName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(reminderName);
        return WaitForLocalReminderScheduleCoreAsync(grainId, reminderName, cancellationToken);
    }

    /// <summary>
    /// Waits until there are no active local reminder owners for a reminder.
    /// </summary>
    public Task WaitForReminderQuiescenceAsync(GrainId grainId, string reminderName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(reminderName);
        return WaitForActiveReminderCountCoreAsync(grainId, 0, reminderName, ownerFilter: null, cancellationToken);
    }

    /// <summary>
    /// Waits until there are no active local reminders on the specified silos.
    /// </summary>
    public Task WaitForGlobalQuiescenceAsync(
        IReadOnlySet<SiloAddress> siloAddresses,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(siloAddresses);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        GlobalQuiescenceWaiter waiter;
        lock (_lock)
        {
            if (IsGloballyQuiescentCore(siloAddresses))
            {
                return Task.CompletedTask;
            }

            waiter = new(siloAddresses);
            _globalQuiescenceWaiters.Add(waiter);
            RegisterCancellation(waiter, _globalQuiescenceWaiters, cancellationToken);
        }

        return waiter.TaskSource.Task;
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
            RegisterCancellation(waiter, _tickCountWaiters, cancellationToken);
        }

        return waiter.TaskSource.Task;
    }

    private Task WaitForActiveReminderCountCoreAsync(
        GrainId grainId,
        int targetCount,
        string reminderName,
        Predicate<SiloAddress?>? ownerFilter,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        ActiveReminderCountWaiter? waiter;
        lock (_lock)
        {
            if (GetActiveReminderCountCore(grainId, reminderName, ownerFilter) == targetCount)
            {
                return Task.CompletedTask;
            }

            waiter = new ActiveReminderCountWaiter(grainId, reminderName, targetCount, ownerFilter);
            _activeReminderCountWaiters.Add(waiter);
            RegisterCancellation(waiter, _activeReminderCountWaiters, cancellationToken);
        }

        return waiter.TaskSource.Task;
    }

    private Task WaitForLocalReminderScheduleCoreAsync(GrainId grainId, string reminderName, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        LocalReminderScheduleWaiter? waiter;
        lock (_lock)
        {
            if (IsLocalReminderScheduleReadyCore(grainId, reminderName))
            {
                return Task.CompletedTask;
            }

            waiter = new LocalReminderScheduleWaiter(grainId, reminderName);
            _localReminderScheduleWaiters.Add(waiter);
            RegisterCancellation(waiter, _localReminderScheduleWaiters, cancellationToken);
        }

        return waiter.TaskSource.Task;
    }

    private void RegisterCancellation<TWaiter>(TWaiter waiter, List<TWaiter> waiters, CancellationToken cancellationToken)
        where TWaiter : Waiter
    {
        waiter.CancellationRegistration = cancellationToken.Register(static state =>
        {
            var (observer, pendingWaiter, pendingWaiters, token) =
                ((ReminderDiagnosticObserver Observer, TWaiter Waiter, List<TWaiter> Waiters, CancellationToken Token))state!;
            observer.CancelWaiter(pendingWaiter, pendingWaiters, token);
        }, (this, waiter, waiters, cancellationToken));
    }

    private void CancelWaiter<TWaiter>(TWaiter waiter, List<TWaiter> waiters, CancellationToken cancellationToken)
        where TWaiter : Waiter
    {
        lock (_lock)
        {
            waiters.Remove(waiter);
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

    private int GetActiveReminderCountCore(
        GrainId grainId,
        string reminderName,
        Predicate<SiloAddress?>? ownerFilter = null)
    {
        return _activeLocalReminders.TryGetValue(new ReminderTickKey(grainId, reminderName), out var instances)
            ? instances.Values.Count(instance => ownerFilter is null || ownerFilter(instance.SiloAddress))
            : 0;
    }

    private bool IsLocalReminderScheduleReadyCore(GrainId grainId, string reminderName)
    {
        var key = new ReminderTickKey(grainId, reminderName);
        if (GetActiveReminderCountCore(grainId, reminderName) == 0)
        {
            return false;
        }

        foreach (var instance in _activeLocalReminders[key].Values)
        {
            if (instance.TickWaitArmedCount <= instance.TickAttemptCount
                || instance.TickWaitArmedVersion < instance.ScheduleVersion)
            {
                return false;
            }
        }

        return true;
    }

    private bool TryGetLocalReminderInstance(
        ReminderTickKey key,
        object identity,
        [NotNullWhen(true)] out LocalReminderInstanceState? instance)
    {
        if (_activeLocalReminders.TryGetValue(key, out var instances))
        {
            return instances.TryGetValue(new LocalReminderInstanceKey(identity), out instance);
        }

        instance = null;
        return false;
    }

    private void ReleaseReadyTickWaiters(List<Waiter> ready)
    {
        for (var i = _tickCountWaiters.Count - 1; i >= 0; i--)
        {
            var waiter = _tickCountWaiters[i];
            if (GetTickCountCore(waiter.GrainId, waiter.ReminderName) < waiter.TargetCount)
            {
                continue;
            }

            _tickCountWaiters.RemoveAt(i);
            ready.Add(waiter);
        }
    }

    private void ReleaseReadyActiveReminderWaiters(List<Waiter> ready)
    {
        for (var i = _activeReminderCountWaiters.Count - 1; i >= 0; i--)
        {
            var waiter = _activeReminderCountWaiters[i];
            if (GetActiveReminderCountCore(waiter.GrainId, waiter.ReminderName, waiter.OwnerFilter) != waiter.TargetCount)
            {
                continue;
            }

            _activeReminderCountWaiters.RemoveAt(i);
            ready.Add(waiter);
        }
    }

    private void ReleaseReadyLocalReminderScheduleWaiters(List<Waiter> ready)
    {
        for (var i = _localReminderScheduleWaiters.Count - 1; i >= 0; i--)
        {
            var waiter = _localReminderScheduleWaiters[i];
            if (!IsLocalReminderScheduleReadyCore(waiter.GrainId, waiter.ReminderName))
            {
                continue;
            }

            _localReminderScheduleWaiters.RemoveAt(i);
            ready.Add(waiter);
        }
    }

    private void ReleaseReadyGlobalQuiescenceWaiters(List<Waiter> ready)
    {
        for (var i = _globalQuiescenceWaiters.Count - 1; i >= 0; i--)
        {
            var waiter = _globalQuiescenceWaiters[i];
            if (!IsGloballyQuiescentCore(waiter.SiloAddresses))
            {
                continue;
            }

            _globalQuiescenceWaiters.RemoveAt(i);
            ready.Add(waiter);
        }
    }

    private bool IsGloballyQuiescentCore(IReadOnlySet<SiloAddress> siloAddresses)
    {
        return !_activeLocalReminders.Values
            .SelectMany(static instances => instances.Values)
            .Any(instance => instance.SiloAddress is { } address && siloAddresses.Contains(address));
    }

    private void ReleaseReadyScheduleChangeWaiters(List<Waiter> ready)
    {
        for (var i = _scheduleChangeCountWaiters.Count - 1; i >= 0; i--)
        {
            var waiter = _scheduleChangeCountWaiters[i];
            var count = _scheduleChangeCounts.GetValueOrDefault(
                new ReminderTickKey(waiter.GrainId, waiter.ReminderName));
            if (count < waiter.TargetCount)
            {
                continue;
            }

            _scheduleChangeCountWaiters.RemoveAt(i);
            ready.Add(waiter);
        }
    }

    private readonly record struct ReminderTickKey(GrainId GrainId, string ReminderName);
    private readonly record struct LocalReminderInstanceKey(object Identity)
    {
        public bool Equals(LocalReminderInstanceKey other) => ReferenceEquals(Identity, other.Identity);

        public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Identity);
    }

    private sealed class LocalReminderInstanceState(SiloAddress? siloAddress)
    {
        public SiloAddress? SiloAddress { get; } = siloAddress;
        public int TickAttemptCount { get; set; }
        public int TickWaitArmedCount { get; set; }
        public long ScheduleVersion { get; set; }
        public long TickWaitArmedVersion { get; set; } = -1;
    }

    private abstract class Waiter
    {
        public TaskCompletionSource<bool> TaskSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration CancellationRegistration { get; set; }

        public void Complete()
        {
            CancellationRegistration.Dispose();
            TaskSource.TrySetResult(true);
        }
    }

    private sealed class TickCountWaiter(GrainId grainId, string? reminderName, int targetCount) : Waiter
    {
        public GrainId GrainId { get; } = grainId;
        public string? ReminderName { get; } = reminderName;
        public int TargetCount { get; } = targetCount;
    }

    private sealed class ActiveReminderCountWaiter(
        GrainId grainId,
        string reminderName,
        int targetCount,
        Predicate<SiloAddress?>? ownerFilter) : Waiter
    {
        public GrainId GrainId { get; } = grainId;
        public string ReminderName { get; } = reminderName;
        public int TargetCount { get; } = targetCount;
        public Predicate<SiloAddress?>? OwnerFilter { get; } = ownerFilter;
    }

    private sealed class LocalReminderScheduleWaiter(GrainId grainId, string reminderName) : Waiter
    {
        public GrainId GrainId { get; } = grainId;
        public string ReminderName { get; } = reminderName;
    }

    private sealed class GlobalQuiescenceWaiter(IReadOnlySet<SiloAddress> siloAddresses) : Waiter
    {
        public IReadOnlySet<SiloAddress> SiloAddresses { get; } = siloAddresses.ToHashSet();
    }

    private sealed class ScheduleChangeCountWaiter(
        GrainId grainId,
        string reminderName,
        int targetCount) : Waiter
    {
        public GrainId GrainId { get; } = grainId;
        public string ReminderName { get; } = reminderName;
        public int TargetCount { get; } = targetCount;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _storageSubscription.Dispose();
        _connection.Dispose();
        _serviceConnection.Dispose();
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
    /// Waits until all active local owners have armed the next tick wait for a grain reminder.
    /// </summary>
    public static Task WaitForLocalReminderScheduleAsync(this ReminderDiagnosticObserver observer, IAddressable grain, string reminderName, CancellationToken cancellationToken)
    {
        return observer.WaitForLocalReminderScheduleAsync(grain.GetGrainId(), reminderName, cancellationToken);
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
