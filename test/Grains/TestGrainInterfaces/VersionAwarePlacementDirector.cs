using System;
using Orleans.Placement;
using Orleans.Runtime;

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
    [Orleans.GenerateSerializer]
    public class VersionAwarePlacementStrategy : PlacementStrategy
    {
        internal static VersionAwarePlacementStrategy Singleton { get; } = new VersionAwarePlacementStrategy();
    }
}
