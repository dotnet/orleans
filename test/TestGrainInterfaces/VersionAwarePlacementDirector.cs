using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
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
}
