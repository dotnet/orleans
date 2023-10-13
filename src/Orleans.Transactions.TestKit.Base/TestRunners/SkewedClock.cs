using System;

namespace Orleans.Transactions.TestKit
{
    public class SkewedClock : IClock
    {
        private readonly TimeSpan minSkew;
        private readonly int skewRangeTicks;

        public SkewedClock(TimeSpan minSkew, TimeSpan maxSkew)
        {
            this.minSkew = minSkew;
            this.skewRangeTicks = (int)(maxSkew.Ticks - minSkew.Ticks);
        }

        public DateTime UtcNow()
        {
            TimeSpan skew = TimeSpan.FromTicks(minSkew.Ticks + Random.Shared.Next(skewRangeTicks));
            // skew forward in time or backward in time
            return ((Random.Shared.Next() & 1) != 0)
                ? DateTime.UtcNow + skew
                : DateTime.UtcNow - skew;
        }
    }
}
