using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.GrainDirectory
{
    /// <summary>
    /// Interface for grain directory implementations
    /// </summary>
    public interface IGrainDirectory
    {
        /// <summary>
        /// Register a <see cref="GrainAddress"/> entry in the directory.
        /// Only one <see cref="GrainAddress"/> per <see cref="GrainAddress.GrainId"/> can be registered. If there is already an
        /// existing entry, the directory will not override it.
        /// </summary>
        /// <param name="address">The <see cref="GrainAddress"/> to register</param>
        /// <returns>The <see cref="GrainAddress"/> that is effectively registered in the directory.</returns>
        Task<GrainAddress?> Register(GrainAddress address);

        /// <summary>
        /// Register a <see cref="GrainAddress"/> entry in the directory.
        /// </summary>
        /// <param name="address">The <see cref="GrainAddress"/> to register.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The <see cref="GrainAddress"/> that is effectively registered in the directory.</returns>
        Task<GrainAddress?> Register(GrainAddress address, CancellationToken cancellationToken) =>
            Register(address).WaitAsync(cancellationToken);

        /// <summary>
        /// Register a <see cref="GrainAddress"/> entry in the directory.
        /// Only one <see cref="GrainAddress"/> per <see cref="GrainAddress.GrainId"/> can be registered. If there is already an
        /// existing entry, the directory will not override it.
        /// </summary>
        /// <param name="address">The <see cref="GrainAddress"/> to register</param>
        /// <param name="previousAddress">The previous registration to replace, if any.</param>
        /// <returns>The <see cref="GrainAddress"/> that is effectively registered in the directory.</returns>
        Task<GrainAddress?> Register(GrainAddress address, GrainAddress? previousAddress) => GrainDirectoryExtension.Register(this, address, previousAddress);

        /// <summary>
        /// Register a <see cref="GrainAddress"/> entry in the directory.
        /// </summary>
        /// <param name="address">The <see cref="GrainAddress"/> to register.</param>
        /// <param name="previousAddress">The previous registration, if any.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The <see cref="GrainAddress"/> that is effectively registered in the directory.</returns>
        Task<GrainAddress?> Register(GrainAddress address, GrainAddress? previousAddress, CancellationToken cancellationToken) =>
            Register(address, previousAddress).WaitAsync(cancellationToken);

        /// <summary>
        /// Unregisters the specified <see cref="GrainAddress"/> entry from the directory.
        /// </summary>
        /// <param name="address">
        /// The <see cref="GrainAddress"/> to unregister.
        /// </param>
        /// <returns>
        /// A <see cref="Task"/> representing the operation.
        /// </returns>
        Task Unregister(GrainAddress address);

        /// <summary>
        /// Unregisters the specified <see cref="GrainAddress"/> entry from the directory.
        /// </summary>
        /// <param name="address">The <see cref="GrainAddress"/> to unregister.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        Task Unregister(GrainAddress address, CancellationToken cancellationToken) =>
            Unregister(address).WaitAsync(cancellationToken);

        /// <summary>
        /// Lookup for a <see cref="GrainAddress"/> for a given Grain ID.
        /// </summary>
        /// <param name="grainId">The Grain ID to lookup</param>
        /// <returns>The <see cref="GrainAddress"/> entry found in the directory, if any</returns>
        Task<GrainAddress?> Lookup(GrainId grainId);

        /// <summary>
        /// Lookup for a <see cref="GrainAddress"/> for a given Grain ID.
        /// </summary>
        /// <param name="grainId">The Grain ID to lookup.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The <see cref="GrainAddress"/> entry found in the directory, if any.</returns>
        Task<GrainAddress?> Lookup(GrainId grainId, CancellationToken cancellationToken) =>
            Lookup(grainId).WaitAsync(cancellationToken);

        /// <summary>
        /// Unregisters all grain directory entries which point to any of the specified silos.
        /// </summary>
        /// <remarks>
        /// Can be a No-Op depending on the implementation.
        /// </remarks>
        /// <param name="siloAddresses">The silos to be removed from the directory</param>
        /// <returns>
        /// A <see cref="Task"/> representing the operation.
        /// </returns>
        Task UnregisterSilos(List<SiloAddress> siloAddresses);

        /// <summary>
        /// Unregisters all grain directory entries which point to any of the specified silos.
        /// </summary>
        /// <param name="siloAddresses">The silos to be removed from the directory.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        Task UnregisterSilos(List<SiloAddress> siloAddresses, CancellationToken cancellationToken) =>
            UnregisterSilos(siloAddresses).WaitAsync(cancellationToken);
    }

    internal static class GrainDirectoryExtension
    {
        internal static async Task<GrainAddress?> Register(IGrainDirectory directory, GrainAddress address, GrainAddress? previousAddress)
        {
            if (previousAddress is not null)
            {
                await directory.Unregister(previousAddress);
            }

            return await directory.Register(address);
        }
    }
}
