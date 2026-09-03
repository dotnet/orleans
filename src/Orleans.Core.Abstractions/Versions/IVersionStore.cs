using System.Collections.Generic;
using System.Threading;
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
        [Alias("GetCompatibilityStrategies")]
        Task<Dictionary<GrainInterfaceType, CompatibilityStrategy>> GetCompatibilityStrategies();

        /// <summary>Gets the configured compatibility strategies.</summary>
        [Alias("245EF151")]
        Task<Dictionary<GrainInterfaceType, CompatibilityStrategy>> GetCompatibilityStrategies(CancellationToken cancellationToken)
            => GetCompatibilityStrategies();

        /// <summary>
        /// Gets the mapping from grain interface type to grain interface version selector strategy.
        /// </summary>
        /// <returns>The mapping from grain interface type to grain interface version selector strategy.</returns>
        [Alias("GetSelectorStrategies")]
        Task<Dictionary<GrainInterfaceType, VersionSelectorStrategy>> GetSelectorStrategies();

        /// <summary>Gets the configured selector strategies.</summary>
        [Alias("CE5EE42F")]
        Task<Dictionary<GrainInterfaceType, VersionSelectorStrategy>> GetSelectorStrategies(CancellationToken cancellationToken)
            => GetSelectorStrategies();

        /// <summary>
        /// Gets the default grain interface version compatibility strategy.
        /// </summary>
        /// <returns>The default grain interface version compatibility strategy.</returns>
        [Alias("GetCompatibilityStrategy")]
        Task<CompatibilityStrategy?> GetCompatibilityStrategy();

        /// <summary>Gets the default compatibility strategy.</summary>
        [Alias("294392A7")]
        Task<CompatibilityStrategy?> GetCompatibilityStrategy(CancellationToken cancellationToken)
            => GetCompatibilityStrategy();

        /// <summary>
        /// Gets the default grain interface version selector strategy.
        /// </summary>
        /// <returns>The default grain interface version selector strategy.</returns>
        [Alias("GetSelectorStrategy")]
        Task<VersionSelectorStrategy?> GetSelectorStrategy();

        /// <summary>Gets the default selector strategy.</summary>
        [Alias("E9C4A2A7")]
        Task<VersionSelectorStrategy?> GetSelectorStrategy(CancellationToken cancellationToken)
            => GetSelectorStrategy();
    }
}
