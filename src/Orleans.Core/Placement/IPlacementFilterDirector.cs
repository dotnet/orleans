using System.Collections.Generic;
using Orleans.Runtime;
using Orleans.Runtime.Placement;

namespace Orleans.Placement;

/// <summary>
/// Filters the silos which are candidates for a grain placement operation.
/// </summary>
public interface IPlacementFilterDirector
{
    /// <summary>
    /// Filters the candidate silos for the specified placement target.
    /// </summary>
    /// <param name="filterStrategy">The placement filter strategy to apply.</param>
    /// <param name="target">The grain and request context for the placement operation.</param>
    /// <param name="silos">The candidate silos to filter.</param>
    /// <returns>The silos which satisfy the placement filter.</returns>
    IEnumerable<SiloAddress> Filter(PlacementFilterStrategy filterStrategy, PlacementTarget target, IEnumerable<SiloAddress> silos);
}
