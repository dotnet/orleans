using System;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Runtime.Placement;

/// <summary>
/// Resolves directors for cluster placement strategies.
/// </summary>
public sealed class ClusterPlacementDirectorResolver(IServiceProvider services)
{
    /// <summary>
    /// Resolves the director for the provided strategy.
    /// </summary>
    public IClusterPlacementDirector Resolve(ClusterPlacementStrategy strategy)
        => services.GetRequiredKeyedService<IClusterPlacementDirector>(strategy.GetType());
}
