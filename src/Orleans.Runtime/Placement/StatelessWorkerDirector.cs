using System;
using System.Threading.Tasks;

namespace Orleans.Runtime.Placement
{
    internal class StatelessWorkerDirector : IPlacementDirector
    {
        public async Task<SiloAddress> OnAddActivation(PlacementStrategy strategy, PlacementTarget target, IPlacementContext context)
        {
            var compatibleSilos = await context.GetCompatibleSilosAsync(target);

            // If a valid placement hint was specified, use it.
            if (IPlacementDirector.GetPlacementHint(target.RequestContextData, compatibleSilos) is { } placementHint)
            {
                return placementHint;
            }

            // If the current silo is not shutting down, place locally if we are compatible
            if (!context.LocalSiloStatus.IsTerminating())
            {
                foreach (var silo in compatibleSilos)
                {
                    if (silo.Equals(context.LocalSilo))
                    {
                        return context.LocalSilo;
                    }
                }
            }

            // otherwise, place somewhere else
            return compatibleSilos[Random.Shared.Next(compatibleSilos.Length)];
        }
    }
}
