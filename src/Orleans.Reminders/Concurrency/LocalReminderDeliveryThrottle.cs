using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime.Messaging;

namespace Orleans.Reminders.Concurrency;

/// <summary>
/// An in-process <see cref="IReminderDeliveryThrottle"/> implementation that bounds reminder
/// dispatch through a configurable pipeline of phases: silo-overload backoff, slow-start
/// capacity ramp-up, in-flight concurrency cap, and sustained-rate cap. Each phase is
/// independently optional; an acquire must pass every configured phase to be admitted.
/// </summary>
/// <remarks>
/// <para>This is the implementation that backs the Per-Silo tier.</para>
/// <para>Phases run in the order: overload &#x2192; slow-start &#x2192; concurrency &#x2192; rate. Earlier phases run
/// first so that an overloaded silo's protection is honored before any local permits or
/// tokens are consumed.</para>
/// <para>Block-mode behavior (<see cref="ThrottleBlockMode.Wait"/> / <see cref="ThrottleBlockMode.WaitUpTo"/> /
/// <see cref="ThrottleBlockMode.SkipImmediately"/>) is applied per phase to give the user full
/// control over each behavior. The overall acquire budget for the concurrency and rate phases
/// (governed by <see cref="ThrottleConfig.BlockMode"/>) is shared across both so that
/// <c>WaitUpTo</c> is honored as a single wall-clock cap on those phases.</para>
/// </remarks>
public sealed class LocalReminderDeliveryThrottle : IReminderDeliveryThrottle, IDisposable
{
    private readonly ThrottleConfig _config;
    private readonly TimeProvider _timeProvider;
    private readonly string _tierName;
    private readonly IOverloadDetector? _overloadDetector;
    private readonly SemaphoreSlim? _concurrencySemaphore;
    private readonly TokenBucket? _rateBucket;
    private readonly SemaphoreSlim? _slowStartSemaphore;
    private readonly CancellationTokenSource? _slowStartStopCts;
    private readonly Task? _slowStartTask;
    private int _slowStartCurrentCapacity;

    /// <summary>
    /// Initializes a new instance with the supplied configuration. Used by tests; production
    /// code should resolve the throttle through DI.
    /// </summary>
    /// <param name="config">The throttle configuration.</param>
    /// <param name="timeProvider">The time provider used for rate calculations and slow-start ramp.</param>
    /// <param name="tierName">A name for this tier reported in observability output.</param>
    /// <param name="overloadDetector">Optional silo overload detector. Required when <see cref="ThrottleConfig.Overload"/> is configured.</param>
    public LocalReminderDeliveryThrottle(ThrottleConfig config, TimeProvider timeProvider, string tierName, IOverloadDetector? overloadDetector = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentException.ThrowIfNullOrEmpty(tierName);

        if (config.Overload is not null && overloadDetector is null)
        {
            throw new ArgumentException(
                "ThrottleConfig.Overload is configured but no IOverloadDetector was supplied. " +
                "Register IOverloadDetector in the silo's service collection or remove the RespectOverload configuration.",
                nameof(overloadDetector));
        }

        _config = config;
        _timeProvider = timeProvider;
        _tierName = tierName;
        _overloadDetector = overloadDetector;

        if (config.MaxConcurrent is { } max)
        {
            _concurrencySemaphore = new SemaphoreSlim(max, max);
        }

        if (config.PermitsPerSecond is { } rate)
        {
            _rateBucket = new TokenBucket(rate, config.BurstSize!.Value, timeProvider);
        }

        if (config.SlowStart is { } slowStart)
        {
            _slowStartCurrentCapacity = slowStart.InitialCapacity;
            _slowStartSemaphore = new SemaphoreSlim(slowStart.InitialCapacity, config.MaxConcurrent!.Value);
            _slowStartStopCts = new CancellationTokenSource();
            // Invoke synchronously rather than via Task.Run so the first Task.Delay registers
            // with the supplied TimeProvider as part of the constructor's synchronous flow.
            // After the first await, continuations resume on the thread pool because the
            // method body uses ConfigureAwait(false). This is important for tests that drive
            // the FakeTimeProvider — without synchronous registration, Advance() can race the
            // ramp-up task's first Task.Delay registration.
            _slowStartTask = SlowStartRampUpAsync(_slowStartStopCts.Token);
        }
    }

