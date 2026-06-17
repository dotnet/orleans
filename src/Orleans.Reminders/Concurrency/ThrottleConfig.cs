using System;

namespace Orleans.Reminders.Concurrency;

/// <summary>
/// Static, immutable configuration for a single throttle tier.
/// </summary>
/// <remarks>
/// <para>A configuration must specify at least one active limiter or overload gate. The startup
/// validator rejects empty configurations rather than silently installing a no-op.</para>
/// <para>Construct via <see cref="ReminderThrottleConfigBuilder"/> for compile-time-friendly
/// chaining; direct construction is supported for advanced scenarios.</para>
/// </remarks>
public sealed class ThrottleConfig
{
    internal ThrottleConfig(
        LocalConcurrencyLimiterConfig? concurrency,
        LocalRateLimiterConfig? rate,
        OverloadConfig? overload,
        SlowStartConfig? slowStart)
    {
        if (concurrency is null && rate is null && overload is null)
        {
            throw new ArgumentException("At least one of MaxConcurrent, PermitsPerSecond, or RespectOverload must be specified.");
        }

        if (slowStart is not null)
        {
            if (concurrency is null)
            {
                throw new ArgumentException("SlowStart requires MaxConcurrent to be specified (slow-start ramps from its initial capacity up to MaxConcurrent).");
            }

            if (slowStart.InitialCapacity > concurrency.MaxConcurrent)
            {
                throw new ArgumentException($"SlowStart.InitialCapacity ({slowStart.InitialCapacity}) cannot exceed MaxConcurrent ({concurrency.MaxConcurrent}).");
            }
        }

        Concurrency = concurrency;
        Rate = rate;
        Overload = overload;
        SlowStart = slowStart;
    }

    internal LocalConcurrencyLimiterConfig? Concurrency { get; }

    internal LocalRateLimiterConfig? Rate { get; }

    /// <summary>The maximum number of in-flight dispatches permitted by this tier, or <c>null</c> for unbounded.</summary>
    public int? MaxConcurrent => Concurrency?.MaxConcurrent;

    /// <summary>The sustained rate of dispatches permitted by this tier, or <c>null</c> for no rate cap.</summary>
    public double? PermitsPerSecond => Rate?.PermitsPerSecond;

    /// <summary>
    /// The maximum burst size for the token bucket. Auto-derived to roughly one second of
    /// <see cref="PermitsPerSecond"/> when not explicitly set; <c>null</c> when only
    /// <see cref="MaxConcurrent"/> is configured.
    /// </summary>
    public int? BurstSize => Rate?.BurstSize;

    /// <summary>Optional silo-overload backoff configuration, or <c>null</c> if disabled.</summary>
    public OverloadConfig? Overload { get; }

    /// <summary>Optional slow-start ramp-up configuration, or <c>null</c> if disabled.</summary>
    public SlowStartConfig? SlowStart { get; }
}

internal sealed class LocalConcurrencyLimiterConfig
{
    public LocalConcurrencyLimiterConfig(int maxConcurrent, ThrottleBlockMode blockMode)
    {
        if (maxConcurrent <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrent), maxConcurrent, "MaxConcurrent must be greater than zero.");
        }

        MaxConcurrent = maxConcurrent;
        BlockMode = blockMode ?? throw new ArgumentNullException(nameof(blockMode));
    }

    public int MaxConcurrent { get; }

    public ThrottleBlockMode BlockMode { get; }
}

internal sealed class LocalRateLimiterConfig
{
    public LocalRateLimiterConfig(double permitsPerSecond, int? burstSize, ThrottleBlockMode blockMode)
    {
        if (!(permitsPerSecond > 0 && double.IsFinite(permitsPerSecond)))
        {
            throw new ArgumentOutOfRangeException(nameof(permitsPerSecond), permitsPerSecond, "PermitsPerSecond must be greater than zero and finite.");
        }

        if (burstSize is { } bs && bs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(burstSize), bs, "BurstSize must be greater than zero.");
        }

        PermitsPerSecond = permitsPerSecond;
        BurstSize = burstSize ?? Math.Max(1, (int)Math.Ceiling(permitsPerSecond));
        BlockMode = blockMode ?? throw new ArgumentNullException(nameof(blockMode));
    }

    public double PermitsPerSecond { get; }

    public int BurstSize { get; }

    public ThrottleBlockMode BlockMode { get; }
}

/// <summary>
/// Fluent builder for <see cref="ThrottleConfig"/>. Designed so that incomplete configurations
/// are rejected at <see cref="Build"/> time and the resulting <see cref="ThrottleConfig"/>
/// is always self-consistent.
/// </summary>
public sealed class ReminderThrottleConfigBuilder
{
    private int? _maxConcurrent;
    private ThrottleBlockMode? _maxConcurrentBlockMode;
    private double? _permitsPerSecond;
    private ThrottleBlockMode? _permitsPerSecondBlockMode;
    private int? _burstSize;
    private OverloadConfig? _overload;
    private SlowStartConfig? _slowStart;

