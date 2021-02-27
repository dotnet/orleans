using System;
using Orleans.Internal;

namespace Orleans.EventSourcing.Common
{
    /// <summary>
    /// Describes a connection issue that occurred when sending update notifications to remote instances.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class NotificationFailed : ConnectionIssue
    {
        /// <summary> The destination cluster which we could not reach successfully. </summary>
        [Id(0)]
        public string RemoteCluster { get; set; }

        /// <summary> The exception we caught when trying to send the notification message. </summary>
        [Id(1)]
        public Exception Exception { get; set; }

        /// <inheritdoc/>
        public override TimeSpan ComputeRetryDelay(TimeSpan? previous)
        {
            if (NumberOfConsecutiveFailures < 3) return TimeSpan.FromMilliseconds(1);
            else if (NumberOfConsecutiveFailures < 1000) return TimeSpan.FromSeconds(30);
            else return TimeSpan.FromMinutes(1);
        }
    }

    /// <summary>
    /// Describes a connection issue that occurred when communicating with primary storage.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class PrimaryOperationFailed : ConnectionIssue
    {
        /// <summary>
        /// The exception that was caught when communicating with the primary.
        /// </summary>
        [Id(0)]
        public Exception Exception { get; set; }

        /// <inheritdoc/>
        public override TimeSpan ComputeRetryDelay(TimeSpan? previous)
        {
            // after first fail do not backoff yet... keep it at zero
            if (previous == null)
            {
                return TimeSpan.Zero;
            }

            var backoff = previous.Value.TotalMilliseconds;

            // grows exponentially up to slowpoll interval
            if (previous.Value.TotalMilliseconds < slowpollinterval)
                backoff = (int)((backoff + ThreadSafeRandom.Next(5, 15)) * 1.5);

            // during slowpoll, slightly randomize
            if (backoff > slowpollinterval)
                backoff = slowpollinterval + ThreadSafeRandom.Next(1, 200);

            return TimeSpan.FromMilliseconds(backoff);
        }

        private const int slowpollinterval = 10000;
    }
}
