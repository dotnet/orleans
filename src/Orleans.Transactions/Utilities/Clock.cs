using System;

namespace Orleans.Transactions
{
    /// <summary>
    /// Provides time from the system clock.
    /// </summary>
    public class Clock : IClock
    {
        /// <inheritdoc/>
        public DateTime UtcNow()
        {
            return DateTime.UtcNow;
        }
    }
}