    /// <summary>
    /// Caps the in-flight dispatches admitted by this tier and requires an explicit block mode for
    /// the concurrency limiter.
    /// </summary>
    /// <param name="value">A positive concurrency limit.</param>
    /// <param name="blockMode">The block mode for the concurrency limiter.</param>
    public ReminderThrottleConfigBuilder MaxConcurrent(int value, ThrottleBlockMode blockMode)
    {
        _maxConcurrent = value;
        _maxConcurrentBlockMode = blockMode ?? throw new ArgumentNullException(nameof(blockMode));
        return this;
    }

    /// <summary>
    /// Caps the sustained rate of dispatches admitted by this tier, overrides the token bucket's
    /// burst size, and requires an explicit block mode for the rate limiter.
    /// </summary>
    /// <param name="value">A positive, finite permits-per-second rate.</param>
    /// <param name="burstSize">A positive burst capacity.</param>
    /// <param name="blockMode">The block mode for the rate limiter.</param>
    public ReminderThrottleConfigBuilder PermitsPerSecond(double value, int burstSize, ThrottleBlockMode blockMode)
    {
        _permitsPerSecond = value;
        _burstSize = burstSize;
        _permitsPerSecondBlockMode = blockMode ?? throw new ArgumentNullException(nameof(blockMode));
        return this;
    }

    /// <summary>
    /// Opts the throttle in to honoring <see cref="Orleans.Runtime.Messaging.IOverloadDetector"/>. When
    /// the silo is overloaded (CPU/memory pressure exceeding the configured load-shedding
    /// thresholds), reminder dispatch waits, waits with timeout, or skips, according to
    /// <paramref name="onOverload"/>. The choice is required at configuration time to avoid
    /// silent defaults — silo overload behavior is a deliberate decision.
    /// </summary>
    /// <param name="onOverload">The block mode to apply while overload is in effect.</param>
    /// <param name="pollInterval">How often to re-check the overload signal while waiting. Defaults to 1 second.</param>
    public ReminderThrottleConfigBuilder RespectOverload(ThrottleBlockMode onOverload, TimeSpan? pollInterval = null)
    {
        _overload = new OverloadConfig(onOverload, pollInterval ?? TimeSpan.FromSeconds(1));
        return this;
    }

    /// <summary>
    /// Enables slow-start ramp-up of the configured <c>MaxConcurrent</c> capacity. Capacity starts
    /// at <paramref name="initialCapacity"/> and doubles every <paramref name="interval"/> until
    /// it reaches the configured maximum. The choice of <paramref name="onCapacityExceeded"/> is
    /// required at configuration time so that the behavior during ramp-up is an explicit
    /// decision, not a silent default.
    /// </summary>
    /// <param name="initialCapacity">The capacity available immediately after startup. Must be positive and not exceed <c>MaxConcurrent</c>.</param>
    /// <param name="interval">The doubling interval. Must be positive.</param>
    /// <param name="onCapacityExceeded">The block mode used while the ramping capacity (not the full capacity) is exhausted.</param>
    public ReminderThrottleConfigBuilder SlowStart(int initialCapacity, TimeSpan interval, ThrottleBlockMode onCapacityExceeded)
    {
        _slowStart = new SlowStartConfig(initialCapacity, interval, onCapacityExceeded);
        return this;
    }

    /// <summary>Builds the configured <see cref="ThrottleConfig"/>.</summary>
    /// <exception cref="ArgumentException">Required combinations of options are not met.</exception>
    public ThrottleConfig Build()
    {
        LocalConcurrencyLimiterConfig? concurrency = null;
        if (_maxConcurrent is { } maxConcurrent)
        {
            concurrency = new LocalConcurrencyLimiterConfig(
                maxConcurrent,
                _maxConcurrentBlockMode ?? throw new InvalidOperationException("MaxConcurrent requires an explicit block mode."));
        }

        LocalRateLimiterConfig? rate = null;
        if (_permitsPerSecond is { } permitsPerSecond)
        {
            rate = new LocalRateLimiterConfig(
                permitsPerSecond,
                _burstSize ?? throw new InvalidOperationException("PermitsPerSecond requires an explicit burst size."),
                _permitsPerSecondBlockMode ?? throw new InvalidOperationException("PermitsPerSecond requires an explicit block mode."));
        }

        return new ThrottleConfig(concurrency, rate, _overload, _slowStart);
    }
}
