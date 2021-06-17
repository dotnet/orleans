using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Internal;

namespace Orleans.Runtime.Placement
{
    internal class StatelessWorkerDirector : IPlacementDirector
    {
        public Task<SiloAddress> OnAddActivation(PlacementStrategy strategy, PlacementTarget target, IPlacementContext context)
        {
            var compatibleSilos = context.GetCompatibleSilos(target);

            // If the current silo is not shutting down, place locally if we are compatible
            if (!context.LocalSiloStatus.IsTerminating())
            {
                foreach (var silo in compatibleSilos)
                {
                    if (silo.Equals(context.LocalSilo))
                    {
                        return Task.FromResult(context.LocalSilo);
                    }
                }
            }

            // otherwise, place somewhere else
            return Task.FromResult(compatibleSilos[ThreadSafeRandom.Next(compatibleSilos.Length)]);
        }

        internal static ActivationData PickRandom(List<ActivationData> local) => local[local.Count == 1 ? 0 : ThreadSafeRandom.Next(local.Count)];
    }
}
