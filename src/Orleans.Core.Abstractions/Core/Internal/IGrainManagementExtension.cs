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
        ValueTask DeactivateOnIdle();

        /// <summary>
        /// Attempts to migrate the current instance to a new location once it becomes idle.
        /// </summary>
        /// <returns>A <see cref="Task"/> which represents the method call.</returns>
        ValueTask MigrateOnIdle();
    }
}
