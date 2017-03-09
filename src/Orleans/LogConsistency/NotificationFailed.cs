using System;

namespace Orleans.LogConsistency
{
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