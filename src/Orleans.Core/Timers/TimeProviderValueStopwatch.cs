using System;

namespace Orleans.Runtime;

/// <summary>
/// A non-allocating stopwatch backed by a <see cref="TimeProvider"/>.
/// </summary>
internal readonly struct TimeProviderValueStopwatch
{
    private readonly TimeProvider _timeProvider;

    private TimeProviderValueStopwatch(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        StartTimestamp = timeProvider.GetTimestamp();
    }

    /// <summary>
    /// Gets the timestamp captured when this stopwatch started.
    /// </summary>
    public long StartTimestamp { get; }

    /// <summary>
    /// Starts a new stopwatch.
    /// </summary>
    public static TimeProviderValueStopwatch StartNew(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        return new(timeProvider);
    }

    /// <summary>
    /// Gets the elapsed duration and returns the timestamp used as the endpoint.
    /// </summary>
    public TimeSpan GetElapsedTime(out long endTimestamp)
    {
        endTimestamp = _timeProvider.GetTimestamp();
        return _timeProvider.GetElapsedTime(StartTimestamp, endTimestamp);
    }
}
