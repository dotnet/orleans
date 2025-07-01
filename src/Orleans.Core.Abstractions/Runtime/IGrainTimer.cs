using System;
using System.Threading;

namespace Orleans.Runtime;

/// <summary>
/// Represents a timer belonging to a grain.
/// </summary>
public interface IGrainTimer : IDisposable
{
    /// <summary>Changes the start time and the interval between method invocations for a timer, using <see cref="TimeSpan"/> values to measure time intervals.</summary>
    /// <param name="dueTime">
    /// A <see cref="TimeSpan"/> representing the amount of time to delay before invoking the callback method specified when the <see cref="IGrainTimer"/> was constructed.
    /// Specify <see cref="Timeout.InfiniteTimeSpan"/> to prevent the timer from restarting.
    /// Specify <see cref="TimeSpan.Zero"/> to restart the timer immediately.
    /// </param>
    /// <param name="period">
    /// The time interval between invocations of the callback method specified when the timer was constructed.
    /// Specify <see cref="Timeout.InfiniteTimeSpan"/> to disable periodic signaling.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">The <paramref name="dueTime"/> or <paramref name="period"/> parameter, in milliseconds, is less than -1 or greater than 4294967294.</exception>
    void Change(TimeSpan dueTime, TimeSpan period);
}
