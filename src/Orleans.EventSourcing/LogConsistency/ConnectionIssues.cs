using System;

namespace Orleans.EventSourcing
{

    /// <summary>
    /// Represents information about connection issues encountered inside log consistency protocols.
    /// It is used both inside the protocol to track retry loops, and is made visible to users 
    /// who want to monitor their log-consistent grains for communication issues.
    /// </summary>
    [Serializable]
    public abstract class ConnectionIssue
    {
        /// <summary>
        /// The UTC timestamp of the last time at which the issue was observed
        /// </summary>
        public DateTime TimeStamp { get; set; }

        /// <summary>
        /// The UTC timestamp of the first time we observed this issue
        /// </summary>
        public DateTime TimeOfFirstFailure { get; set; }

        /// <summary>
        /// The number of times we have observed this issue since the first failure
        /// </summary>
        public int NumberOfConsecutiveFailures { get; set; }

        /// <summary>
        /// The delay we are waiting before the next retry
        /// </summary>
        public TimeSpan RetryDelay { get; set; }

        /// <summary>
        /// Computes the retry delay based on the rest of the information. Is overridden by subclasses
        /// that represent specific categories of issues.
        /// </summary>
        /// <param name="previous">The previously used retry delay</param>
        /// <returns></returns>
        public abstract TimeSpan ComputeRetryDelay(TimeSpan? previous);
    }



    /// <summary>
    /// Represents information about notification failures encountered inside log consistency protocols.
    /// </summary>
    [Serializable]
    public abstract class NotificationFailed : ConnectionIssue
    {
        /// <summary>
        /// The clusterId of the remote cluster to which we had an issue when sending change notifications.
        /// </summary>
        public string RemoteClusterId { get; set; }

        /// <summary>
        /// The exception we caught, or null if the problem was not caused by an exception.
        /// </summary>
        public Exception Exception { get; set; }
    }



}