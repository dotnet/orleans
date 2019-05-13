
using System;

namespace Orleans.Transactions
{
    public class CausalClock
    {
        private readonly Object lockable = new object();
        private readonly IClock clock;
        private long previous;

        public CausalClock(IClock clock)
        {
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public DateTime UtcNow()
        {
            lock (this.lockable) return new DateTime(
                previous = Math.Max(previous + 1, this.clock.UtcNow().Ticks));
        }

        public DateTime Merge(DateTime timestamp)
        {
            lock (this.lockable) return new DateTime(
                previous = Math.Max(previous, timestamp.Ticks));
        }

        public DateTime MergeUtcNow(DateTime timestamp)
        {
            lock (this.lockable) return new DateTime(
                previous = Math.Max(Math.Max(previous + 1, timestamp.Ticks + 1), this.clock.UtcNow().Ticks));
        }
    }
}
