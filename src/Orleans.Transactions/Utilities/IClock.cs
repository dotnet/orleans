using System;

namespace Orleans.Transactions
{
    /// <summary>
    /// Provides the current Coordinated Universal Time.
    /// </summary>
    public interface IClock
    {
        /// <summary>
        /// Gets the current Coordinated Universal Time.
        /// </summary>
        /// <returns>The current UTC date and time.</returns>
        DateTime UtcNow();
    }
}
