using Orleans.DurableJobs;
using Orleans.Runtime;

namespace Orleans.DurableMessaging;

internal readonly record struct DurableMessagingPumpExecutionKey(string JobName, string JobId, string RunId);

internal readonly record struct DurableMessagingPumpExecution(DurableMessagingPumpExecutionKey Key, long Generation);

internal sealed class DurableMessagingPumpResults
{
    private const int DefaultMaxRetainedEntries = 65_536;
    internal static readonly TimeSpan DefaultRetentionPeriod = TimeSpan.FromMinutes(10);

    private readonly object _lock = new();
    private readonly Dictionary<DurableMessagingPumpExecutionKey, Entry> _entries = [];
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _completedRetentionPeriod;
    private readonly TimeSpan _abandonedRetentionPeriod;
    private readonly TimeSpan _cleanupInterval;
    private readonly int _maxRetainedEntries;
    private DateTimeOffset _nextCleanup;
    private long _generation;

    internal DurableMessagingPumpResults()
        : this(TimeProvider.System, DefaultRetentionPeriod, DefaultRetentionPeriod, DefaultMaxRetainedEntries)
    {
    }

    internal DurableMessagingPumpResults(
        TimeProvider timeProvider,
        TimeSpan completedRetentionPeriod,
        TimeSpan abandonedRetentionPeriod,
        int maxRetainedEntries)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(completedRetentionPeriod, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(abandonedRetentionPeriod, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRetainedEntries);

        _timeProvider = timeProvider;
        _completedRetentionPeriod = completedRetentionPeriod;
        _abandonedRetentionPeriod = abandonedRetentionPeriod;
        _maxRetainedEntries = maxRetainedEntries;
        _cleanupInterval = TimeSpan.FromTicks(Math.Max(
            TimeSpan.FromSeconds(1).Ticks,
            Math.Min(TimeSpan.FromMinutes(1).Ticks, Math.Min(completedRetentionPeriod.Ticks, abandonedRetentionPeriod.Ticks) / 4)));
        _nextCleanup = DurableMessagingTime.AddClamped(timeProvider.GetUtcNow(), _cleanupInterval);
    }

