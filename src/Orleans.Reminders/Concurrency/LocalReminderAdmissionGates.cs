using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime.Messaging;

namespace Orleans.Reminders.Concurrency;

internal interface IReminderAdmissionGate : IDisposable
{
    ValueTask<GateAcquireResult> AcquireAsync(ReminderDeliveryContext context, ReminderAcquireBudget budget, CancellationToken cancellationToken);
}

internal readonly struct GateAcquireResult
{
    public static readonly GateAcquireResult Admitted = new(true, default, null);

    private GateAcquireResult(bool admitted, ReminderSkipReason skipReason, Action? releaseAction)
    {
        AdmittedLease = admitted;
        SkipReason = skipReason;
        ReleaseAction = releaseAction;
    }

    public bool AdmittedLease { get; }

    public ReminderSkipReason SkipReason { get; }

    public Action? ReleaseAction { get; }

    public static GateAcquireResult AdmittedWithRelease(Action releaseAction) => new(true, default, releaseAction);

    public static GateAcquireResult Skipped(ReminderSkipReason reason) => new(false, reason, null);
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

    public ReminderAcquireBudget(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        _startTimestamp = timeProvider.GetTimestamp();
    }

    public TimeSpan Elapsed => _timeProvider.GetElapsedTime(_startTimestamp);

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
    private readonly SemaphoreSlim _semaphore;
    private readonly CancellationTokenSource _stopCts;
    private readonly Task _rampUpTask;
    private readonly int _targetCapacity;
    private int _currentCapacity;

    public SlowStartReminderAdmissionGate(SlowStartConfig config, int targetCapacity, TimeProvider timeProvider)
    {
        _config = config;
        _timeProvider = timeProvider;
        _semaphore = new SemaphoreSlim(config.InitialCapacity, targetCapacity);
        _stopCts = new CancellationTokenSource();
        _targetCapacity = targetCapacity;
        _currentCapacity = config.InitialCapacity;

        // Register the first delay against the supplied TimeProvider before the constructor returns.
        _rampUpTask = SlowStartRampUpAsync(_stopCts.Token);
    }

    public int CurrentCapacity => Volatile.Read(ref _currentCapacity);

    public async ValueTask<GateAcquireResult> AcquireAsync(ReminderDeliveryContext context, ReminderAcquireBudget budget, CancellationToken cancellationToken)
    {
        _ = context;
        if (Volatile.Read(ref _currentCapacity) >= _targetCapacity)
        {
            return GateAcquireResult.Admitted;
        }

        if (_semaphore.Wait(0))
        {
            return GateAcquireResult.AdmittedWithRelease(() => _semaphore.Release());
        }

        var waitBudget = budget.GetWaitBudget(_config.BlockMode);
        if (waitBudget.Duration == TimeSpan.Zero)
        {
            return GateAcquireResult.Skipped(ReminderSkipReason.SlowStartLimited);
        }

        if (waitBudget.Duration == Timeout.InfiniteTimeSpan)
        {
            await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                _semaphore.Release();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return GateAcquireResult.AdmittedWithRelease(() => _semaphore.Release());
        }

        var acquired = await WaitSemaphoreWithTimeoutAsync(waitBudget.Duration, cancellationToken).ConfigureAwait(false);
        if (acquired && cancellationToken.IsCancellationRequested)
        {
            _semaphore.Release();
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (acquired && budget.GetRemainingWaitBudget().TimedOut)
        {
            _semaphore.Release();
            return GateAcquireResult.Skipped(ReminderSkipReason.SlowStartLimited);
        }

        return acquired
            ? GateAcquireResult.AdmittedWithRelease(() => _semaphore.Release())
            : GateAcquireResult.Skipped(ReminderSkipReason.SlowStartLimited);
    }

    public void Dispose()
    {
        _stopCts.Cancel();
        try
        {
            _rampUpTask.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _stopCts.Dispose();
            _semaphore.Dispose();
        }
    }

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

    private async Task SlowStartRampUpAsync(CancellationToken stopToken)
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
                        _semaphore.Release(toRelease);
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
        {
        }
    }
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

    public async ValueTask<GateAcquireResult> AcquireAsync(ReminderDeliveryContext context, ReminderAcquireBudget budget, CancellationToken cancellationToken)
    {
        _ = context;
        if (_semaphore.Wait(0))
        {
            return GateAcquireResult.AdmittedWithRelease(() => _semaphore.Release());
        }

        var waitBudget = budget.GetWaitBudget(_config.BlockMode);
        if (waitBudget.Duration == TimeSpan.Zero)
        {
            return GateAcquireResult.Skipped(waitBudget.TimedOut ? ReminderSkipReason.AcquireTimeout : ReminderSkipReason.LocalLimiterFull);
        }

        if (waitBudget.Duration == Timeout.InfiniteTimeSpan)
        {
            await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                _semaphore.Release();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return GateAcquireResult.AdmittedWithRelease(() => _semaphore.Release());
        }

        var acquired = await WaitSemaphoreWithTimeoutAsync(waitBudget.Duration, cancellationToken).ConfigureAwait(false);
        if (acquired && cancellationToken.IsCancellationRequested)
        {
            _semaphore.Release();
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (acquired && budget.GetRemainingWaitBudget().TimedOut)
        {
            _semaphore.Release();
            return GateAcquireResult.Skipped(ReminderSkipReason.AcquireTimeout);
        }

        return acquired
            ? GateAcquireResult.AdmittedWithRelease(() => _semaphore.Release())
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

    public async ValueTask<GateAcquireResult> AcquireAsync(ReminderDeliveryContext context, ReminderAcquireBudget budget, CancellationToken cancellationToken)
    {
        _ = context;
        var waitFor = _tokenBucket.TryConsumeOrComputeWait(cancellationToken);
        if (waitFor == TimeSpan.Zero)
        {
            return GateAcquireResult.Admitted;
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

            waitFor = _tokenBucket.TryConsumeOrComputeWait(cancellationToken);
            if (waitFor == TimeSpan.Zero)
            {
                return GateAcquireResult.Admitted;
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

        public TimeSpan TryConsumeOrComputeWait(CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Refill();
                if (_tokens >= 1.0)
                {
                    _tokens -= 1.0;
                    return TimeSpan.Zero;
                }

                var missingTokens = 1.0 - _tokens;
                return TimeSpan.FromSeconds(missingTokens / _ratePerSecond);
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
            _tokens = Math.Min(_capacity, _tokens + (elapsed * _ratePerSecond));
            _lastRefillTimestamp = now;
        }
    }
}
