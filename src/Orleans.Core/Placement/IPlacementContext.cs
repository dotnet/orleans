using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

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
        /// <param name="cancellationToken">An additional token which cancels this candidate query.</param>
        /// <returns>The collection of compatible silos remaining after placement filters have run.</returns>
        /// <remarks>
        /// The placement operation's cancellation token is honored even when <paramref name="cancellationToken"/>
        /// is omitted. Candidates reflect the compatibility snapshot obtained before filtering.
        /// The returned collection must not be modified.
        /// </remarks>
        Task<SiloAddress[]> GetCompatibleSilosAsync(PlacementTarget target, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets the collection of silos which are compatible with the provided placement target, along with the versions of the grain interface which each server supports.
        /// </summary>
        /// <param name="target">
        /// A description of the grain being placed as well as contextual information about the request which is triggering placement.
        /// </param>
        /// <returns>The collection of silos which are compatible with the provided placement target, along with the versions of the grain interface which each server supports.</returns>
        /// <remarks>
        /// This method returns unfiltered compatibility data. Placement filters are applied only by
        /// <see cref="GetCompatibleSilosAsync"/>. The returned collections must not be modified.
        /// </remarks>
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
