using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;

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
        private int _retryCount = 0;

        private const int MaxRetry = 5;
        private const int Delay = 1_500;

        public async Task<bool> ShouldRetryConnectionAttempt(
            Exception exception,
            CancellationToken cancellationToken)
        {
            if (_retryCount >= MaxRetry)
            {
                return false;
            }

            if (!cancellationToken.IsCancellationRequested && exception is SiloUnavailableException)
            {
                await Task.Delay(++_retryCount * Delay, cancellationToken);
                return true;
            }

            return false;
        }
    }
}