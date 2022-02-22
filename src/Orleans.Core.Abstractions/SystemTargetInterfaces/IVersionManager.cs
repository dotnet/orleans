using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;

namespace Orleans
{
    /// <summary>
    /// Functionality for managing how grain interface versions are negotiated.
    /// </summary>
    public interface IVersionManager
    {
        /// <summary>
        /// Set the compatibility strategy.
        /// </summary>
        /// <param name="strategy">The strategy to set. Set to <see langword="null"/> to revert to the default strategy provided in configuration.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        Task SetCompatibilityStrategy(CompatibilityStrategy strategy);

        /// <summary>
        /// Set the selector strategy.
        /// </summary>
        /// <param name="strategy">The strategy to set. Set to <see langword="null"/> to revert to the default strategy provided in configuration.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        Task SetSelectorStrategy(VersionSelectorStrategy strategy);

        /// <summary>
        /// Set the compatibility strategy for a specific interface.
        /// </summary>
        /// <param name="interfaceType">The type of the interface.</param>
        /// <param name="strategy">The strategy to set. Set to <see langword="null"/> to revert to the default strategy provided in configuration.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        Task SetCompatibilityStrategy(GrainInterfaceType interfaceType, CompatibilityStrategy strategy);

        /// <summary>
        /// Set the selector strategy for a specific interface.
        /// </summary>
        /// <param name="interfaceType">The type of the interface.</param>
        /// <param name="strategy">The strategy to set. Set to <see langword="null"/> to revert to the default strategy provided in configuration.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        Task SetSelectorStrategy(GrainInterfaceType interfaceType, VersionSelectorStrategy strategy);
    }
}