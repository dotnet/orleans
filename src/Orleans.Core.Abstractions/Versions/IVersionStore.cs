using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;

namespace Orleans.Versions
{
    /// <summary>
    /// Functionality for accessing runtime-modifiable grain interface version strategies.
    /// </summary>
    public interface IVersionStore : IVersionManager
    {
        /// <summary>
        /// Gets a value indicating whether this instance is enabled.
        /// </summary>
        bool IsEnabled { get; }

        /// <summary>
        /// Gets the mapping from grain interface type to grain interface version compatibility strategy.
        /// </summary>
        /// <returns>The mapping from grain interface type to grain interface version compatibility strategy.</returns>
        Task<Dictionary<GrainInterfaceType, CompatibilityStrategy>> GetCompatibilityStrategies();

        /// <summary>
        /// Gets the mapping from grain interface type to grain interface version selector strategy.
        /// </summary>
        /// <returns>The mapping from grain interface type to grain interface version selector strategy.</returns>
        Task<Dictionary<GrainInterfaceType, VersionSelectorStrategy>> GetSelectorStrategies();

        /// <summary>
        /// Gets the default grain interface version compatibility strategy.
        /// </summary>
        /// <returns>The default grain interface version compatibility strategy.</returns>
        Task<CompatibilityStrategy> GetCompatibilityStrategy();

        /// <summary>
        /// Gets the default grain interface version selector strategy.
        /// </summary>
        /// <returns>The default grain interface version selector strategy.</returns>
        Task<VersionSelectorStrategy> GetSelectorStrategy();
    }
}