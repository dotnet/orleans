using System;

namespace Orleans.Versions.Placement
{
    [Serializable]
    public class MinimumVersionPlacement : VersionPlacementStrategy
    {
        internal static MinimumVersionPlacement Singleton { get; } = new MinimumVersionPlacement();

        private MinimumVersionPlacement()
        { }

        public override bool Equals(object obj)
        {
            return obj is MinimumVersionPlacement;
        }

        public override int GetHashCode()
        {
            return GetType().GetHashCode();
        }
    }
}