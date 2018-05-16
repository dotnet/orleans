
using System;

namespace Orleans.Transactions.Tests
{
    public class SkewedClock : IClock
    {
        private readonly Random rand = new Random(Guid.NewGuid().GetHashCode());
        private readonly TimeSpan minSkew;
        private readonly int skewRangeTicks;

        public SkewedClock(TimeSpan minSkew, TimeSpan maxSkew)
        {
            this.minSkew = minSkew;
            this.skewRangeTicks = (int)(maxSkew.Ticks - minSkew.Ticks);
        }
        public DateTime UtcNow()
        {
            TimeSpan skew = TimeSpan.FromTicks(minSkew.Ticks + rand.Next(0, skewRangeTicks));
            // skew forward in time or backward in time
            return (rand.Next() > 0)
                ? DateTime.UtcNow + skew
                : DateTime.UtcNow - skew;
        }
    }
}
