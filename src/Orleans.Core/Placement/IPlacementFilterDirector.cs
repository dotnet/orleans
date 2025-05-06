using System.Collections.Generic;
using Orleans.Runtime;
using Orleans.Runtime.Placement;

#nullable enable
namespace Orleans.Placement;

public interface IPlacementFilterDirector
{
}


/// <summary>
/// Does not have access to the request context data, but has the ability for filtering to be cached in some cases.
/// </summary>
public interface IPlacementFilterDirectorWithoutRequestContext : IPlacementFilterDirector
{
    IEnumerable<SiloAddress> Filter(PlacementFilterStrategy filterStrategy, PlacementFilterContext context, IEnumerable<SiloAddress> silos);
}

/// <summary>
/// Has access to the request context data, but does not have the ability for filtering to be cached. Filtering logic must be run on every activation.
/// </summary>
public interface IPlacementFilterDirectorWithRequestContext : IPlacementFilterDirector
{
    IEnumerable<SiloAddress> Filter(PlacementFilterStrategy filterStrategy, PlacementTarget target, IEnumerable<SiloAddress> silos);
}

