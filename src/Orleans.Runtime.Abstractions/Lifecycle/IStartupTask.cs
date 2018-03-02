using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime
{
    /// <summary>
    /// Defines an action to be taken during silo startup.
    /// </summary>
    public interface IStartupTask
    {
        /// <summary>
        /// Called after the silo has started.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token which is canceled when the method must abort.</param>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        Task Execute(CancellationToken cancellationToken);
    }
}
