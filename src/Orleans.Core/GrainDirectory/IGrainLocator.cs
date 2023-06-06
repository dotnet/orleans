#nullable enable
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.GrainDirectory
{
    /// <summary>
    /// Used to locate Grain activation in the cluster
    /// </summary>
    public interface IGrainLocator
    {
        /// <summary>
        /// Registers the provided address in the appropriate grain directory.
        /// </summary>
        /// <param name="address">The address to register.</param>
        /// <returns>The grain address which is registered in the directory immediately following this call.</returns>
        Task<GrainAddress> Register(GrainAddress address, GrainAddress? previousRegistration);

        /// <summary>
        /// Deregisters a grain address from the directory.
        /// </summary>
        /// <param name="address">The address to deregister.</param>
        /// <param name="cause">The cause for deregistration.</param>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        Task Unregister(GrainAddress address, UnregistrationCause cause);

        /// <summary>
        /// Finds the corresponding address for a grain.
        /// </summary>
        /// <param name="grainId">The grain id.</param>
        /// <returns>The address corresponding to the specified grain id, or <see langword="null"/> if the grain is not currently registered.</returns>
        ValueTask<GrainAddress?> Lookup(GrainId grainId);

        /// <summary>
        /// Records a grain placement decision.
        /// </summary>
        /// <param name="grainId">The newly placed grain.</param>
        /// <param name="siloAddress">The placement result.</param>
        void CachePlacementDecision(GrainId grainId, SiloAddress siloAddress);

        /// <summary>
        /// Invalidates any lookup cache entry associated with the provided grain id.
        /// </summary>
        /// <param name="grainId">
        /// The grain id.
        /// </param>
        void InvalidateCache(GrainId grainId);

        /// <summary>
        /// Removes the specified address from the lookup cache.
        /// </summary>
        /// <param name="address">
        /// The grain address to invalidate.
        /// </param>
        void InvalidateCache(GrainAddress address);

        /// <summary>
        /// Attempts to find the grain address for the provided grain id in the local lookup cache.
        /// </summary>
        /// <param name="grainId">The grain id to find.</param>
        /// <param name="address">The resulting grain address, if found, or <see langword="null"/> if not found.</param>
        /// <returns>A value indicating whether a valid entry was found.</returns>
        bool TryLookupInCache(GrainId grainId, [NotNullWhen(true)] out GrainAddress? address);
    }
}