    /// <summary>The tier name reported on leases produced by this throttle.</summary>
    public string TierName => _tierName;

    /// <summary>The number of currently available concurrency permits, or <c>int.MaxValue</c> when concurrency is unbounded.</summary>
    public int AvailableConcurrencyPermits => _concurrencySemaphore?.CurrentCount ?? int.MaxValue;

    /// <summary>The current available token count in the rate bucket, or <c>int.MaxValue</c> when rate is unbounded.</summary>
    public int AvailableRateTokens => _rateBucket?.SnapshotAvailable() ?? int.MaxValue;

    /// <summary>The current slow-start capacity (ramps up over time toward <c>MaxConcurrent</c>).</summary>
    public int SlowStartCurrentCapacity => _slowStartSemaphore is not null ? Volatile.Read(ref _slowStartCurrentCapacity) : int.MaxValue;

    /// <inheritdoc />
    public async ValueTask<ReminderDeliveryLease> AcquireAsync(ReminderDeliveryContext context, CancellationToken cancellationToken)
    {
        _ = context;
        cancellationToken.ThrowIfCancellationRequested();

        var startTimestamp = _timeProvider.GetTimestamp();

        // Phase 1: Overload gate. Owns its own block mode so that overload behavior is a
        // deliberate, user-visible choice (the IOverloadDetector signal is cluster-wide and
        // typically warrants different handling than the per-tier concurrency/rate path).
        if (_config.Overload is { } overload)
        {
            var overloadResult = await WaitForOverloadClearAsync(overload, cancellationToken).ConfigureAwait(false);
            if (!overloadResult.Admitted)
            {
                return ReminderDeliveryLease.Skipped(_tierName, ElapsedSince(startTimestamp), overloadResult.SkipReason);
            }
        }

        // Phase 2: Slow-start ramp. Owns its own block mode so that the user explicitly chooses
        // how to behave while capacity is restricted during cold-start (wait for ramp / wait
        // with timeout / skip immediately).
        var slowStartAcquired = false;
        if (_slowStartSemaphore is not null)
        {
            var slowStartResult = await TryAcquireSlowStartAsync(_config.SlowStart!.BlockMode, cancellationToken).ConfigureAwait(false);
            if (!slowStartResult.Admitted)
            {
                return ReminderDeliveryLease.Skipped(_tierName, ElapsedSince(startTimestamp), slowStartResult.SkipReason);
            }

            slowStartAcquired = true;
        }

        // Phases 3 (concurrency) and 4 (rate) share a single budget governed by ThrottleConfig.BlockMode
        // so that ThrottleBlockMode.WaitUpTo is honored as a single wall-clock cap across both.
        var sharedBudget = _config.BlockMode switch
        {
            ThrottleBlockMode.SkipImmediatelyMode => TimeSpan.Zero,
            ThrottleBlockMode.WaitWithTimeout w => w.Timeout,
            _ => Timeout.InfiniteTimeSpan,
        };

        var sharedBudgetStart = _timeProvider.GetTimestamp();
        TimeSpan RemainingSharedBudget()
        {
            if (sharedBudget == Timeout.InfiniteTimeSpan)
            {
                return Timeout.InfiniteTimeSpan;
            }

            if (sharedBudget == TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }

            var remaining = sharedBudget - _timeProvider.GetElapsedTime(sharedBudgetStart);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        var concurrencyAcquired = false;
        if (_concurrencySemaphore is not null)
        {
            AcquireResult concurrencyResult;
            try
            {
                concurrencyResult = await TryAcquireSemaphoreAsync(RemainingSharedBudget(), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (slowStartAcquired) { _slowStartSemaphore!.Release(); }
                throw;
            }

            if (!concurrencyResult.Admitted)
            {
                if (slowStartAcquired) { _slowStartSemaphore!.Release(); }
                return ReminderDeliveryLease.Skipped(_tierName, ElapsedSince(startTimestamp), concurrencyResult.SkipReason);
            }

            concurrencyAcquired = true;
        }

        if (_rateBucket is not null)
        {
            AcquireResult rateResult;
            try
            {
                rateResult = await TryAcquireRateAsync(RemainingSharedBudget(), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (concurrencyAcquired) { _concurrencySemaphore!.Release(); }
                if (slowStartAcquired) { _slowStartSemaphore!.Release(); }
                throw;
            }

            if (!rateResult.Admitted)
            {
                if (concurrencyAcquired) { _concurrencySemaphore!.Release(); }
                if (slowStartAcquired) { _slowStartSemaphore!.Release(); }
                return ReminderDeliveryLease.Skipped(_tierName, ElapsedSince(startTimestamp), rateResult.SkipReason);
            }
        }

        return ReminderDeliveryLease.Admitted(_tierName, ElapsedSince(startTimestamp), CreateReleaseAction(concurrencyAcquired, slowStartAcquired));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _slowStartStopCts?.Cancel();
        try
        {
            _slowStartTask?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }

        _slowStartStopCts?.Dispose();
        _concurrencySemaphore?.Dispose();
        _slowStartSemaphore?.Dispose();
    }

    private Action? CreateReleaseAction(bool concurrencyAcquired, bool slowStartAcquired)
    {
        if (!concurrencyAcquired && !slowStartAcquired)
        {
            return null;
        }

        return () =>
        {
            if (concurrencyAcquired)
            {
                _concurrencySemaphore!.Release();
            }

            if (slowStartAcquired)
            {
                _slowStartSemaphore!.Release();
            }
        };
    }

    private TimeSpan ElapsedSince(long startTimestamp) => _timeProvider.GetElapsedTime(startTimestamp);

    private async ValueTask<AcquireResult> WaitForOverloadClearAsync(OverloadConfig overload, CancellationToken cancellationToken)
    {
        if (!_overloadDetector!.IsOverloaded)
        {
            return AcquireResult.AdmittedResult;
        }

        switch (overload.BlockMode)
        {
            case ThrottleBlockMode.SkipImmediatelyMode:
                return AcquireResult.SkippedResult(ReminderSkipReason.SiloOverloaded);

            case ThrottleBlockMode.WaitWithTimeout w:
                {
                    var deadlineStart = _timeProvider.GetTimestamp();
                    while (_overloadDetector.IsOverloaded)
                    {
                        var elapsed = _timeProvider.GetElapsedTime(deadlineStart);
                        if (elapsed >= w.Timeout)
                        {
                            return AcquireResult.SkippedResult(ReminderSkipReason.SiloOverloaded);
                        }

                        var remaining = w.Timeout - elapsed;
                        var sleepFor = remaining < overload.PollInterval ? remaining : overload.PollInterval;
                        await Task.Delay(sleepFor, _timeProvider, cancellationToken).ConfigureAwait(false);
                    }

                    return AcquireResult.AdmittedResult;
                }

            case ThrottleBlockMode.WaitForever:
            default:
                while (_overloadDetector.IsOverloaded)
                {
                    await Task.Delay(overload.PollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
                }

                return AcquireResult.AdmittedResult;
        }
    }

    private async ValueTask<AcquireResult> TryAcquireSlowStartAsync(ThrottleBlockMode blockMode, CancellationToken cancellationToken)
    {
        if (_slowStartSemaphore!.Wait(0))
        {
            return AcquireResult.AdmittedResult;
        }

        switch (blockMode)
        {
            case ThrottleBlockMode.SkipImmediatelyMode:
                return AcquireResult.SkippedResult(ReminderSkipReason.SlowStartLimited);

            case ThrottleBlockMode.WaitWithTimeout w:
                {
                    using var timeoutCts = new CancellationTokenSource(w.Timeout, _timeProvider);
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                    try
                    {
                        await _slowStartSemaphore.WaitAsync(linkedCts.Token).ConfigureAwait(false);
                        return AcquireResult.AdmittedResult;
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        return AcquireResult.SkippedResult(ReminderSkipReason.SlowStartLimited);
                    }
                }

            case ThrottleBlockMode.WaitForever:
            default:
                await _slowStartSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                return AcquireResult.AdmittedResult;
        }
    }

    private async ValueTask<AcquireResult> TryAcquireSemaphoreAsync(TimeSpan budget, CancellationToken cancellationToken)
    {
        if (_concurrencySemaphore!.Wait(0))
        {
            return AcquireResult.AdmittedResult;
        }

        if (budget == TimeSpan.Zero)
        {
            return AcquireResult.SkippedResult(ReminderSkipReason.LocalLimiterFull);
        }

        if (budget == Timeout.InfiniteTimeSpan)
        {
            await _concurrencySemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return AcquireResult.AdmittedResult;
        }

        var ok = await WaitSemaphoreWithTimeoutAsync(budget, cancellationToken).ConfigureAwait(false);
        return ok ? AcquireResult.AdmittedResult : AcquireResult.SkippedResult(ReminderSkipReason.AcquireTimeout);
    }

    private async ValueTask<bool> WaitSemaphoreWithTimeoutAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        // Use a timeout CTS linked with the caller's cancellation. SemaphoreSlim.WaitAsync's
        // contract guarantees that if cancellation wins the race against grant, no permit is held;
        // if grant wins, no OperationCanceledException is raised and the permit IS held. We
        // distinguish "timeout cancelled us" from "caller cancelled us" by inspecting the caller's
        // token explicitly: if only the timeout fired, swallow the OCE and return false; if the
        // caller cancelled, rethrow.
        using var timeoutCts = new CancellationTokenSource(timeout, _timeProvider);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await _concurrencySemaphore!.WaitAsync(linkedCts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async ValueTask<AcquireResult> TryAcquireRateAsync(TimeSpan budget, CancellationToken cancellationToken)
    {
        var waitFor = _rateBucket!.TryConsumeOrComputeWait();
        if (waitFor == TimeSpan.Zero)
        {
            return AcquireResult.AdmittedResult;
        }

        if (budget == TimeSpan.Zero)
        {
            return AcquireResult.SkippedResult(ReminderSkipReason.LocalLimiterFull);
        }

        if (budget != Timeout.InfiniteTimeSpan && waitFor > budget)
        {
            return AcquireResult.SkippedResult(ReminderSkipReason.AcquireTimeout);
        }

        var startTimestamp = _timeProvider.GetTimestamp();

        while (true)
        {
            await Task.Delay(waitFor, _timeProvider, cancellationToken).ConfigureAwait(false);

            waitFor = _rateBucket.TryConsumeOrComputeWait();
            if (waitFor == TimeSpan.Zero)
            {
                return AcquireResult.AdmittedResult;
            }

            if (budget != Timeout.InfiniteTimeSpan)
            {
                var elapsed = _timeProvider.GetElapsedTime(startTimestamp);
                if (elapsed >= budget || elapsed + waitFor > budget)
                {
                    return AcquireResult.SkippedResult(ReminderSkipReason.AcquireTimeout);
                }
            }
        }
    }

    /// <summary>
    /// Background loop that doubles the slow-start semaphore's capacity every
    /// <see cref="SlowStartConfig.Interval"/> until it reaches <c>MaxConcurrent</c>. Mirrors the
    /// equivalent ramp-up in <c>Orleans.DurableJobs.ShardExecutor</c>.
    /// </summary>
    private async Task SlowStartRampUpAsync(CancellationToken stopToken)
    {
        try
        {
            var slowStart = _config.SlowStart!;
            var targetCapacity = _config.MaxConcurrent!.Value;
            while (true)
            {
                var current = Volatile.Read(ref _slowStartCurrentCapacity);
                if (current >= targetCapacity)
                {
                    return;
                }

                await Task.Delay(slowStart.Interval, _timeProvider, stopToken).ConfigureAwait(false);

                while (true)
                {
                    current = Volatile.Read(ref _slowStartCurrentCapacity);
                    if (current >= targetCapacity)
                    {
                        return;
                    }

                    var newCapacity = (int)Math.Min((long)current * 2, targetCapacity);
                    var toRelease = newCapacity - current;
                    if (toRelease <= 0)
                    {
                        break;
                    }

                    if (Interlocked.CompareExchange(ref _slowStartCurrentCapacity, newCapacity, current) == current)
                    {
                        _slowStartSemaphore!.Release(toRelease);
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
        {
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
