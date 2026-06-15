using System;

namespace Orleans.Reminders.Concurrency;

/// <summary>
/// Static, immutable configuration for a single throttle tier.
/// </summary>
/// <remarks>
/// <para>A configuration must specify at least one of <see cref="MaxConcurrent"/> or
/// <see cref="PermitsPerSecond"/>. The startup validator rejects empty configurations
/// rather than silently installing a no-op.</para>
/// <para>Construct via <see cref="ReminderThrottleConfigBuilder"/> for compile-time-friendly
/// chaining; direct construction is supported for advanced scenarios.</para>
/// </remarks>
public sealed class ThrottleConfig
{
    internal ThrottleConfig(
        int? maxConcurrent,
        double? permitsPerSecond,
        int? burstSize,
        ThrottleBlockMode blockMode,
        OverloadConfig? overload,
        SlowStartConfig? slowStart)
    {
        if (maxConcurrent is null && permitsPerSecond is null && overload is null)
        {
            throw new ArgumentException("At least one of MaxConcurrent, PermitsPerSecond, or RespectOverload must be specified.");
        }

        if (maxConcurrent is { } mc && mc <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrent), mc, "MaxConcurrent must be greater than zero.");
        }

        if (permitsPerSecond is { } pps && !(pps > 0 && double.IsFinite(pps)))
        {
            throw new ArgumentOutOfRangeException(nameof(permitsPerSecond), pps, "PermitsPerSecond must be greater than zero and finite.");
        }

        if (burstSize is { } bs && bs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(burstSize), bs, "BurstSize must be greater than zero.");
        }

        if (permitsPerSecond is null && burstSize is not null)
        {
            throw new ArgumentException("BurstSize requires PermitsPerSecond to be specified.");
        }

        if (slowStart is not null)
        {
            if (maxConcurrent is null)
            {
                throw new ArgumentException("SlowStart requires MaxConcurrent to be specified (slow-start ramps from its initial capacity up to MaxConcurrent).");
            }

            if (slowStart.InitialCapacity > maxConcurrent.Value)
            {
                throw new ArgumentException($"SlowStart.InitialCapacity ({slowStart.InitialCapacity}) cannot exceed MaxConcurrent ({maxConcurrent.Value}).");
            }
        }

        MaxConcurrent = maxConcurrent;
        PermitsPerSecond = permitsPerSecond;
        BurstSize = burstSize ?? (permitsPerSecond is { } p ? Math.Max(1, (int)Math.Ceiling(p)) : null);
        BlockMode = blockMode ?? throw new ArgumentNullException(nameof(blockMode));
        Overload = overload;
        SlowStart = slowStart;
    }

    /// <summary>The maximum number of in-flight dispatches permitted by this tier, or <c>null</c> for unbounded.</summary>
    public int? MaxConcurrent { get; }

    /// <summary>The sustained rate of dispatches permitted by this tier, or <c>null</c> for no rate cap.</summary>
    public double? PermitsPerSecond { get; }

    /// <summary>
    /// The maximum burst size for the token bucket. Auto-derived to roughly one second of
    /// <see cref="PermitsPerSecond"/> when not explicitly set; <c>null</c> when only
    /// <see cref="MaxConcurrent"/> is configured.
    /// </summary>
    public int? BurstSize { get; }

    /// <summary>How the tier behaves when an acquire cannot be admitted immediately.</summary>
    public ThrottleBlockMode BlockMode { get; }

    /// <summary>Optional silo-overload backoff configuration, or <c>null</c> if disabled.</summary>
    public OverloadConfig? Overload { get; }

    /// <summary>Optional slow-start ramp-up configuration, or <c>null</c> if disabled.</summary>
    public SlowStartConfig? SlowStart { get; }
}

/// <summary>
/// Fluent builder for <see cref="ThrottleConfig"/>. Designed so that incomplete configurations
/// are rejected at <see cref="Build"/> time and the resulting <see cref="ThrottleConfig"/>
/// is always self-consistent.
/// </summary>
public sealed class ReminderThrottleConfigBuilder
{
    private int? _maxConcurrent;
    private double? _permitsPerSecond;
    private int? _burstSize;
    private ThrottleBlockMode _blockMode = ThrottleBlockMode.Wait;
    private OverloadConfig? _overload;
    private SlowStartConfig? _slowStart;

    /// <summary>Caps the in-flight dispatches admitted by this tier.</summary>
    /// <param name="value">A positive concurrency limit.</param>
    public ReminderThrottleConfigBuilder MaxConcurrent(int value)
    {
        _maxConcurrent = value;
        return this;
    }

    /// <summary>Caps the sustained rate of dispatches admitted by this tier.</summary>
    /// <param name="value">A positive, finite permits-per-second rate.</param>
    public ReminderThrottleConfigBuilder PermitsPerSecond(double value)
    {
        _permitsPerSecond = value;
        return this;
    }

    /// <summary>
    /// Overrides the auto-derived burst size for the token bucket. Only required for
    /// workloads with unusual burst characteristics; most users should leave this unset.
    /// </summary>
    /// <param name="value">A positive burst capacity.</param>
    public ReminderThrottleConfigBuilder BurstSize(int value)
    {
        _burstSize = value;
        return this;
    }

    /// <summary>
    /// Selects the behavior used when an acquire cannot be admitted immediately. Defaults
    /// to <see cref="ThrottleBlockMode.Wait"/>.
    /// </summary>
    /// <param name="mode">The block mode.</param>
    public ReminderThrottleConfigBuilder BlockMode(ThrottleBlockMode mode)
    {
        _blockMode = mode;
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
    public ThrottleConfig Build() => new(_maxConcurrent, _permitsPerSecond, _burstSize, _blockMode, _overload, _slowStart);
}