    public bool TryStart(
        DurableMessagingPumpExecutionKey key,
        CancellationToken cancellationToken,
        out DurableMessagingPumpExecution execution)
    {
        List<Entry>? removed;
        lock (_lock)
        {
            var now = _timeProvider.GetUtcNow();
            removed = Prune(now, force: _entries.Count >= _maxRetainedEntries);
            if (_entries.ContainsKey(key))
            {
                execution = default;
            }
            else
            {
                if (_entries.Count >= _maxRetainedEntries)
                {
                    var candidate = _entries
                        .Where(static pair => pair.Value.State != EntryState.Running)
                        .OrderBy(static pair => pair.Value.State == EntryState.Completed ? pair.Value.CompletedAt : pair.Value.CreatedAt)
                        .FirstOrDefault();
                    if (candidate.Value is not null && _entries.Remove(candidate.Key))
                    {
                        (removed ??= []).Add(candidate.Value);
                    }
                }

                if (_entries.Count >= _maxRetainedEntries)
                {
                    execution = default;
                }
                else
                {
                    execution = new(key, ++_generation);
                    _entries.Add(key, new Entry(execution.Generation, now));
                }
            }
        }

        DisposeRegistrations(removed);
        if (execution == default)
        {
            return false;
        }

        if (!cancellationToken.CanBeCanceled)
        {
            return true;
        }

        var registration = cancellationToken.UnsafeRegister(
            static state =>
            {
                var cancellation = (CancellationState)state!;
                cancellation.Owner.CancelWaiting(cancellation.Execution, cancellation.Token);
            },
            new CancellationState(this, execution, cancellationToken));

        var disposeRegistration = false;
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var current) && current.Generation == execution.Generation)
            {
                current.CancellationRegistration = registration;
            }
            else
            {
                disposeRegistration = true;
            }
        }

        if (disposeRegistration)
        {
            registration.Dispose();
        }

        return true;
    }

    public bool TryBegin(DurableMessagingPumpExecution execution)
    {
        CancellationTokenRegistration registration = default;
        lock (_lock)
        {
            if (!_entries.TryGetValue(execution.Key, out var entry)
                || entry.Generation != execution.Generation
                || entry.State != EntryState.Waiting)
            {
                return false;
            }

            entry.State = EntryState.Running;
            registration = entry.CancellationRegistration;
            entry.CancellationRegistration = default;
        }

        registration.Dispose();
        return true;
    }

    public void Complete(DurableMessagingPumpExecution execution, DurableJobRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Finish(execution, result, exception: null);
    }

    public void Fail(DurableMessagingPumpExecution execution, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Finish(execution, result: null, exception);
    }

    public bool TryTake(
        DurableMessagingPumpExecutionKey key,
        out DurableJobRunResult? result,
        out Exception? exception)
    {
        Entry? removedEntry = null;
        List<Entry>? pruned;
        lock (_lock)
        {
            pruned = Prune(_timeProvider.GetUtcNow(), force: false);
            if (!_entries.TryGetValue(key, out var entry) || entry.State != EntryState.Completed)
            {
                result = null;
                exception = null;
            }
            else
            {
                _entries.Remove(key);
                removedEntry = entry;
                result = entry.Result;
                exception = entry.Exception;
            }
        }

        DisposeRegistrations(pruned);
        if (removedEntry is null)
        {
            return false;
        }

        removedEntry.CancellationRegistration.Dispose();
        return true;
    }

    private void Finish(
        DurableMessagingPumpExecution execution,
        DurableJobRunResult? result,
        Exception? exception)
    {
        List<Entry>? removed;
        lock (_lock)
        {
            var now = _timeProvider.GetUtcNow();
            if (_entries.TryGetValue(execution.Key, out var entry)
                && entry.Generation == execution.Generation
                && entry.State != EntryState.Completed)
            {
                entry.Result = result;
                entry.Exception = exception;
                entry.State = EntryState.Completed;
                entry.CompletedAt = now;
            }

            removed = Prune(now, force: _entries.Count > _maxRetainedEntries);
        }

        DisposeRegistrations(removed);
    }

    private void CancelWaiting(DurableMessagingPumpExecution execution, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var now = _timeProvider.GetUtcNow();
            if (_entries.TryGetValue(execution.Key, out var entry)
                && entry.Generation == execution.Generation
                && entry.State == EntryState.Waiting)
            {
                entry.Exception = new OperationCanceledException(cancellationToken);
                entry.State = EntryState.Completed;
                entry.CompletedAt = now;
            }
        }
    }

    private List<Entry>? Prune(DateTimeOffset now, bool force)
    {
        if (!force && now < _nextCleanup)
        {
            return null;
        }

        _nextCleanup = DurableMessagingTime.AddClamped(now, _cleanupInterval);
        List<Entry>? removed = null;
        foreach (var pair in _entries.ToArray())
        {
            var entry = pair.Value;
            var expired = entry.State switch
            {
                EntryState.Completed => now - entry.CompletedAt >= _completedRetentionPeriod,
                EntryState.Waiting => now - entry.CreatedAt >= _abandonedRetentionPeriod,
                _ => false
            };
            if (expired && _entries.Remove(pair.Key))
            {
                (removed ??= []).Add(entry);
            }
        }

        if (_entries.Count <= _maxRetainedEntries)
        {
            return removed;
        }

        foreach (var pair in _entries
            .Where(static pair => pair.Value.State != EntryState.Running)
            .OrderBy(static pair => pair.Value.State == EntryState.Completed ? pair.Value.CompletedAt : pair.Value.CreatedAt)
            .ToArray())
        {
            if (_entries.Count <= _maxRetainedEntries)
            {
                break;
            }

            if (_entries.Remove(pair.Key))
            {
                (removed ??= []).Add(pair.Value);
            }
        }

        return removed;
    }

    private static void DisposeRegistrations(List<Entry>? entries)
    {
        if (entries is null)
        {
            return;
        }

        foreach (var entry in entries)
        {
            entry.CancellationRegistration.Dispose();
        }
    }

    private sealed class Entry(long generation, DateTimeOffset createdAt)
    {
        public long Generation { get; } = generation;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public DateTimeOffset CompletedAt { get; set; }
        public EntryState State { get; set; }
        public DurableJobRunResult? Result { get; set; }
        public Exception? Exception { get; set; }
        public CancellationTokenRegistration CancellationRegistration { get; set; }
    }

    private sealed record CancellationState(
        DurableMessagingPumpResults Owner,
        DurableMessagingPumpExecution Execution,
        CancellationToken Token);

    private enum EntryState
    {
        Waiting,
        Running,
        Completed
    }
}

internal sealed class OneShotTimerHandle
{
    private readonly object _lock = new();
    private IGrainTimer? _timer;
    private bool _completed;

    public void Attach(IGrainTimer timer)
    {
        lock (_lock)
        {
            if (_completed)
            {
                timer.Dispose();
            }
            else
            {
                _timer = timer;
            }
        }
    }

    public void Complete()
    {
        lock (_lock)
        {
            _completed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
