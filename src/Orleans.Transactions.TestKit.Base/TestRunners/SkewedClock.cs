using System;
using System.Diagnostics.CodeAnalysis;

namespace Orleans.Transactions.TestKit
{
    /// <summary>
    /// Provides UTC timestamps with a randomly selected positive or negative clock skew.
    /// </summary>
    public class SkewedClock : IClock
    {
        private readonly TimeSpan minSkew;
        private readonly int skewRangeTicks;

        /// <summary>
        /// Initializes a new instance of the <see cref="SkewedClock"/> class.
        /// </summary>
        /// <param name="minSkew">The inclusive minimum magnitude of the generated clock skew.</param>
        /// <param name="maxSkew">The exclusive maximum magnitude of the generated clock skew.</param>
        public SkewedClock(TimeSpan minSkew, TimeSpan maxSkew)
        {
            this.minSkew = minSkew;
            this.skewRangeTicks = (int)(maxSkew.Ticks - minSkew.Ticks);
        }

        /// <inheritdoc/>
        [SuppressMessage("Security", "CA5394:Do not use insecure randomness",
            Justification = "Randomness injects positive or negative clock skew into transaction conformance tests.")]
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
