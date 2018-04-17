using Orleans.Runtime;
using Orleans.Runtime.Placement;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace UnitTests.Grains
{
    public class VersionAwarePlacementDirector : IPlacementDirector
    {
        private readonly Random random = new Random();

        public Task<SiloAddress> OnAddActivation(PlacementStrategy strategy, PlacementTarget target, IPlacementContext context)
        {
            IReadOnlyList<SiloAddress> silos;
            if (target.InterfaceVersion == 0)
            {
                silos = (IReadOnlyList<SiloAddress>)context.GetCompatibleSilos(target);
            }
            else
            {
                var silosByVersion = context.GetCompatibleSilosWithVersions(target);
                var maxSiloCount = 0;
                ushort version = 0;
                foreach (var kvp in silosByVersion)
                {
                    if (kvp.Value.Count > maxSiloCount)
                    {
                        version = kvp.Key;
                        maxSiloCount = kvp.Value.Count;
                    }
                }
                silos = silosByVersion[version];
            }

            return Task.FromResult(silos[random.Next(silos.Count)]);
        }
    }

}
