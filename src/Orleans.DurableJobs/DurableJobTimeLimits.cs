using System;

namespace Orleans.DurableJobs;

internal static class DurableJobTimeLimits
{
    // TimerQueue uses a 32-bit millisecond duration. Keep one millisecond below
    // the sentinel values used for infinite timers.
    public static readonly TimeSpan MaximumTimerDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 2L);

    public static TimeSpan ClampTimerDelay(TimeSpan delay)
        => delay > MaximumTimerDelay ? MaximumTimerDelay : delay;
}
