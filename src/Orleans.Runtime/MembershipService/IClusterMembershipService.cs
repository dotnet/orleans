using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Runtime
{
    /// <summary>
    /// Functionality for querying and interacting with cluster membership.
    /// </summary>
    public interface IClusterMembershipService
    {
        /// <summary>
        /// Gets the most recent cluster membership snapshot.
        /// </summary>
        /// <value>The current snapshot.</value>
        ClusterMembershipSnapshot CurrentSnapshot { get; }

        /// <summary>
        /// Gets an enumerable collection of membership updates.
        /// </summary>
        /// <value>The membership updates.</value>
        IAsyncEnumerable<ClusterMembershipSnapshot> MembershipUpdates { get; }

        /// <summary>
        /// Refreshes cluster membership if it is not at or above the specified minimum version.
        /// </summary>
        /// <param name="minimumVersion">The minimum version.</param>
        /// <returns>A <see cref="ValueTask"/> representing the work performed.</returns>
        ValueTask Refresh(MembershipVersion minimumVersion = default);

        /// <summary>
        /// Unilaterally declares the specified silo defunct.
        /// </summary>
        /// <param name="siloAddress">The silo address which is being declared defunct.</param>
        /// <returns><see langword="true"/> if the silo has been evicted, <see langword="false"/> otherwise.</returns>
        Task<bool> TryKill(SiloAddress siloAddress);
    }
}
