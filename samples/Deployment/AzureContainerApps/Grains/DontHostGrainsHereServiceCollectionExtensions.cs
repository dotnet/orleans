using Grains;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Runtime.Placement;

namespace Orleans.Hosting
{
    public static class DontHostGrainsHereServiceCollectionExtensions
    {
        public static IServiceCollection DontHostGrainsHere(this IServiceCollection services)
        {
            services.AddPlacementDirector<DontPlaceMeOnTheDashboardStrategy, DontPlaceMeOnTheDashboardSiloDirector>();

            return services;
        }
    }
}
