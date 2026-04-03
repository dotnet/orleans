#nullable enable
using System.Collections.Concurrent;
using Orleans;
using ReminderEvents = Orleans.Reminders.Diagnostics.ReminderEvents;

namespace TestExtensions;

/// <summary>
/// A test helper that subscribes to Orleans reminder diagnostic events and provides
/// methods to wait for reminder ticks deterministically.
/// </summary>
/// <remarks>
/// This replaces Task.Delay/Thread.Sleep patterns in tests with event-driven waiting,
/// making tests faster and more reliable when waiting for grain reminder callbacks.
/// </remarks>
public sealed class ReminderDiagnosticObserver : IDisposable, IObserver<ReminderEvents.ReminderEvent>
{
    private readonly ConcurrentBag<ReminderEvents.Registered> _registeredEvents = new();
    private readonly ConcurrentBag<ReminderEvents.Unregistered> _unregisteredEvents = new();
    private readonly ConcurrentBag<ReminderEvents.TickFiring> _tickFiringEvents = new();
    private readonly ConcurrentBag<ReminderEvents.TickCompleted> _tickCompletedEvents = new();
    private readonly ConcurrentBag<ReminderEvents.TickFailed> _tickFailedEvents = new();
    private IDisposable? _subscription;

    /// <summary>
    /// Gets all captured reminder registered events.
    /// </summary>
    public IReadOnlyCollection<ReminderEvents.Registered> RegisteredEvents => _registeredEvents.ToArray();

    /// <summary>
    /// Gets all captured reminder unregistered events.
    /// </summary>
    public IReadOnlyCollection<ReminderEvents.Unregistered> UnregisteredEvents => _unregisteredEvents.ToArray();

    /// <summary>
    /// Gets all captured reminder tick firing events.
    /// </summary>
    public IReadOnlyCollection<ReminderEvents.TickFiring> TickFiringEvents => _tickFiringEvents.ToArray();

    /// <summary>
    /// Gets all captured reminder tick completed events.
    /// </summary>
    public IReadOnlyCollection<ReminderEvents.TickCompleted> TickCompletedEvents => _tickCompletedEvents.ToArray();

    /// <summary>
    /// Gets all captured reminder tick failed events.
    /// </summary>
    public IReadOnlyCollection<ReminderEvents.TickFailed> TickFailedEvents => _tickFailedEvents.ToArray();

    /// <summary>
    /// Creates a new instance of the observer and starts listening for reminder diagnostic events.
    /// </summary>
    public static ReminderDiagnosticObserver Create()
    {
        var observer = new ReminderDiagnosticObserver();
        observer._subscription = ReminderEvents.AllEvents.Subscribe(observer);
        return observer;
    }

    private ReminderDiagnosticObserver()
    {
    }

    /// <summary>
    /// Waits for a reminder tick to complete on a specific grain.
    /// </summary>
    /// <param name="grainId">The grain ID to wait for.</param>
    /// <param name="reminderName">Optional reminder name to filter by. If null, waits for any reminder tick on the grain.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <returns>The tick completed event payload.</returns>
    public async Task<ReminderEvents.TickCompleted> WaitForReminderTickAsync(GrainId grainId, string? reminderName = null, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource(effectiveTimeout);

        var existingMatch = FindTickCompletedEvent(grainId, reminderName);
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

            var match = FindTickCompletedEvent(grainId, reminderName);
            if (match is not null)
            {
                return match;
            }
        }

