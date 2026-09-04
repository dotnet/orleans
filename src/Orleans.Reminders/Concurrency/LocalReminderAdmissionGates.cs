using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime.Messaging;

namespace Orleans.Reminders.Concurrency;

internal interface IReminderAdmissionGate : IDisposable
{
    ThrottleBlockMode BlockMode { get; }

    ValueTask<GateAcquireResult> AcquireAsync(ReminderDeliveryContext context, ReminderAcquireBudget budget, CancellationToken cancellationToken);
}

internal readonly struct GateAcquireResult
{
    public static readonly GateAcquireResult Admitted = new(true, default, null);

    private GateAcquireResult(bool admitted, ReminderSkipReason skipReason, ReminderAdmissionReservation? reservation)
    {
        AdmittedLease = admitted;
        SkipReason = skipReason;
        Reservation = reservation;
    }

    public bool AdmittedLease { get; }

    public ReminderSkipReason SkipReason { get; }

    public ReminderAdmissionReservation? Reservation { get; }

    public static GateAcquireResult Reserved(ReminderAdmissionReservation reservation) => new(true, default, reservation);

    public static GateAcquireResult Skipped(ReminderSkipReason reason) => new(false, reason, null);
}

internal abstract class ReminderAdmissionReservation
{
    private const int Pending = 0;
    private const int Committed = 1;
    private const int RolledBack = 2;
    private int _state;

    public Action? Commit()
    {
        if (Interlocked.CompareExchange(ref _state, Committed, Pending) != Pending)
        {
            throw new InvalidOperationException("The reminder admission reservation is no longer pending.");
        }

        return CommitCore();
    }

    public void Rollback()
    {
        if (Interlocked.CompareExchange(ref _state, RolledBack, Pending) == Pending)
        {
            RollbackCore();
        }
    }

    protected abstract Action? CommitCore();

    protected abstract void RollbackCore();
}

internal sealed class CallbackReminderAdmissionReservation(
    Action rollback,
    Action? releaseAfterCommit,
    Action? commit = null) : ReminderAdmissionReservation
{
    protected override Action? CommitCore()
    {
        commit?.Invoke();
        return releaseAfterCommit;
    }

    protected override void RollbackCore() => rollback();
}

internal enum ReminderAdmissionCommitOutcome
{
    Committed,
    Cancelled,
    TimedOut,
}

internal sealed class ReminderAdmissionTransaction : IDisposable
{
    private const int Pending = 0;
    private const int Committed = 1;
    private const int RolledBack = 2;
    private readonly object _lock = new();
    private readonly CancellationTokenRegistration _cancellationRegistration;
    private List<ReminderAdmissionReservation>? _reservations;
    private int _state;

    public ReminderAdmissionTransaction(CancellationToken cancellationToken)
    {
        _cancellationRegistration = cancellationToken.UnsafeRegister(
            static state => ((ReminderAdmissionTransaction)state!).Rollback(),
            this);
    }

    public bool TryAdd(ReminderAdmissionReservation? reservation)
    {
        lock (_lock)
        {
            if (_state == Pending)
            {
                if (reservation is not null)
                {
                    _reservations ??= new List<ReminderAdmissionReservation>(capacity: 3);
                    _reservations.Add(reservation);
                }

                return true;
            }
        }

        reservation?.Rollback();
        return false;
    }

    public ReminderAdmissionCommitOutcome TryCommit(
        ReminderAcquireBudget budget,
        CancellationToken cancellationToken,
        out List<Action>? releaseActions)
    {
        List<ReminderAdmissionReservation>? reservationsToRollback = null;
        ReminderAdmissionCommitOutcome outcome;
        releaseActions = null;

        lock (_lock)
        {
            if (_state != Pending || cancellationToken.IsCancellationRequested)
            {
                if (_state == Pending)
                {
                    _state = RolledBack;
                    reservationsToRollback = TakeReservations();
                }

                outcome = ReminderAdmissionCommitOutcome.Cancelled;
            }
            else if (budget.IsTimedOut)
            {
                _state = RolledBack;
                reservationsToRollback = TakeReservations();
                outcome = ReminderAdmissionCommitOutcome.TimedOut;
            }
            else
            {
                _state = Committed;
                if (_reservations is { Count: > 0 } reservations)
                {
                    foreach (var reservation in reservations)
                    {
                        if (reservation.Commit() is { } releaseAction)
                        {
                            releaseActions ??= new List<Action>(capacity: reservations.Count);
                            releaseActions.Add(releaseAction);
                        }
                    }

                    _reservations = null;
                }

                outcome = ReminderAdmissionCommitOutcome.Committed;
            }
        }

        Rollback(reservationsToRollback);
        return outcome;
    }

