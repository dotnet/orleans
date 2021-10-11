using System;
using System.Threading;
using System.Threading.Tasks;

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
}