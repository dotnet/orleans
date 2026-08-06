using System;

namespace Orleans.Internal
{
    internal static class BackoffComputation
    {
        private const int MaxBoundShift = 30;

        public static TimeSpan ComputeBackoffDelay(int consecutiveFailures, TimeSpan baseMin, TimeSpan baseMax, TimeSpan cap)
        {
            if (baseMin <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(baseMin), baseMin, "baseMin must be positive.");
            if (baseMax <= baseMin) throw new ArgumentOutOfRangeException(nameof(baseMax), baseMax, "baseMax must be greater than baseMin.");
            if (cap < baseMax) throw new ArgumentOutOfRangeException(nameof(cap), cap, "cap must be greater than or equal to baseMax.");

            var shift = consecutiveFailures <= 1 ? 0 : Math.Min(consecutiveFailures - 1, MaxBoundShift);

            var minTicks = SafeShift(baseMin.Ticks, shift);
            var maxTicks = SafeShift(baseMax.Ticks, shift);

            if (maxTicks <= 0 || maxTicks >= cap.Ticks)
            {
                maxTicks = cap.Ticks;
                minTicks = cap.Ticks / 2;
            }

            if (minTicks >= maxTicks)
            {
                minTicks = Math.Max(1, maxTicks - 1);
            }

            return RandomTimeSpan.Next(TimeSpan.FromTicks(minTicks), TimeSpan.FromTicks(maxTicks));
        }

        private static long SafeShift(long value, int shift)
        {
            if (shift <= 0) return value;
            if (shift >= 63 || value > (long.MaxValue >> shift)) return -1;
            return value << shift;
        }
    }
}
