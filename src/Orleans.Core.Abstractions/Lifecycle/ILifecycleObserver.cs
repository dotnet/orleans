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
        /// Handle start notifications
        /// </summary>
        /// <returns></returns>
        Task OnStart(CancellationToken ct);

        /// <summary>
        /// Handle stop notifications
        /// </summary>
        /// <returns></returns>
        Task OnStop(CancellationToken ct);
    }
}
