
using System;

namespace Orleans.Transactions
{
    public class CausalClock
    {
        private readonly object lockable = new object();
        private readonly IClock clock;
        private long previous;

        public CausalClock(IClock clock)
        {
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public DateTime UtcNow()
        {
            lock (this.lockable)
            {
                var ticks = previous = Math.Max(previous + 1, this.clock.UtcNow().Ticks);
                return new DateTime(ticks, DateTimeKind.Utc);
            }
        }

        public DateTime Merge(DateTime timestamp)
        {
            lock (this.lockable)
            {
                var ticks = previous = Math.Max(previous, timestamp.Ticks);
                return new DateTime(ticks, DateTimeKind.Utc);
            }
        }

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
