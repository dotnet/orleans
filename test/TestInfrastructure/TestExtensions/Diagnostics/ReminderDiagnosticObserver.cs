#nullable enable
using System.Collections.Concurrent;
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
    private readonly IConnectableObservable<ReminderEvents.ReminderEvent> _events;
    private readonly IDisposable _connection;
    private readonly IDisposable _storageSubscription;
    private readonly ConcurrentBag<ReminderEvents.TickCompleted> _tickCompletedEvents = new();

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
        if (value is ReminderEvents.TickCompleted e)
        {
            _tickCompletedEvents.Add(e);
        }
    }

    /// <summary>
    /// Waits for a reminder tick to complete on a specific grain.
    /// </summary>
    public Task<ReminderEvents.TickCompleted> WaitForReminderTickAsync(GrainId grainId, string? reminderName = null, CancellationToken cancellationToken = default)
    {
        return _events
            .OfType<ReminderEvents.TickCompleted>()
            .FirstAsync(e => MatchesReminder(e, grainId, reminderName))
            .ToTask(cancellationToken);
    }

    /// <summary>
    /// Waits for a specific number of reminder ticks to complete on a grain.
    /// </summary>
    public async Task WaitForTickCountAsync(GrainId grainId, int expectedCount, string? reminderName = null, CancellationToken cancellationToken = default)
    {
        await _events
            .OfType<ReminderEvents.TickCompleted>()
            .Where(e => MatchesReminder(e, grainId, reminderName))
            .Take(expectedCount)
            .LastOrDefaultAsync()
            .ToTask(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Waits for a reminder to be registered.
    /// </summary>
    public Task<ReminderEvents.Registered> WaitForReminderRegisteredAsync(GrainId grainId, string reminderName, CancellationToken cancellationToken = default)
    {
        return _events
            .OfType<ReminderEvents.Registered>()
            .FirstAsync(e => MatchesReminder(e, grainId, reminderName))
            .ToTask(cancellationToken);
    }

    /// <summary>
    /// Waits for a reminder to be unregistered.
    /// </summary>
    public Task<ReminderEvents.Unregistered> WaitForReminderUnregisteredAsync(GrainId grainId, string reminderName, CancellationToken cancellationToken = default)
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
        if (reminderName is not null)
        {
            return _tickCompletedEvents.Count(e => MatchesReminder(e, grainId, reminderName));
        }

        return _tickCompletedEvents.Count(e => e.GrainId == grainId);
    }

    private static bool MatchesReminder(ReminderEvents.ReminderEvent evt, GrainId grainId, string? reminderName)
    {
        return evt.GrainId == grainId
            && (reminderName is null || evt.ReminderName == reminderName);
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
    public static Task<ReminderEvents.TickCompleted> WaitForReminderTickAsync(this ReminderDiagnosticObserver observer, IAddressable grain, string? reminderName = null, CancellationToken cancellationToken = default)
    {
        return observer.WaitForReminderTickAsync(grain.GetGrainId(), reminderName, cancellationToken);
    }

    /// <summary>
    /// Waits for a specific number of reminder ticks on a grain.
    /// </summary>
    public static Task WaitForTickCountAsync(this ReminderDiagnosticObserver observer, IAddressable grain, int expectedCount, string? reminderName = null, CancellationToken cancellationToken = default)
    {
        return observer.WaitForTickCountAsync(grain.GetGrainId(), expectedCount, reminderName, cancellationToken);
    }

    /// <summary>
    /// Waits for a reminder to be registered on a grain.
    /// </summary>
    public static Task<ReminderEvents.Registered> WaitForReminderRegisteredAsync(this ReminderDiagnosticObserver observer, IAddressable grain, string reminderName, CancellationToken cancellationToken = default)
    {
        return observer.WaitForReminderRegisteredAsync(grain.GetGrainId(), reminderName, cancellationToken);
    }

    /// <summary>
    /// Waits for a reminder to be unregistered on a grain.
    /// </summary>
    public static Task<ReminderEvents.Unregistered> WaitForReminderUnregisteredAsync(this ReminderDiagnosticObserver observer, IAddressable grain, string reminderName, CancellationToken cancellationToken = default)
    {
        return observer.WaitForReminderUnregisteredAsync(grain.GetGrainId(), reminderName, cancellationToken);
    }
}
