using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Placement;

namespace Orleans.Runtime.Placement.Filtering;

/// <summary>
/// Responsible for resolving an <see cref="IPlacementFilterDirector"/> for a <see cref="PlacementFilterStrategy"/>.
/// </summary>
/// <param name="services">The service provider used to resolve placement filter directors.</param>
public sealed class PlacementFilterDirectorResolver(IServiceProvider services)
{
    /// <summary>
    /// Gets the placement filter director associated with the provided placement filter strategy.
    /// </summary>
    /// <param name="placementFilterStrategy">The placement filter strategy.</param>
    /// <returns>The placement filter director associated with <paramref name="placementFilterStrategy"/>.</returns>
    public IPlacementFilterDirector GetFilterDirector(PlacementFilterStrategy placementFilterStrategy) => services.GetRequiredKeyedService<IPlacementFilterDirector>(placementFilterStrategy.GetType());
}
