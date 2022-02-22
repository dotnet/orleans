using System.Collections.Generic;

namespace Orleans.Runtime.Placement
{
    /// <summary>
    /// Provides context for a grain placement operation.
    /// </summary>
    public interface IPlacementContext
    {
        /// <summary>
        /// Gets the collection of silos which are compatible with the provided placement target.
        /// </summary>
        /// <param name="target">
        /// A description of the grain being placed as well as contextual information about the request which is triggering placement.
        /// </param>
        /// <returns>The collection of silos which are compatible with the provided placement target.</returns>
        SiloAddress[] GetCompatibleSilos(PlacementTarget target);

        /// <summary>
        /// Gets the collection of silos which are compatible with the provided placement target, along with the versions of the grain interface which each server supports.
        /// </summary>
        /// <param name="target">
        /// A description of the grain being placed as well as contextual information about the request which is triggering placement.
        /// </param>
        /// <returns>The collection of silos which are compatible with the provided placement target, along with the versions of the grain interface which each server supports.</returns>
        IReadOnlyDictionary<ushort, SiloAddress[]> GetCompatibleSilosWithVersions(PlacementTarget target);

        /// <summary>
        /// Gets the local silo's identity.
        /// </summary>
        SiloAddress LocalSilo { get; }

        /// <summary>
        /// Gets the local silo's status.
        /// </summary>
        SiloStatus LocalSiloStatus { get; }
    }
}
