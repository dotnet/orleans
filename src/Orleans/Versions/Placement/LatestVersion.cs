using System;

namespace Orleans.Versions.Placement
{
    [Serializable]
    public class LatestVersionPlacement : VersionPlacementStrategy
    {
        internal static LatestVersionPlacement Singleton { get; } = new LatestVersionPlacement();

        private LatestVersionPlacement()
        { }

        public override bool Equals(object obj)
        {
            return obj is LatestVersionPlacement;
        }

        public override int GetHashCode()
        {
            return GetType().GetHashCode();
        }
    }
}