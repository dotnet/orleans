using System.Threading;
using System.Threading.Tasks;

namespace Orleans
{
    /// <summary>
    /// Lifecycle observer used to handle start and stop notification.
    /// </summary>
    public interface ILifecycleObserver
    {
        /// <summary>
        /// Handle start notifications.
        /// </summary>
        /// <param name="cancellationToken">
        /// The cancellation token which indicates that the operation should be aborted promptly when it is canceled.
        /// </param>
        /// <returns>
        /// A <see cref="Task"/> which represents the operation.
        /// </returns>
        Task OnStart(CancellationToken cancellationToken = default);

        /// <summary>
        /// Handle stop notifications.
        /// </summary>
        /// <param name="cancellationToken">
        /// The cancellation token which indicates that the operation should be stopped promptly when it is canceled.
        /// </param>
        /// <returns>
        /// A <see cref="Task"/> which represents the operation.
        /// </returns>
        Task OnStop(CancellationToken cancellationToken = default);
    }
}
