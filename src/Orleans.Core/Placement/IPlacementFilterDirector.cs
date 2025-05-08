using System.Collections.Generic;
using Orleans.Runtime;
using Orleans.Runtime.Placement;

#nullable enable
namespace Orleans.Placement;

public interface IPlacementFilterDirector
{
    IEnumerable<SiloAddress> Filter(PlacementFilterStrategy filterStrategy, PlacementTarget target, IEnumerable<SiloAddress> silos);
}
