using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Runtime.Placement;

namespace Orleans.Placement;

/// <summary>
/// Filters the silos which are candidates for a grain placement operation.
/// </summary>
/// <remarks>
/// Directors are registered as singletons and must support concurrent placement operations.
/// </remarks>
public interface IPlacementFilterDirector
{
    /// <summary>
    /// Filters the candidate silos for the specified placement target.
    /// </summary>
    /// <param name="filterStrategy">The placement filter strategy to apply.</param>
    /// <param name="target">The grain and request context for the placement operation.</param>
    /// <param name="silos">The candidate silos to filter. The collection must not be modified.</param>
    /// <param name="cancellationToken">A token which cancels the placement operation.</param>
    /// <returns>A materialized subset of the candidate silos which satisfies the placement filter.</returns>
    /// <remarks>
    /// The result must contain only candidates from <paramref name="silos"/> and must not be modified
    /// after completion. A filter can return <paramref name="silos"/> unchanged.
    /// </remarks>
    Task<IReadOnlyList<SiloAddress>> FilterAsync(
        PlacementFilterStrategy filterStrategy,
        PlacementTarget target,
        IReadOnlyList<SiloAddress> silos,
        CancellationToken cancellationToken = default);
}
