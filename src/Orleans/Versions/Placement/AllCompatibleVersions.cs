using System;

namespace Orleans.Versions.Placement
{
    [Serializable]
    public class AllCompatibleVersions : VersionPlacementStrategy
    {
        internal static AllCompatibleVersions Singleton { get; } = new AllCompatibleVersions();

        private AllCompatibleVersions()
        { }

        public override bool Equals(object obj)
        {
            return obj is AllCompatibleVersions;
        }

        public override int GetHashCode()
        {
            return GetType().GetHashCode();
        }
    }
}