using System;

namespace Orleans.Versions.Selector
{
    [Serializable]
    public class LatestVersion : VersionSelectorStrategy
    {
        public static LatestVersion Singleton { get; } = new LatestVersion();

        private LatestVersion()
        { }

        public override bool Equals(object obj)
        {
            return obj is LatestVersion;
        }

        public override int GetHashCode()
        {
            return GetType().GetHashCode();
        }
    }
}