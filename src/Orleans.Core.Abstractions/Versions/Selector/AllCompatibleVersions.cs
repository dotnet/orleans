using System;

namespace Orleans.Versions.Selector
{
    [Serializable]
    public class AllCompatibleVersions : VersionSelectorStrategy
    {
        public static AllCompatibleVersions Singleton { get; } = new AllCompatibleVersions();

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