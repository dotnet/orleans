using System;

namespace Orleans.EventSourcing
{

    /// <summary>
    /// Represents information about connection issues encountered inside log consistency protocols.
    /// It is used both inside the protocol to track retry loops, and is made visible to users 
    /// who want to monitor their log-consistent grains for communication issues.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public abstract class ConnectionIssue
    {
        /// <summary>
        /// The UTC timestamp of the last time at which the issue was observed
        /// </summary>
        [Id(0)]
        public DateTime TimeStamp { get; set; }

        /// <summary>
        /// The UTC timestamp of the first time we observed this issue
        /// </summary>
        [Id(1)]
        public DateTime TimeOfFirstFailure { get; set; }

        /// <summary>
        /// The number of times we have observed this issue since the first failure
        /// </summary>
        [Id(2)]
        public int NumberOfConsecutiveFailures { get; set; }

        /// <summary>
        /// The delay we are waiting before the next retry
        /// </summary>
        [Id(3)]
        public TimeSpan RetryDelay { get; set; }

        /// <summary>
        /// Computes the retry delay based on the rest of the information. Is overridden by subclasses
        /// that represent specific categories of issues.
        /// </summary>
        /// <param name="previous">The previously used retry delay</param>
        /// <returns></returns>
        public abstract TimeSpan ComputeRetryDelay(TimeSpan? previous);
    }
}