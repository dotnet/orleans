using System.Collections.Generic;

namespace Orleans.Runtime.Placement.Filtering;

public interface IPlacementFilterDirector
{
    IEnumerable<SiloAddress> Filter(PlacementFilterStrategy filterStrategy, PlacementTarget target,
        IEnumerable<SiloAddress> silos);
}