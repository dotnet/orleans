using System;

namespace Orleans.EventSourcing.Common
{
    /// <summary>
    /// Describes a connection issue that occurred when communicating with primary storage.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public abstract class PrimaryOperationFailed : ConnectionIssue
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
                backoff = (int)((backoff + Random.Shared.Next(5, 15)) * 1.5);

            // during slowpoll, slightly randomize
            if (backoff > slowpollinterval)
                backoff = slowpollinterval + Random.Shared.Next(1, 200);

            return TimeSpan.FromMilliseconds(backoff);
        }

        private const int slowpollinterval = 10000;
    }
}
