using Orleans.Runtime;
using Orleans.Runtime.Placement;

namespace Orleans.Placement;

public interface IPlacementFilterDirector
{
    SiloAddress[] Filter(PlacementFilterStrategy filterStrategy, PlacementTarget target, SiloAddress[] silos);
}
