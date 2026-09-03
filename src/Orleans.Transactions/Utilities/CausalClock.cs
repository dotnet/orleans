
using System;
using System.Threading;

namespace Orleans.Transactions
{

    /// <summary>
    /// Provides monotonically increasing UTC timestamps and merges timestamps from distributed participants.
    /// </summary>
    public class CausalClock
    {
#if NET9_0_OR_GREATER
        private readonly Lock lockable = new();
#else
        private readonly object lockable = new();
#endif
        private readonly IClock clock;
        private long previous;

        /// <summary>
        /// Initializes a new instance of the <see cref="CausalClock"/> class.
        /// </summary>
        /// <param name="clock">The physical clock used as the lower bound for generated timestamps.</param>
        public CausalClock(IClock clock)
        {
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        /// <summary>
        /// Gets a UTC timestamp which is later than every timestamp previously observed by this instance.
        /// </summary>
        /// <returns>A monotonically increasing UTC timestamp.</returns>
        public DateTime UtcNow()
        {
            lock (this.lockable)
            {
                var ticks = previous = Math.Max(previous + 1, this.clock.UtcNow().Ticks);
                return new DateTime(ticks, DateTimeKind.Utc);
            }
        }

        /// <summary>
        /// Merges a timestamp into this clock.
        /// </summary>
        /// <param name="timestamp">The timestamp to observe.</param>
        /// <returns>The latest timestamp observed by this clock.</returns>
        public DateTime Merge(DateTime timestamp)
        {
            lock (this.lockable)
            {
                var ticks = previous = Math.Max(previous, timestamp.Ticks);
                return new DateTime(ticks, DateTimeKind.Utc);
            }
        }

        /// <summary>
        /// Merges a timestamp and advances this clock using the current UTC time.
        /// </summary>
        /// <param name="timestamp">The timestamp to observe before advancing the clock.</param>
        /// <returns>A UTC timestamp later than the supplied and previously observed timestamps.</returns>
        public DateTime MergeUtcNow(DateTime timestamp)
        {
            lock (this.lockable)
            {
                var ticks = previous = Math.Max(Math.Max(previous + 1, timestamp.Ticks + 1), this.clock.UtcNow().Ticks);
                return new DateTime(ticks, DateTimeKind.Utc);
            }
        }
    }
}
