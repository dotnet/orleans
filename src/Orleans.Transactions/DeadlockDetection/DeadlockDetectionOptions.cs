using System;

namespace Orleans.Transactions.DeadlockDetection
{
    public class DeadlockDetectionOptions
    {
        /// <summary>
        /// Start deadlock detection if a transaction is late by this amount.
        /// </summary>
        public TimeSpan DeadlockDetectionTimeout { get; set; } = DefaultDeadlockDetectionTimeout;
        private static readonly TimeSpan DefaultDeadlockDetectionTimeout = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Time to wait for a silo to respond with it's locks
        /// </summary>
        public TimeSpan DeadlockRequestTimeout { get; set; } = DefaultDeadlockRequestTimeout;
        private static readonly TimeSpan DefaultDeadlockRequestTimeout = TimeSpan.FromSeconds(3);

        /// <summary>
        /// Give up after making this many requests to silos without stabilizing or finding
        /// a deadlock.
        /// </summary>
        public int MaxDeadlockRequests { get; set; } = DefaultMaxDeadlockRequests;
        private const int DefaultMaxDeadlockRequests = 5;

        /// <summary>
        /// Max number of deadlock detection requests to handle concurrently.
        /// </summary>
        public int MaxConcurrentDeadlockAnalysis { get; set; } = DefaultMaxConcurrentDeadlockAnalysis;
        private const int DefaultMaxConcurrentDeadlockAnalysis = 3;
    }
}