#nullable enable
using System;
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
#pragma warning disable CS0618 // Type or member is obsolete
        Task<GrainAddress?> Register(GrainAddress address, CancellationToken cancellationToken) => Register(address).WaitAsync(cancellationToken);
#pragma warning restore CS0618 // Type or member is obsolete

        /// <summary>
        /// Register a <see cref="GrainAddress"/> entry in the directory.
        /// Only one <see cref="GrainAddress"/> per <see cref="GrainAddress.GrainId"/> can be registered. If there is already an
        /// existing entry, the directory will not override it.
        /// </summary>
        /// <param name="address">The <see cref="GrainAddress"/> to register</param>
        /// <returns>The <see cref="GrainAddress"/> that is effectively registered in the directory.</returns>
#pragma warning disable CS0618 // Type or member is obsolete
        Task<GrainAddress?> Register(GrainAddress address, GrainAddress? previousAddress, CancellationToken cancellationToken) => Register(address, previousAddress).WaitAsync(cancellationToken);
#pragma warning restore CS0618 // Type or member is obsolete

        /// <summary>
        /// Unregisters the specified <see cref="GrainAddress"/> entry from the directory.
        /// </summary>
        /// <param name="address">
        /// The <see cref="GrainAddress"/> to unregister.
        /// </param>
        /// <returns>
        /// A <see cref="Task"/> representing the operation.
        /// </returns>
#pragma warning disable CS0618 // Type or member is obsolete
        Task Unregister(GrainAddress address, CancellationToken cancellationToken) => Unregister(address).WaitAsync(cancellationToken);
#pragma warning restore CS0618 // Type or member is obsolete

        /// <summary>
        /// Lookup for a <see cref="GrainAddress"/> for a given Grain ID.
        /// </summary>
        /// <param name="grainId">The Grain ID to lookup</param>
        /// <returns>The <see cref="GrainAddress"/> entry found in the directory, if any</returns>
#pragma warning disable CS0618 // Type or member is obsolete
        Task<GrainAddress?> Lookup(GrainId grainId, CancellationToken cancellationToken) => Lookup(grainId).WaitAsync(cancellationToken);
#pragma warning restore CS0618 // Type or member is obsolete

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
#pragma warning disable CS0618 // Type or member is obsolete
        Task UnregisterSilos(List<SiloAddress> siloAddresses, CancellationToken cancellationToken) => UnregisterSilos(siloAddresses).WaitAsync(cancellationToken);
#pragma warning restore CS0618 // Type or member is obsolete

        /// <summary>
        /// Register a <see cref="GrainAddress"/> entry in the directory.
        /// Only one <see cref="GrainAddress"/> per <see cref="GrainAddress.GrainId"/> can be registered. If there is already an
        /// existing entry, the directory will not override it.
        /// </summary>
        /// <param name="address">The <see cref="GrainAddress"/> to register</param>
        /// <returns>The <see cref="GrainAddress"/> that is effectively registered in the directory.</returns>
        [Obsolete($"Use the overload which accepts a {nameof(CancellationToken)}")]
        Task<GrainAddress?> Register(GrainAddress address);

        /// <summary>
        /// Register a <see cref="GrainAddress"/> entry in the directory.
        /// Only one <see cref="GrainAddress"/> per <see cref="GrainAddress.GrainId"/> can be registered. If there is already an
        /// existing entry, the directory will not override it.
        /// </summary>
        /// <param name="address">The <see cref="GrainAddress"/> to register</param>
        /// <returns>The <see cref="GrainAddress"/> that is effectively registered in the directory.</returns>
        [Obsolete($"Use the overload which accepts a {nameof(CancellationToken)}")]
        Task<GrainAddress?> Register(GrainAddress address, GrainAddress? previousAddress) => GrainDirectoryExtension.Register(this, address, previousAddress, CancellationToken.None);

        /// <summary>
        /// Unregisters the specified <see cref="GrainAddress"/> entry from the directory.
        /// </summary>
        /// <param name="address">
        /// The <see cref="GrainAddress"/> to unregister.
        /// </param>
        /// <returns>
        /// A <see cref="Task"/> representing the operation.
        /// </returns>
        [Obsolete($"Use the overload which accepts a {nameof(CancellationToken)}")]
        Task Unregister(GrainAddress address);

        /// <summary>
        /// Lookup for a <see cref="GrainAddress"/> for a given Grain ID.
        /// </summary>
        /// <param name="grainId">The Grain ID to lookup</param>
        /// <returns>The <see cref="GrainAddress"/> entry found in the directory, if any</returns>
        [Obsolete($"Use the overload which accepts a {nameof(CancellationToken)}")]
        Task<GrainAddress?> Lookup(GrainId grainId);

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
        [Obsolete($"Use the overload which accepts a {nameof(CancellationToken)}")]
        Task UnregisterSilos(List<SiloAddress> siloAddresses);
    }

    internal static class GrainDirectoryExtension
    {
        internal static async Task<GrainAddress?> Register(IGrainDirectory directory, GrainAddress address, GrainAddress? previousAddress, CancellationToken cancellationToken)
        {
            if (previousAddress is not null)
            {
                await directory.Unregister(previousAddress, cancellationToken);
            }

            return await directory.Register(address, cancellationToken);
        }
    }
}
