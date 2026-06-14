using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Reminders.Concurrency;

/// <summary>
/// An in-process <see cref="IReminderDeliveryThrottle"/> implementation that bounds
/// concurrency with a <see cref="SemaphoreSlim"/> and bounds rate with a token-bucket
/// algorithm driven by an injected <see cref="TimeProvider"/>.
/// </summary>
/// <remarks>
/// <para>This is the implementation that backs the Per-Silo tier.</para>
/// <para>The concurrency and rate components compose by AND: an acquire must obtain both a
/// concurrency permit and a rate token before being admitted. Either component may be
/// disabled by configuring its corresponding option as <c>null</c> in the
/// <see cref="ThrottleConfig"/>.</para>
/// </remarks>
public sealed class LocalReminderDeliveryThrottle : IReminderDeliveryThrottle, IDisposable
{
    private readonly ThrottleConfig _config;
    private readonly TimeProvider _timeProvider;
    private readonly string _tierName;
    private readonly SemaphoreSlim? _concurrencySemaphore;
    private readonly TokenBucket? _rateBucket;

    /// <summary>Initializes a new instance with the supplied configuration and clock.</summary>
    /// <param name="config">The throttle configuration.</param>
    /// <param name="timeProvider">The time provider used for rate calculations.</param>
    /// <param name="tierName">A name for this tier reported in observability output.</param>
    public LocalReminderDeliveryThrottle(ThrottleConfig config, TimeProvider timeProvider, string tierName)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentException.ThrowIfNullOrEmpty(tierName);

        _config = config;
        _timeProvider = timeProvider;
        _tierName = tierName;

        if (config.MaxConcurrent is { } max)
        {
            _concurrencySemaphore = new SemaphoreSlim(max, max);
        }

