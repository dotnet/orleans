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

        /// <summary>
        /// Initializes a new instance of the <see cref="PlacementDirectorResolver"/> class.
        /// </summary>
        /// <param name="services">The service provider used to resolve placement directors.</param>
        public PlacementDirectorResolver(IServiceProvider services)
        {
            _services = services;
        }

        /// <summary>
        /// Gets the placement director associated with the provided placement strategy.
        /// </summary>
        /// <param name="placementStrategy">The placement strategy.</param>
        /// <returns>The placement director associated with <paramref name="placementStrategy"/>.</returns>
        public IPlacementDirector GetPlacementDirector(PlacementStrategy placementStrategy) => _services.GetRequiredKeyedService<IPlacementDirector>(placementStrategy.GetType());
    }
}