    public void Rollback()
    {
        List<ReminderAdmissionReservation>? reservations;
        lock (_lock)
        {
            if (_state != Pending)
            {
                return;
            }

            _state = RolledBack;
            reservations = TakeReservations();
        }

        Rollback(reservations);
    }

    public void Dispose()
    {
        _cancellationRegistration.Dispose();
        Rollback();
    }

    private List<ReminderAdmissionReservation>? TakeReservations()
    {
        var result = _reservations;
        _reservations = null;
        return result;
    }

    private static void Rollback(List<ReminderAdmissionReservation>? reservations)
    {
        if (reservations is null)
        {
            return;
        }

        for (var i = reservations.Count - 1; i >= 0; i--)
        {
            reservations[i].Rollback();
        }
    }
}

internal readonly struct ReminderWaitBudget
{
    public ReminderWaitBudget(TimeSpan duration, bool timedOut)
    {
        Duration = duration;
        TimedOut = timedOut;
    }

    public TimeSpan Duration { get; }

    public bool TimedOut { get; }
}

internal sealed class ReminderAcquireBudget
{
    private readonly TimeProvider _timeProvider;
    private readonly long _startTimestamp;
    private TimeSpan? _sharedDeadlineFromStart;

    public ReminderAcquireBudget(TimeProvider timeProvider, TimeSpan? timeout)
    {
        _timeProvider = timeProvider;
        _startTimestamp = timeProvider.GetTimestamp();
        _sharedDeadlineFromStart = timeout;
    }

    public TimeSpan Elapsed => _timeProvider.GetElapsedTime(_startTimestamp);

    public bool IsTimedOut => GetRemainingWaitBudget().TimedOut;

    public ReminderWaitBudget GetWaitBudget(ThrottleBlockMode blockMode)
    {
        switch (blockMode)
        {
            case ThrottleBlockMode.SkipImmediatelyMode:
                return new ReminderWaitBudget(TimeSpan.Zero, timedOut: false);

            case ThrottleBlockMode.WaitWithTimeout waitWithTimeout:
                var deadline = SaturatingAdd(Elapsed, waitWithTimeout.Timeout);
                _sharedDeadlineFromStart = _sharedDeadlineFromStart is { } existing && existing <= deadline
                    ? existing
                    : deadline;
                break;
        }

        return GetRemainingWaitBudget();
    }

    public ReminderWaitBudget GetRemainingWaitBudget()
    {
        if (_sharedDeadlineFromStart is not { } deadline)
        {
            return new ReminderWaitBudget(Timeout.InfiniteTimeSpan, timedOut: false);
        }

        var remaining = deadline - Elapsed;
        return remaining > TimeSpan.Zero
            ? new ReminderWaitBudget(remaining, timedOut: false)
            : new ReminderWaitBudget(TimeSpan.Zero, timedOut: true);
    }

    private static TimeSpan SaturatingAdd(TimeSpan left, TimeSpan right)
    {
        if (right >= TimeSpan.Zero && left > TimeSpan.MaxValue - right)
        {
            return TimeSpan.MaxValue;
        }

        if (right < TimeSpan.Zero && left < TimeSpan.MinValue - right)
        {
            return TimeSpan.MinValue;
        }

        return left + right;
    }
}

internal sealed class OverloadReminderAdmissionGate : IReminderAdmissionGate
{
    private readonly OverloadConfig _config;
    private readonly TimeProvider _timeProvider;
    private readonly IOverloadDetector _overloadDetector;

    public OverloadReminderAdmissionGate(OverloadConfig config, TimeProvider timeProvider, IOverloadDetector overloadDetector)
    {
        _config = config;
        _timeProvider = timeProvider;
        _overloadDetector = overloadDetector;
    }

    public ThrottleBlockMode BlockMode => _config.BlockMode;

