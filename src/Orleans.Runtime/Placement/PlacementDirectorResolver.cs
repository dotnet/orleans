using System;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Runtime.Placement
{
    /// <summary>
    /// Responsible for resolving an <see cref="IPlacementDirector"/> for a <see cref="PlacementStrategy"/>.
    /// </summary>
    public sealed class PlacementDirectorResolver
    {
        private readonly IServiceProvider _services;

        public PlacementDirectorResolver(IServiceProvider services)
        {
            _services = services;
        }

        public IPlacementDirector GetPlacementDirector(PlacementStrategy placementStrategy) => _services.GetRequiredKeyedService<IPlacementDirector>(placementStrategy.GetType());
    }
}