        throw new TimeoutException($"Timed out waiting for reminder tick on grain {grainId}{(reminderName is not null ? $" reminder {reminderName}" : "")} after {effectiveTimeout}");
    }

    /// <summary>
    /// Waits for a specific number of reminder ticks to complete on a grain.
    /// </summary>
    /// <param name="grainId">The grain ID to wait for.</param>
    /// <param name="expectedCount">The minimum number of ticks to wait for.</param>
    /// <param name="reminderName">Optional reminder name to filter by. If null, counts ticks from any reminder on the grain.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 60 seconds.</param>
    /// <returns>A task that completes when the expected count is reached.</returns>
    /// <exception cref="TimeoutException">Thrown if the expected count is not reached within the timeout.</exception>
    public async Task WaitForTickCountAsync(GrainId grainId, int expectedCount, string? reminderName = null, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(60);
        using var cts = new CancellationTokenSource(effectiveTimeout);

        while (!cts.Token.IsCancellationRequested)
        {
            var currentCount = GetTickCount(grainId, reminderName);
            if (currentCount >= expectedCount)
            {
                return;
            }

            try
            {
                await Task.Delay(10, cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        var finalCount = GetTickCount(grainId, reminderName);
        if (finalCount >= expectedCount)
        {
            return;
        }

        throw new TimeoutException($"Timed out waiting for {expectedCount} reminder ticks on grain {grainId}{(reminderName is not null ? $" reminder {reminderName}" : "")}. Current count: {finalCount} after {effectiveTimeout}");
    }

    /// <summary>
    /// Waits for a reminder to be registered.
    /// </summary>
    /// <param name="grainId">The grain ID to wait for.</param>
    /// <param name="reminderName">The reminder name to wait for.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <returns>The registered event payload.</returns>
    public async Task<ReminderEvents.Registered> WaitForReminderRegisteredAsync(GrainId grainId, string reminderName, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource(effectiveTimeout);

        var existingMatch = _registeredEvents.FirstOrDefault(e => MatchesReminder(e, grainId, reminderName));
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

            var match = _registeredEvents.FirstOrDefault(e => MatchesReminder(e, grainId, reminderName));
            if (match is not null)
            {
                return match;
            }
        }

        throw new TimeoutException($"Timed out waiting for reminder '{reminderName}' to be registered on grain {grainId} after {effectiveTimeout}");
    }

    /// <summary>
    /// Waits for a reminder to be unregistered.
    /// </summary>
    /// <param name="grainId">The grain ID to wait for.</param>
    /// <param name="reminderName">The reminder name to wait for.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <returns>The unregistered event payload.</returns>
    public async Task<ReminderEvents.Unregistered> WaitForReminderUnregisteredAsync(GrainId grainId, string reminderName, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource(effectiveTimeout);

        var existingMatch = _unregisteredEvents.FirstOrDefault(e => MatchesReminder(e, grainId, reminderName));
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

            var match = _unregisteredEvents.FirstOrDefault(e => MatchesReminder(e, grainId, reminderName));
            if (match is not null)
            {
                return match;
            }
        }

        throw new TimeoutException($"Timed out waiting for reminder '{reminderName}' to be unregistered on grain {grainId} after {effectiveTimeout}");
    }

    /// <summary>
    /// Gets the count of completed reminder ticks for a specific grain.
    /// </summary>
    /// <param name="grainId">The grain ID to filter by.</param>
    /// <param name="reminderName">Optional reminder name to filter by.</param>
    /// <returns>The count of completed reminder tick events.</returns>
    public int GetTickCount(GrainId grainId, string? reminderName = null)
    {
        if (reminderName is not null)
        {
            return _tickCompletedEvents.Count(e => MatchesReminder(e, grainId, reminderName));
        }

        return _tickCompletedEvents.Count(e => e.GrainId == grainId);
    }

    /// <summary>
    /// Gets the count of completed reminder ticks for a specific reminder name across all grains.
    /// </summary>
    /// <param name="reminderName">The reminder name to filter by.</param>
    /// <returns>The count of completed reminder tick events.</returns>
    public int GetTickCountByReminderName(string reminderName)
    {
        return _tickCompletedEvents.Count(e => e.ReminderName == reminderName);
    }

    /// <summary>
    /// Checks if a reminder has ticked on a specific grain.
    /// </summary>
    /// <param name="grainId">The grain ID to check.</param>
    /// <param name="reminderName">Optional reminder name to filter by.</param>
    /// <returns>True if at least one reminder tick has occurred.</returns>
    public bool HasReminderTicked(GrainId grainId, string? reminderName = null)
    {
        return FindTickCompletedEvent(grainId, reminderName) is not null;
    }

    /// <summary>
    /// Clears all captured events.
    /// </summary>
    public void Clear()
    {
        _registeredEvents.Clear();
        _unregisteredEvents.Clear();
        _tickFiringEvents.Clear();
        _tickCompletedEvents.Clear();
        _tickFailedEvents.Clear();
    }

    private ReminderEvents.TickCompleted? FindTickCompletedEvent(GrainId grainId, string? reminderName)
    {
        if (reminderName is not null)
        {
            return _tickCompletedEvents.FirstOrDefault(e => MatchesReminder(e, grainId, reminderName));
        }

        return _tickCompletedEvents.FirstOrDefault(e => e.GrainId == grainId);
    }

    private static bool MatchesReminder(ReminderEvents.ReminderEvent evt, GrainId grainId, string? reminderName)
    {
        return evt.GrainId == grainId
            && (reminderName is null || evt.ReminderName == reminderName);
    }

    void IObserver<ReminderEvents.ReminderEvent>.OnNext(ReminderEvents.ReminderEvent value)
    {
        switch (value)
        {
            case ReminderEvents.Registered registered:
                _registeredEvents.Add(registered);
                break;
            case ReminderEvents.Unregistered unregistered:
                _unregisteredEvents.Add(unregistered);
                break;
            case ReminderEvents.TickFiring tickFiring:
                _tickFiringEvents.Add(tickFiring);
                break;
            case ReminderEvents.TickCompleted tickCompleted:
                _tickCompletedEvents.Add(tickCompleted);
                break;
            case ReminderEvents.TickFailed tickFailed:
                _tickFailedEvents.Add(tickFailed);
                break;
        }
    }

    void IObserver<ReminderEvents.ReminderEvent>.OnError(Exception error)
    {
    }

    void IObserver<ReminderEvents.ReminderEvent>.OnCompleted()
    {
    }

    public void Dispose()
    {
        _subscription?.Dispose();
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
    public static Task<ReminderEvents.TickCompleted> WaitForReminderTickAsync(this ReminderDiagnosticObserver observer, IAddressable grain, string? reminderName = null, TimeSpan? timeout = null)
    {
        return observer.WaitForReminderTickAsync(grain.GetGrainId(), reminderName, timeout);
    }

    /// <summary>
    /// Waits for a specific number of reminder ticks on a grain.
    /// </summary>
    public static Task WaitForTickCountAsync(this ReminderDiagnosticObserver observer, IAddressable grain, int expectedCount, string? reminderName = null, TimeSpan? timeout = null)
    {
        return observer.WaitForTickCountAsync(grain.GetGrainId(), expectedCount, reminderName, timeout);
    }

    /// <summary>
    /// Waits for a reminder to be registered on a grain.
    /// </summary>
    public static Task<ReminderEvents.Registered> WaitForReminderRegisteredAsync(this ReminderDiagnosticObserver observer, IAddressable grain, string reminderName, TimeSpan? timeout = null)
    {
        return observer.WaitForReminderRegisteredAsync(grain.GetGrainId(), reminderName, timeout);
    }

    /// <summary>
    /// Waits for a reminder to be unregistered on a grain.
    /// </summary>
    public static Task<ReminderEvents.Unregistered> WaitForReminderUnregisteredAsync(this ReminderDiagnosticObserver observer, IAddressable grain, string reminderName, TimeSpan? timeout = null)
    {
        return observer.WaitForReminderUnregisteredAsync(grain.GetGrainId(), reminderName, timeout);
    }
}