    public async ValueTask<GateAcquireResult> AcquireAsync(ReminderDeliveryContext context, ReminderAcquireBudget budget, CancellationToken cancellationToken)
    {
        _ = context;
        if (!_overloadDetector.IsOverloaded)
        {
            return GateAcquireResult.Admitted;
        }

        var waitBudget = budget.GetWaitBudget(_config.BlockMode);
        if (waitBudget.Duration == TimeSpan.Zero)
        {
            return GateAcquireResult.Skipped(ReminderSkipReason.SiloOverloaded);
        }

        while (_overloadDetector.IsOverloaded)
        {
            waitBudget = budget.GetRemainingWaitBudget();
            if (waitBudget.Duration == TimeSpan.Zero)
            {
                return GateAcquireResult.Skipped(ReminderSkipReason.SiloOverloaded);
            }

            var sleepFor = waitBudget.Duration == Timeout.InfiniteTimeSpan || waitBudget.Duration > _config.PollInterval
                ? _config.PollInterval
                : waitBudget.Duration;

            await Task.Delay(sleepFor, _timeProvider, cancellationToken).ConfigureAwait(false);
            if (budget.GetRemainingWaitBudget().TimedOut)
            {
                return GateAcquireResult.Skipped(ReminderSkipReason.SiloOverloaded);
            }
        }

        return GateAcquireResult.Admitted;
    }

    public void Dispose()
    {
    }
}

internal sealed class SlowStartReminderAdmissionGate : IReminderAdmissionGate
{
    private readonly SlowStartConfig _config;
    private readonly TimeProvider _timeProvider;
    private readonly int _targetCapacity;
    private readonly object _lifecycleLock = new();
    private SemaphoreSlim _semaphore;
    private CancellationTokenSource? _rampCancellation;
    private Task _rampUpTask = Task.CompletedTask;
    private int _currentCapacity;
    private bool _started;
    private bool _disposed;

    public SlowStartReminderAdmissionGate(SlowStartConfig config, int targetCapacity, TimeProvider timeProvider)
    {
        _config = config;
        _timeProvider = timeProvider;
        _semaphore = new SemaphoreSlim(config.InitialCapacity, targetCapacity);
        _targetCapacity = targetCapacity;
        _currentCapacity = config.InitialCapacity;
    }

    public int CurrentCapacity => Volatile.Read(ref _currentCapacity);

    public ThrottleBlockMode BlockMode => _config.BlockMode;

