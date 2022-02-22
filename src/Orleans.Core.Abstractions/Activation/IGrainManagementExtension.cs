using System.Threading.Tasks;

namespace Orleans.Runtime
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
        Task DeactivateOnIdle();
    }
}
