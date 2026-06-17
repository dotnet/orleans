using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;

namespace Orleans
{
    /// <summary>
    /// Filter used to determine if cluster connection should be retried.
    /// </summary>
    public interface IClientConnectionRetryFilter
    {
        /// <summary>
        /// Returns a value indicating whether connection to an Orleans cluster should be re-attempted.
        /// </summary>
        /// <param name="exception">The exception thrown from the last connection attempt.</param>
        /// <param name="cancellationToken">The cancellation token used to notify when connection has been aborted externally.</param>
        /// <returns><see langword="true"/> if connection should be re-attempted, <see langword="false"/> if attempts to connect to the cluster should be aborted.</returns>
        Task<bool> ShouldRetryConnectionAttempt(Exception exception, CancellationToken cancellationToken);
    }

    internal sealed class LinearBackoffClientConnectionRetryFilter : IClientConnectionRetryFilter
    {
        private const int MaxRetry = 15;
        private const int Delay = 1_500;
        private int _retryCount;

        public async Task<bool> ShouldRetryConnectionAttempt(
            Exception exception,
            CancellationToken cancellationToken)
        {
            if (_retryCount >= MaxRetry)
            {
                return false;
            }

            if (!cancellationToken.IsCancellationRequested && exception is OrleansMessageRejectionException or ConnectionFailedException)
            {
                ++_retryCount;
                var currentDelay = TimeSpan.FromMilliseconds(Delay * _retryCount);
                await Task.Delay(currentDelay, cancellationToken).ConfigureAwait(false);
                return true;
            }

            return false;
        }
    }
}
