using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Core.Internal
{
    /// <summary>
    /// Provides functionality for performing management operations on a grain activation.
    /// </summary>
    public interface IGrainManagementExtension : IGrainExtension
    {
        /// <summary>
        /// Deactivates the current instance once it becomes idle.
        /// </summary>
        /// <returns>A <see cref="Task"/> which represents the method call.</returns>
        [Alias("DeactivateOnIdle")]
        ValueTask DeactivateOnIdle();

        /// <summary>
        /// Deactivates the current instance once it becomes idle.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A <see cref="Task"/> which represents the method call.</returns>
        [Alias("1B9614D1")]
        ValueTask DeactivateOnIdle(CancellationToken cancellationToken) => DeactivateOnIdle();

        /// <summary>
        /// Attempts to migrate the current instance to a new location once it becomes idle.
        /// </summary>
        /// <returns>A <see cref="Task"/> which represents the method call.</returns>
        [Alias("MigrateOnIdle")]
        ValueTask MigrateOnIdle();

        /// <summary>
        /// Attempts to migrate the current instance to a new location once it becomes idle.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A <see cref="Task"/> which represents the method call.</returns>
        [Alias("4CC93B45")]
        ValueTask MigrateOnIdle(CancellationToken cancellationToken) => MigrateOnIdle();
    }
}