    public void Start()
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                return;
            }

            _started = true;
            _rampCancellation = new CancellationTokenSource();
            _rampUpTask = SlowStartRampUpAsync(_semaphore, _rampCancellation.Token);
        }
    }

    public void Stop()
    {
        lock (_lifecycleLock)
        {
            if (!_started)
            {
                return;
            }

            _started = false;
            _rampCancellation!.Cancel();
            try
            {
                _rampUpTask.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (_rampCancellation.IsCancellationRequested)
            {
            }
            finally
            {
                _rampCancellation.Dispose();
                _rampCancellation = null;
                _rampUpTask = Task.CompletedTask;
            }

            var previousSemaphore = _semaphore;
            _semaphore = new SemaphoreSlim(_config.InitialCapacity, _targetCapacity);
            Volatile.Write(ref _currentCapacity, _config.InitialCapacity);
            previousSemaphore.Dispose();
        }
    }

    public async ValueTask<GateAcquireResult> AcquireAsync(ReminderDeliveryContext context, ReminderAcquireBudget budget, CancellationToken cancellationToken)
    {
        _ = context;
        if (Volatile.Read(ref _currentCapacity) >= _targetCapacity)
        {
            return GateAcquireResult.Admitted;
        }

        var semaphore = Volatile.Read(ref _semaphore);
        if (semaphore.Wait(0, CancellationToken.None))
        {
            return GateAcquireResult.Reserved(CreateSemaphoreReservation(semaphore));
        }

        var waitBudget = budget.GetWaitBudget(_config.BlockMode);
        if (waitBudget.Duration == TimeSpan.Zero)
        {
            return GateAcquireResult.Skipped(ReminderSkipReason.SlowStartLimited);
        }

        if (waitBudget.Duration == Timeout.InfiniteTimeSpan)
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return GateAcquireResult.Reserved(CreateSemaphoreReservation(semaphore));
        }

        var acquired = await WaitSemaphoreWithTimeoutAsync(semaphore, waitBudget.Duration, cancellationToken).ConfigureAwait(false);
        return acquired
            ? GateAcquireResult.Reserved(CreateSemaphoreReservation(semaphore))
            : GateAcquireResult.Skipped(ReminderSkipReason.SlowStartLimited);
    }

    public void Dispose()
    {
        Stop();
        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _semaphore.Dispose();
        }
    }

    private async ValueTask<bool> WaitSemaphoreWithTimeoutAsync(SemaphoreSlim semaphore, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout, _timeProvider);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await semaphore.WaitAsync(linkedCts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task SlowStartRampUpAsync(SemaphoreSlim semaphore, CancellationToken stopToken)
    {
        try
        {
            while (true)
            {
                var current = Volatile.Read(ref _currentCapacity);
                if (current >= _targetCapacity)
                {
                    return;
                }

                await Task.Delay(_config.Interval, _timeProvider, stopToken).ConfigureAwait(false);

                while (true)
                {
                    current = Volatile.Read(ref _currentCapacity);
                    if (current >= _targetCapacity)
                    {
                        return;
                    }

                    var newCapacity = (int)Math.Min((long)current * 2, _targetCapacity);
                    var toRelease = newCapacity - current;
                    if (toRelease <= 0)
                    {
                        break;
                    }

                    if (Interlocked.CompareExchange(ref _currentCapacity, newCapacity, current) == current)
                    {
                        semaphore.Release(toRelease);
                        break;
                    }
                }
            }

        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
        {
        }
    }

    private static ReminderAdmissionReservation CreateSemaphoreReservation(SemaphoreSlim semaphore)
        => new CallbackReminderAdmissionReservation(
            rollback: () => semaphore.Release(),
            releaseAfterCommit: () => semaphore.Release());
}

internal sealed class LocalConcurrencyReminderAdmissionGate : IReminderAdmissionGate
{
    private readonly LocalConcurrencyLimiterConfig _config;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _semaphore;

    public LocalConcurrencyReminderAdmissionGate(LocalConcurrencyLimiterConfig config, TimeProvider timeProvider)
    {
        _config = config;
        _timeProvider = timeProvider;
        _semaphore = new SemaphoreSlim(config.MaxConcurrent, config.MaxConcurrent);
    }

    public int AvailablePermits => _semaphore.CurrentCount;

    public ThrottleBlockMode BlockMode => _config.BlockMode;

    public async ValueTask<GateAcquireResult> AcquireAsync(ReminderDeliveryContext context, ReminderAcquireBudget budget, CancellationToken cancellationToken)
    {
        _ = context;
        if (_semaphore.Wait(0, CancellationToken.None))
        {
            return GateAcquireResult.Reserved(CreateSemaphoreReservation(_semaphore));
        }

        var waitBudget = budget.GetWaitBudget(_config.BlockMode);
        if (waitBudget.Duration == TimeSpan.Zero)
        {
            return GateAcquireResult.Skipped(waitBudget.TimedOut ? ReminderSkipReason.AcquireTimeout : ReminderSkipReason.LocalLimiterFull);
        }

        if (waitBudget.Duration == Timeout.InfiniteTimeSpan)
        {
            await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return GateAcquireResult.Reserved(CreateSemaphoreReservation(_semaphore));
        }

        var acquired = await WaitSemaphoreWithTimeoutAsync(waitBudget.Duration, cancellationToken).ConfigureAwait(false);
        return acquired
            ? GateAcquireResult.Reserved(CreateSemaphoreReservation(_semaphore))
            : GateAcquireResult.Skipped(ReminderSkipReason.AcquireTimeout);
    }

    public void Dispose() => _semaphore.Dispose();

    private async ValueTask<bool> WaitSemaphoreWithTimeoutAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout, _timeProvider);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await _semaphore.WaitAsync(linkedCts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static ReminderAdmissionReservation CreateSemaphoreReservation(SemaphoreSlim semaphore)
        => new CallbackReminderAdmissionReservation(
            rollback: () => semaphore.Release(),
            releaseAfterCommit: () => semaphore.Release());
}

internal sealed class LocalRateReminderAdmissionGate : IReminderAdmissionGate
{
    private readonly LocalRateLimiterConfig _config;
    private readonly TimeProvider _timeProvider;
    private readonly TokenBucket _tokenBucket;

    public LocalRateReminderAdmissionGate(LocalRateLimiterConfig config, TimeProvider timeProvider)
    {
        _config = config;
        _timeProvider = timeProvider;
        _tokenBucket = new TokenBucket(config.PermitsPerSecond, config.BurstSize, timeProvider);
    }

    public int AvailableTokens => _tokenBucket.SnapshotAvailable();

    public ThrottleBlockMode BlockMode => _config.BlockMode;

    public async ValueTask<GateAcquireResult> AcquireAsync(ReminderDeliveryContext context, ReminderAcquireBudget budget, CancellationToken cancellationToken)
    {
        _ = context;
        if (_tokenBucket.TryReserve(cancellationToken, out var reservation, out var waitFor))
        {
            return GateAcquireResult.Reserved(reservation);
        }

        var waitBudget = budget.GetWaitBudget(_config.BlockMode);
        if (waitBudget.Duration == TimeSpan.Zero)
        {
            return GateAcquireResult.Skipped(waitBudget.TimedOut ? ReminderSkipReason.AcquireTimeout : ReminderSkipReason.LocalLimiterFull);
        }

        while (true)
        {
            if (waitBudget.Duration != Timeout.InfiniteTimeSpan && waitFor > waitBudget.Duration)
            {
                return GateAcquireResult.Skipped(ReminderSkipReason.AcquireTimeout);
            }

            await Task.Delay(waitFor, _timeProvider, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            waitBudget = budget.GetRemainingWaitBudget();
            if (waitBudget.Duration == TimeSpan.Zero)
            {
                return GateAcquireResult.Skipped(ReminderSkipReason.AcquireTimeout);
            }

            if (_tokenBucket.TryReserve(cancellationToken, out reservation, out waitFor))
            {
                return GateAcquireResult.Reserved(reservation);
            }

        }
    }

    public void Dispose()
    {
    }

    /// <summary>
    /// A token bucket that produces tokens at a configured sustained rate up to a configured
    /// burst size, with the clock provided externally. Thread-safe.
    /// </summary>
    private sealed class TokenBucket
    {
        private readonly double _ratePerSecond;
        private readonly double _capacity;
        private readonly TimeProvider _timeProvider;
        private readonly object _lock = new();
        private double _tokens;
        private int _reservedTokens;
        private long _lastRefillTimestamp;

        public TokenBucket(double ratePerSecond, int capacity, TimeProvider timeProvider)
        {
            _ratePerSecond = ratePerSecond;
            _capacity = capacity;
            _timeProvider = timeProvider;
            _tokens = capacity;
            _lastRefillTimestamp = timeProvider.GetTimestamp();
        }

        public int SnapshotAvailable()
        {
            lock (_lock)
            {
                Refill();
                return (int)Math.Floor(_tokens);
            }
        }

        public bool TryReserve(
            CancellationToken cancellationToken,
            out ReminderAdmissionReservation reservation,
            out TimeSpan waitFor)
        {
            lock (_lock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Refill();
                if (_tokens >= 1.0)
                {
                    _tokens -= 1.0;
                    _reservedTokens++;
                    reservation = new CallbackReminderAdmissionReservation(
                        rollback: RestoreToken,
                        releaseAfterCommit: null,
                        commit: CommitToken);
                    waitFor = TimeSpan.Zero;
                    return true;
                }

                var missingTokens = 1.0 - _tokens;
                reservation = null!;
                waitFor = TimeSpan.FromSeconds(missingTokens / _ratePerSecond);
                return false;
            }
        }

        private void RestoreToken()
        {
            lock (_lock)
            {
                Refill();
                _reservedTokens--;
                _tokens = Math.Min(_capacity - _reservedTokens, _tokens + 1.0);
            }
        }

        private void CommitToken()
        {
            lock (_lock)
            {
                Refill();
                _reservedTokens--;
            }
        }

        private void Refill()
        {
            var now = _timeProvider.GetTimestamp();
            if (now == _lastRefillTimestamp)
            {
                return;
            }

            var elapsed = _timeProvider.GetElapsedTime(_lastRefillTimestamp, now).TotalSeconds;
            _tokens = Math.Min(_capacity - _reservedTokens, _tokens + (elapsed * _ratePerSecond));
            _lastRefillTimestamp = now;
        }
    }
}
