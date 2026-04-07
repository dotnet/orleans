#nullable enable
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using Orleans;
using ReminderEvents = Orleans.Reminders.Diagnostics.ReminderEvents;

namespace TestExtensions;

/// <summary>
/// A test helper that subscribes to Orleans reminder diagnostic events and provides
/// methods to wait for reminder ticks deterministically.
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
    private readonly List<TickCountWaiter> _tickCountWaiters = [];

    /// <summary>
    /// Creates a new instance of the observer and starts listening for reminder diagnostic events.
    /// </summary>
    public static ReminderDiagnosticObserver Create()
    {
        return new ReminderDiagnosticObserver();
    }

    private ReminderDiagnosticObserver()
    {
        _events = ReminderEvents.AllEvents.Replay();
        _storageSubscription = _events.Subscribe(StoreEvent);
        _connection = _events.Connect();
    }

    private void StoreEvent(ReminderEvents.ReminderEvent value)
    {
        if (value is not ReminderEvents.TickCompleted tickCompleted)
        {
            return;
        }

        List<TaskCompletionSource<bool>> ready = [];
        lock (_lock)
        {
            _tickCountsByGrain[tickCompleted.GrainId] = _tickCountsByGrain.GetValueOrDefault(tickCompleted.GrainId) + 1;
            var reminderKey = new ReminderTickKey(tickCompleted.GrainId, tickCompleted.ReminderName);
            _tickCountsByReminder[reminderKey] = _tickCountsByReminder.GetValueOrDefault(reminderKey) + 1;

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

        foreach (var taskSource in ready)
        {
            taskSource.TrySetResult(true);
        }
    }

    /// <summary>
    /// Waits for a reminder tick to complete on a specific grain.
    /// </summary>
    public Task<ReminderEvents.TickCompleted> WaitForReminderTickAsync(GrainId grainId, string? reminderName, CancellationToken cancellationToken)
    {
        return _events
            .OfType<ReminderEvents.TickCompleted>()
            .FirstAsync(e => MatchesReminder(e, grainId, reminderName))
            .ToTask(cancellationToken);
    }

    /// <summary>
    /// Waits for a specific number of reminder ticks to complete on a grain.
    /// </summary>
    public Task WaitForTickCountAsync(GrainId grainId, int expectedCount, string? reminderName, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedCount);
        return WaitForTickCountCoreAsync(grainId, expectedCount, reminderName, cancellationToken);
    }

    /// <summary>
    /// Waits for additional reminder ticks after the current observed count.
    /// </summary>
    public Task WaitForAdditionalTickCountAsync(GrainId grainId, int additionalCount, string? reminderName, CancellationToken cancellationToken)
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
    public async Task WaitForTickConditionAsync(GrainId grainId, Func<CancellationToken, Task<bool>> condition, string? reminderName, CancellationToken cancellationToken)
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

    private async Task WaitAsync(TickCountWaiter waiter, CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(static state =>
        {
            var (observer, pendingWaiter, token) = ((ReminderDiagnosticObserver Observer, TickCountWaiter Waiter, CancellationToken Token))state!;
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

    private int GetTickCountCore(GrainId grainId, string? reminderName)
    {
        if (reminderName is null)
        {
            return _tickCountsByGrain.GetValueOrDefault(grainId);
        }

        return _tickCountsByReminder.GetValueOrDefault(new ReminderTickKey(grainId, reminderName));
    }

    private readonly record struct ReminderTickKey(GrainId GrainId, string ReminderName);

    private sealed class TickCountWaiter(GrainId grainId, string? reminderName, int targetCount)
    {
        public GrainId GrainId { get; } = grainId;
        public string? ReminderName { get; } = reminderName;
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
    public static Task<ReminderEvents.TickCompleted> WaitForReminderTickAsync(this ReminderDiagnosticObserver observer, IAddressable grain, string? reminderName, CancellationToken cancellationToken)
    {
        return observer.WaitForReminderTickAsync(grain.GetGrainId(), reminderName, cancellationToken);
    }

    /// <summary>
    /// Waits for a specific number of reminder ticks on a grain.
    /// </summary>
    public static Task WaitForTickCountAsync(this ReminderDiagnosticObserver observer, IAddressable grain, int expectedCount, string? reminderName, CancellationToken cancellationToken)
    {
        return observer.WaitForTickCountAsync(grain.GetGrainId(), expectedCount, reminderName, cancellationToken);
    }

    /// <summary>
    /// Waits for additional reminder ticks on a grain after the current observed count.
    /// </summary>
    public static Task WaitForAdditionalTickCountAsync(this ReminderDiagnosticObserver observer, IAddressable grain, int additionalCount, string? reminderName, CancellationToken cancellationToken)
    {
        return observer.WaitForAdditionalTickCountAsync(grain.GetGrainId(), additionalCount, reminderName, cancellationToken);
    }

    /// <summary>
    /// Waits until a condition associated with reminder ticks on a grain becomes true.
    /// </summary>
    public static Task WaitForTickConditionAsync(this ReminderDiagnosticObserver observer, IAddressable grain, Func<CancellationToken, Task<bool>> condition, string? reminderName, CancellationToken cancellationToken)
    {
        return observer.WaitForTickConditionAsync(grain.GetGrainId(), condition, reminderName, cancellationToken);
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
