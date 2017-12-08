using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.LogConsistency
{
 
    /// <summary>
    /// Represents information about connection issues encountered inside log consistency protocols.
    /// It is used both inside the protocol to track retry loops, and is made visible to users 
    /// who want to monitor their log-consistent grains for communication issues.
    /// </summary>
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
        /// The connection is retried after the specified delay.
        /// The application can change this property to customize the desired retry behavior.
        /// </summary>
        public TimeSpan RetryAfter { get; set; }

        /// <summary>
        /// If true, the connection is retried when there is activity.
        /// The application can change this property to customize the desired retry behavior.
        /// </summary>
        public bool RetryOnActivity { get; set; }

        /// <summary>
        /// If this is set to a task, a retry is initiated after the task completes.
        /// The application can set this property to initiate a retry at a suitable moment.
        /// </summary>
        public Task RetryWhen { get; set; }

        /// <summary>
        /// Subclasses implement this method to define a default policy
        /// for specific categories of issues.
        /// </summary>
        public abstract void UpdateRetryParameters();
    }




}