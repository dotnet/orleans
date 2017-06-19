using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Placement;
using Orleans.Runtime;
using Orleans.Runtime.Placement;

namespace UnitTests.GrainInterfaces
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class VersionAwareStrategyAttribute : PlacementAttribute
    {
        public VersionAwareStrategyAttribute()
            : base(VersionAwarePlacementStrategy.Singleton)
        {
        }
    }

    [Serializable]
    public class VersionAwarePlacementStrategy : PlacementStrategy
    {
        internal static VersionAwarePlacementStrategy Singleton { get; } = new VersionAwarePlacementStrategy();

        private VersionAwarePlacementStrategy()
        { }

        public override bool Equals(object obj)
        {
            return obj is VersionAwarePlacementStrategy;
        }

        public override int GetHashCode()
        {
            return GetType().GetHashCode();
        }
    }

    public class VersionAwarePlacementDirector : IPlacementDirector<VersionAwarePlacementStrategy>
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
