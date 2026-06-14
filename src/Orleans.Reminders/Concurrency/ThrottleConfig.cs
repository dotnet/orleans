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
        ThrottleBlockMode blockMode)
    {
        if (maxConcurrent is null && permitsPerSecond is null)
        {
            throw new ArgumentException("At least one of MaxConcurrent or PermitsPerSecond must be specified.");
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

        MaxConcurrent = maxConcurrent;
        PermitsPerSecond = permitsPerSecond;
        BurstSize = burstSize ?? (permitsPerSecond is { } p ? Math.Max(1, (int)Math.Ceiling(p)) : null);
        BlockMode = blockMode ?? throw new ArgumentNullException(nameof(blockMode));
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

    /// <summary>Builds the configured <see cref="ThrottleConfig"/>.</summary>
    /// <exception cref="ArgumentException">Neither <see cref="MaxConcurrent(int)"/> nor <see cref="PermitsPerSecond(double)"/> was specified.</exception>
    public ThrottleConfig Build() => new(_maxConcurrent, _permitsPerSecond, _burstSize, _blockMode);
}
