using System.Threading;
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
        [Alias("SetCompatibilityStrategy")]
        Task SetCompatibilityStrategy(CompatibilityStrategy strategy);

        /// <summary>Set the compatibility strategy.</summary>
        [Alias("8F5C15A9")]
        Task SetCompatibilityStrategy(CompatibilityStrategy strategy, CancellationToken cancellationToken)
            => SetCompatibilityStrategy(strategy);

        /// <summary>
        /// Set the selector strategy.
        /// </summary>
        /// <param name="strategy">The strategy to set. Set to <see langword="null"/> to revert to the default strategy provided in configuration.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        [Alias("SetSelectorStrategy")]
        Task SetSelectorStrategy(VersionSelectorStrategy strategy);

        /// <summary>Set the selector strategy.</summary>
        [Alias("4AAEAFCE")]
        Task SetSelectorStrategy(VersionSelectorStrategy strategy, CancellationToken cancellationToken)
            => SetSelectorStrategy(strategy);

        /// <summary>
        /// Set the compatibility strategy for a specific interface.
        /// </summary>
        /// <param name="interfaceType">The type of the interface.</param>
        /// <param name="strategy">The strategy to set. Set to <see langword="null"/> to revert to the default strategy provided in configuration.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        [Alias("SetCompatibilityStrategyForInterface")]
        Task SetCompatibilityStrategy(GrainInterfaceType interfaceType, CompatibilityStrategy strategy);

        /// <summary>Set the compatibility strategy for a specific interface.</summary>
        [Alias("C01C4EE8")]
        Task SetCompatibilityStrategy(
            GrainInterfaceType interfaceType,
            CompatibilityStrategy strategy,
            CancellationToken cancellationToken)
            => SetCompatibilityStrategy(interfaceType, strategy);

        /// <summary>
        /// Set the selector strategy for a specific interface.
        /// </summary>
        /// <param name="interfaceType">The type of the interface.</param>
        /// <param name="strategy">The strategy to set. Set to <see langword="null"/> to revert to the default strategy provided in configuration.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        [Alias("SetSelectorStrategyForInterface")]
        Task SetSelectorStrategy(GrainInterfaceType interfaceType, VersionSelectorStrategy strategy);

        /// <summary>Set the selector strategy for a specific interface.</summary>
        [Alias("90AB9D5E")]
        Task SetSelectorStrategy(
            GrainInterfaceType interfaceType,
            VersionSelectorStrategy strategy,
            CancellationToken cancellationToken)
            => SetSelectorStrategy(interfaceType, strategy);
    }
}