        if (config.PermitsPerSecond is { } rate)
        {
            _rateBucket = new TokenBucket(rate, config.BurstSize!.Value, timeProvider);
        }
    }

    /// <summary>The tier name reported on leases produced by this throttle.</summary>
    public string TierName => _tierName;

    /// <summary>The number of currently available concurrency permits, or <c>int.MaxValue</c> when concurrency is unbounded.</summary>
    public int AvailableConcurrencyPermits => _concurrencySemaphore?.CurrentCount ?? int.MaxValue;

    /// <summary>The current available token count in the rate bucket, or <c>int.MaxValue</c> when rate is unbounded.</summary>
    public int AvailableRateTokens => _rateBucket?.SnapshotAvailable() ?? int.MaxValue;

    /// <inheritdoc />
    public async ValueTask<ReminderDeliveryLease> AcquireAsync(ReminderDeliveryContext context, CancellationToken cancellationToken)
    {
        _ = context;

        var startTimestamp = _timeProvider.GetTimestamp();

        // Acquire concurrency permit first (cheaper to release than a token bucket entry).
        var concurrencyAcquired = false;
        if (_concurrencySemaphore is not null)
        {
            var concurrencyResult = await TryAcquireSemaphoreAsync(cancellationToken).ConfigureAwait(false);
            if (!concurrencyResult.Admitted)
            {
                return ReminderDeliveryLease.Skipped(_tierName, ElapsedSince(startTimestamp), concurrencyResult.SkipReason);
            }

            concurrencyAcquired = true;
        }

        if (_rateBucket is not null)
        {
            var rateResult = await TryAcquireRateAsync(cancellationToken).ConfigureAwait(false);
            if (!rateResult.Admitted)
            {
                if (concurrencyAcquired)
                {
                    _concurrencySemaphore!.Release();
                }

                return ReminderDeliveryLease.Skipped(_tierName, ElapsedSince(startTimestamp), rateResult.SkipReason);
            }
        }

        return ReminderDeliveryLease.Admitted(_tierName, ElapsedSince(startTimestamp), CreateReleaseAction(concurrencyAcquired));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _concurrencySemaphore?.Dispose();
    }

    private Action? CreateReleaseAction(bool concurrencyAcquired)
    {
        if (!concurrencyAcquired)
        {
            return null;
        }

        return () => _concurrencySemaphore!.Release();
    }

    private TimeSpan ElapsedSince(long startTimestamp) => _timeProvider.GetElapsedTime(startTimestamp);

    private async ValueTask<AcquireResult> TryAcquireSemaphoreAsync(CancellationToken cancellationToken)
    {
        if (_concurrencySemaphore!.Wait(0))
        {
            return AcquireResult.AdmittedResult;
        }

        switch (_config.BlockMode)
        {
            case ThrottleBlockMode.SkipImmediatelyMode:
                return AcquireResult.SkippedResult(ReminderSkipReason.LocalLimiterFull);

            case ThrottleBlockMode.WaitWithTimeout w:
                {
                    var ok = await WaitSemaphoreWithTimeoutAsync(w.Timeout, cancellationToken).ConfigureAwait(false);
                    return ok ? AcquireResult.AdmittedResult : AcquireResult.SkippedResult(ReminderSkipReason.AcquireTimeout);
                }

            case ThrottleBlockMode.WaitForever:
            default:
                await _concurrencySemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                return AcquireResult.AdmittedResult;
        }
    }

    private async ValueTask<bool> WaitSemaphoreWithTimeoutAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delayTask = Task.Delay(timeout, _timeProvider, cts.Token);
        var waitTask = _concurrencySemaphore!.WaitAsync(cts.Token).ContinueWith(_ => true, TaskScheduler.Default);
        var winner = await Task.WhenAny(waitTask, delayTask).ConfigureAwait(false);

        if (winner == delayTask)
        {
            cts.Cancel();
            try { await waitTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            return false;
        }

        cts.Cancel();
        try { await delayTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        // waitTask completed; consult its result.
        try
        {
            return await waitTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async ValueTask<AcquireResult> TryAcquireRateAsync(CancellationToken cancellationToken)
    {
        var waitFor = _rateBucket!.TryConsumeOrComputeWait();
        if (waitFor == TimeSpan.Zero)
        {
            return AcquireResult.AdmittedResult;
        }

        switch (_config.BlockMode)
        {
            case ThrottleBlockMode.SkipImmediatelyMode:
                return AcquireResult.SkippedResult(ReminderSkipReason.LocalLimiterFull);

            case ThrottleBlockMode.WaitWithTimeout w when waitFor > w.Timeout:
                return AcquireResult.SkippedResult(ReminderSkipReason.AcquireTimeout);
        }

        // Wait the bucket-computed delay (capped by timeout if WaitWithTimeout). Then retry.
        var maxWait = _config.BlockMode is ThrottleBlockMode.WaitWithTimeout w2 ? w2.Timeout : Timeout.InfiniteTimeSpan;
        var deadline = maxWait == Timeout.InfiniteTimeSpan ? (long?)null : _timeProvider.GetTimestamp() + (long)(maxWait.TotalSeconds * TimeProvider.System.TimestampFrequency);

        while (true)
        {
            try
            {
                await Task.Delay(waitFor, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }

            waitFor = _rateBucket.TryConsumeOrComputeWait();
            if (waitFor == TimeSpan.Zero)
            {
                return AcquireResult.AdmittedResult;
            }

            if (deadline is { } d)
            {
                var remaining = d - _timeProvider.GetTimestamp();
                if (remaining <= 0 || waitFor.TotalSeconds * TimeProvider.System.TimestampFrequency > remaining)
                {
                    return AcquireResult.SkippedResult(ReminderSkipReason.AcquireTimeout);
                }
            }
        }
    }

    private readonly struct AcquireResult
    {
        public static readonly AcquireResult AdmittedResult = new(true, default);
        public static AcquireResult SkippedResult(ReminderSkipReason reason) => new(false, reason);

        private AcquireResult(bool admitted, ReminderSkipReason reason)
        {
            Admitted = admitted;
            SkipReason = reason;
        }

        public bool Admitted { get; }
        public ReminderSkipReason SkipReason { get; }
    }

    /// <summary>
    /// A token bucket that produces tokens at a configured sustained rate up to a configured
    /// burst size, with the clock provided externally. Thread-safe.
    /// </summary>
    internal sealed class TokenBucket
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

        /// <summary>
        /// Attempts to consume one token. Returns <see cref="TimeSpan.Zero"/> if consumed.
        /// Otherwise returns the wait duration until at least one token will be available.
        /// </summary>
        public TimeSpan TryConsumeOrComputeWait()
        {
            lock (_lock)
            {
                Refill();
                if (_tokens >= 1.0)
                {
                    _tokens -= 1.0;
                    return TimeSpan.Zero;
                }

                var deficit = 1.0 - _tokens;
                return TimeSpan.FromSeconds(deficit / _ratePerSecond);
            }
        }

        private void Refill()
        {
            var now = _timeProvider.GetTimestamp();
            var elapsed = _timeProvider.GetElapsedTime(_lastRefillTimestamp, now);
            if (elapsed > TimeSpan.Zero)
            {
                _tokens = Math.Min(_capacity, _tokens + elapsed.TotalSeconds * _ratePerSecond);
                _lastRefillTimestamp = now;
            }
        }
    }
}
