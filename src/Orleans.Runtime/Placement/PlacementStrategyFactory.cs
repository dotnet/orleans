using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System;
using Orleans.Configuration;

namespace Orleans.Runtime.Placement
{
    internal static class PlacementStrategyFactory
    {
        public static PlacementStrategy Create(IServiceProvider services)
        {
            IOptions<GrainPlacementOptions> grainPlacementOptions = services.GetRequiredService<IOptions<GrainPlacementOptions>>();
            return services.GetRequiredServiceByName<PlacementStrategy>(grainPlacementOptions.Value.DefaultPlacementStrategy);
        }
    }
}
